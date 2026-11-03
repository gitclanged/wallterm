using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WallTerm.Rendering;
using WallTerm.Vt;

namespace WallTerm;

/// <summary>
/// Borderless, desktop-sized tool window owned by the desktop (Progman) and pinned to the bottom of the z-order. Windows
/// keeps an owned window directly above its owner, so it sits above the wallpaper, below every other window, and moves
/// with the desktop when Win+D / "Show desktop" raises it. It takes keyboard focus when clicked and can be summoned
/// above everything with the global hotkey.
/// </summary>
internal sealed class MainWindow : Window
{
    private const int HotkeyId = 0x5754;

    private WallTermConfig _config;
    private readonly VtTerminal _term;
    private readonly TerminalView _view;
    private readonly BurnInGuard _burnIn;
    private readonly ConfigWatcher _watcher;
    private readonly Native.WinEventProc _winEventProc;
    private ConPty? _pty;
    private bool _shellExited;
    private string _hotkey = "";

    private IntPtr _hwnd;
    private bool _raised;          // lifted above other windows by the hotkey until it loses focus
    private IntPtr _desktop;       // Progman, our owner
    private readonly uint _taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
    private IntPtr _previousForeground;
    private IntPtr _winEventHook, _powerNotify;
    private bool _displayOn = true, _locked;
    private int _frameQueued;
    private bool _closing;

    public MainWindow(WallTermConfig config)
    {
        _config = config;
        Title = "WallTerm";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Left = 0;
        Top = 0;
        Width = 800;
        Height = 600;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        RenderOptions.SetClearTypeHint(this, ClearTypeHint.Enabled);

        _term = new VtTerminal(80, 24, config.Scrollback);
        _term.Respond = s => _pty?.Write(s);
        _view = new TerminalView(_term);
        _view.Input += OnViewInput;
        _view.GridResized += (cols, rows) => _pty?.Resize(cols, rows);
        Content = _view;
        _burnIn = new BurnInGuard(_view);
        _winEventProc = OnForegroundChanged;

        ApplyConfig(config);

        _watcher = new ConfigWatcher();
        _watcher.Changed += cfg => Dispatcher.BeginInvoke(() => ApplyConfig(cfg));

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            FitToMonitor();
            StartShell();
            TakeFocusIfDesktopIsForeground();
        };
        // Window level, so clicks on the padding and on empty cells count too.
        PreviewMouseDown += (_, _) => ActivateTerminal();
        // Under the mouse we are an ordinary window, so a click activates us without touching the desktop's
        // activation state (see BeginActivation); elsewhere we go back to being part of the desktop.
        MouseEnter += (_, _) => { if (Native.GetForegroundWindow() != _hwnd) BeginActivation(); };
        MouseLeave += (_, _) => { if (Native.GetForegroundWindow() != _hwnd) EndActivation(); };
        Activated += (_, _) => OnFocusChanged(true);
        Deactivated += (_, _) => OnFocusChanged(false);
        Closing += (_, _) => _closing = true;
        Closed += OnClosed;
    }

    // ───────────── Configuration (hot reload) ─────────────

    private void ApplyConfig(WallTermConfig config)
    {
        _config = config;
        var theme = Theme.From(config);
        _term.DefaultForeground = theme.Foreground;
        _term.DefaultBackground = theme.Background;

        uint bg = theme.WindowBackground;
        var brush = new SolidColorBrush(Color.FromArgb((byte)(bg >> 24), (byte)(bg >> 16), (byte)(bg >> 8), (byte)bg));
        brush.Freeze();
        Background = brush;

        var p = config.Padding;
        _view.Margin = new Thickness(p.Left, p.Top, p.Right, p.Bottom);
        lock (_term.Sync) _term.SetScrollbackCapacity(config.Scrollback);
        _view.Apply(config, theme);
        _burnIn.Configure(config.Wiggle, config.WiggleMinSeconds, config.WiggleMaxSeconds, config.WigglePixels);

        if (_hwnd != IntPtr.Zero)
        {
            ApplyBackdrop();
            RegisterHotkey();
        }
    }

    private void ApplyBackdrop()
    {
        int type = _config.BackdropEffect switch
        {
            "Mica" => Native.DWMSBT_MAINWINDOW,
            "Acrylic" => Native.DWMSBT_TRANSIENTWINDOW,
            _ => Native.DWMSBT_NONE,
        };
        Native.SetDwmInt(_hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, type);
    }

    private void RegisterHotkey()
    {
        if (_hotkey == _config.Hotkey) return;
        Native.UnregisterHotKey(_hwnd, HotkeyId);
        _hotkey = _config.Hotkey;
        if (Hotkey.TryParse(_hotkey, out var mods, out var vk))
            Native.RegisterHotKey(_hwnd, HotkeyId, mods | Native.MOD_NOREPEAT, vk);
    }

    // ───────────── Window setup ─────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_hwnd)!;
        source.AddHook(WndProc);

        // Per-pixel alpha through DWM (no layered window): WPF clears to transparent and DWM composes the
        // semi-transparent background over the wallpaper, still fully hardware accelerated.
        source.CompositionTarget!.BackgroundColor = Colors.Transparent;
        var margins = new Native.MARGINS(-1);
        Native.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
        Native.SetDwmInt(_hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        Native.SetDwmInt(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_DONOTROUND);
        Native.SetDwmInt(_hwnd, Native.DWMWA_BORDER_COLOR, Native.DWMWA_COLOR_NONE);
        Native.SetDwmInt(_hwnd, Native.DWMWA_TRANSITIONS_FORCEDISABLED, 1);

        // Hidden from Alt+Tab and the taskbar, and never activated by Windows on its own (see BeginActivation).
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex = (ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE) & ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

        AttachToDesktop();
        ApplyBackdrop();
        RegisterHotkey();
        _powerNotify = Native.RegisterPowerSettingNotification(_hwnd, ref Native.GUID_CONSOLE_DISPLAY_STATE, 0);
        Native.WTSRegisterSessionNotification(_hwnd, 0);
        _winEventHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _winEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        FitToMonitor();
    }

    /// <summary>Covers the primary monitor's work area (the whole screen with an auto-hidden taskbar).</summary>
    private void FitToMonitor()
    {
        if (_hwnd == IntPtr.Zero) return;
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(Native.MonitorFromPoint(default, Native.MONITOR_DEFAULTTOPRIMARY), ref mi)) return;
        var r = mi.rcWork;
        if (r.Equals(mi.rcMonitor))
        {
            // Auto-hide taskbar: leave one physical pixel on the taskbar's edge so Explorer never classifies this
            // window as a full-screen app (which would stop the hidden taskbar from sliding in).
            var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>() };
            if (Native.SHAppBarMessage(Native.ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero)
            {
                switch (abd.uEdge)
                {
                    case Native.ABE_LEFT: r.Left++; break;
                    case Native.ABE_TOP: r.Top++; break;
                    case Native.ABE_RIGHT: r.Right--; break;
                    default: r.Bottom--; break;
                }
            }
        }
        SetPosInZOrder(_raised ? Native.HWND_TOP : SlotAboveDesktop(), r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    /// <summary>
    /// SetWindowPos that tolerates insertAfter being ourselves (already in the slot): Windows silently ignores the whole
    /// call in that case, size and position included, so drop the z-order change instead.
    /// </summary>
    private void SetPosInZOrder(IntPtr insertAfter, int x, int y, int cx, int cy, uint flags)
    {
        if (insertAfter == _hwnd) flags |= Native.SWP_NOZORDER;
        Native.SetWindowPos(_hwnd, insertAfter, x, y, cx, cy, flags);
    }

    /// <summary>
    /// Makes Explorer's desktop window our owner. Show Desktop (Win+D, the taskbar corner, Win+M) raises the desktop
    /// above every non-minimizable window; as an owned window we are raised with it instead of disappearing behind it.
    /// </summary>
    private void AttachToDesktop()
    {
        var desktop = Native.FindWindow("Progman", null);
        if (desktop == IntPtr.Zero || desktop == _desktop) return;
        _desktop = desktop;
        if (Native.GetForegroundWindow() != _hwnd) EndActivation();
    }

    /// <summary>
    /// Called before we may take focus (mouse over us, hotkey, startup). While unfocused we are WS_EX_NOACTIVATE
    /// and owned by the desktop. Both have to go first: Windows refuses to make a NOACTIVATE window foreground, and an
    /// activation while owned makes us the desktop's "last active popup", after which Windows forwards the desktop's
    /// activation on Win+D to us, cancelling Win+D's "restore windows" toggle.
    /// </summary>
    private void BeginActivation()
    {
        SetExStyle(Native.WS_EX_NOACTIVATE, false);
        if (Native.GetWindowLongPtr(_hwnd, Native.GWLP_HWNDPARENT) != IntPtr.Zero)
            Native.SetWindowLongPtr(_hwnd, Native.GWLP_HWNDPARENT, IntPtr.Zero);
    }

    /// <summary>Lost focus (or never got it): back to being a non-activatable window owned by the desktop.</summary>
    private void EndActivation()
    {
        SetExStyle(Native.WS_EX_NOACTIVATE, true);
        if (_desktop != IntPtr.Zero && Native.GetWindowLongPtr(_hwnd, Native.GWLP_HWNDPARENT) != _desktop)
            Native.SetWindowLongPtr(_hwnd, Native.GWLP_HWNDPARENT, _desktop);
        if (!_raised) PinToBottom();
    }

    private void SetExStyle(long bits, bool on)
    {
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        long next = on ? ex | bits : ex & ~bits;
        if (next != ex) Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(next));
    }

    private void PinToBottom() =>
        SetPosInZOrder(SlotAboveDesktop(), 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

    /// <summary>
    /// The hwndInsertAfter that puts us directly above the desktop window: the window currently just above it (or
    /// ourselves when we are already there), HWND_TOP when the desktop is at the top of its band (Show Desktop).
    /// </summary>
    private IntPtr SlotAboveDesktop()
    {
        var desktop = _desktop != IntPtr.Zero ? _desktop : Native.FindWindow("Progman", null);
        if (desktop == IntPtr.Zero) return Native.HWND_BOTTOM;
        var prev = Native.GetWindow(desktop, Native.GW_HWNDPREV);
        if (prev == IntPtr.Zero || (Native.GetWindowLongPtr(prev, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_TOPMOST) != 0)
            return Native.HWND_TOP;
        return prev;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_WINDOWPOSCHANGING:
            {
                var wp = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
                bool modified = false;
                if (!_raised && (wp.flags & Native.SWP_NOZORDER) == 0)
                {
                    // Not HWND_BOTTOM: that is below the desktop window (owned or not), which then paints the
                    // wallpaper over us until something re-sorts the z-order — a blank flash on clicks and app switches.
                    var after = SlotAboveDesktop();
                    if (after == _hwnd) wp.flags |= Native.SWP_NOZORDER;
                    else wp.hwndInsertAfter = after;
                    modified = true;
                }
                if (!_closing && (wp.flags & Native.SWP_HIDEWINDOW) != 0)
                {
                    wp.flags &= ~Native.SWP_HIDEWINDOW;
                    modified = true;
                }
                if (modified) Marshal.StructureToPtr(wp, lParam, false);
                break;
            }

            case Native.WM_SYSCOMMAND:
            {
                int sc = wParam.ToInt32() & 0xFFF0;
                if (sc is Native.SC_MINIMIZE or Native.SC_MAXIMIZE or Native.SC_CLOSE or Native.SC_MOVE or Native.SC_SIZE or Native.SC_KEYMENU)
                {
                    handled = true;
                    return IntPtr.Zero;
                }
                break;
            }

            case Native.WM_ACTIVATE:
                switch (wParam.ToInt32() & 0xFFFF)
                {
                    case Native.WA_ACTIVE when (Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_NOACTIVATE) != 0:
                        // Activated without a click and without BeginActivation: Explorer forwarding the desktop's
                        // activation on Win+D to its last active owned window. Give it straight back to the desktop,
                        // otherwise the next Win+D shows the desktop again instead of restoring the windows.
                        if (_desktop != IntPtr.Zero) Native.SetForegroundWindow(_desktop);
                        break;
                    case Native.WA_INACTIVE:
                        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                        {
                            if (Native.GetForegroundWindow() != _hwnd) EndActivation();
                        });
                        break;
                }
                break;

            case Native.WM_HOTKEY:
                if (wParam.ToInt32() == HotkeyId)
                {
                    ToggleSummon();
                    handled = true;
                }
                break;

            case Native.WM_POWERBROADCAST:
                if (wParam.ToInt32() == Native.PBT_POWERSETTINGCHANGE)
                {
                    var guid = Marshal.PtrToStructure<Guid>(lParam);
                    if (guid == Native.GUID_CONSOLE_DISPLAY_STATE)
                    {
                        // POWERBROADCAST_SETTING: Guid (16) + DataLength (4) + Data. 0 = off, 1 = on, 2 = dimmed.
                        _displayOn = Marshal.ReadInt32(lParam, 20) != 0;
                        UpdatePowerState();
                    }
                }
                break;

            case Native.WM_WTSSESSION_CHANGE:
                if (wParam.ToInt32() == Native.WTS_SESSION_LOCK)
                {
                    _locked = true;
                    UpdatePowerState();
                }
                else if (wParam.ToInt32() == Native.WTS_SESSION_UNLOCK)
                {
                    _locked = false;
                    UpdatePowerState();
                    FitToMonitor();
                    TakeFocusIfDesktopIsForeground();
                }
                break;

            case var m when m == _taskbarCreatedMsg && m != 0:
                // Explorer restarted: the old desktop window (our owner) is gone.
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    _desktop = IntPtr.Zero;
                    AttachToDesktop();
                    FitToMonitor();
                });
                break;

            case Native.WM_DISPLAYCHANGE:
            case Native.WM_DPICHANGED:
                Dispatcher.BeginInvoke(DispatcherPriority.Background, FitToMonitor);
                break;

            case Native.WM_SETTINGCHANGE:
                if (wParam.ToInt32() == Native.SPI_SETWORKAREA)
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, FitToMonitor);
                break;
        }
        return IntPtr.Zero;
    }

    // ───────────── Focus, hotkey, Show Desktop ─────────────

    /// <summary>The user clicked the terminal: take the keyboard.</summary>
    private void ActivateTerminal()
    {
        if (Native.GetForegroundWindow() != _hwnd)
        {
            BeginActivation();
            if (!Native.SetForegroundWindow(_hwnd))
            {
                EndActivation();
                return;
            }
        }
        Keyboard.Focus(_view);
    }

    /// <summary>Hotkey / second launch: bring the terminal above everything with focus; press again to send it back.</summary>
    private void ToggleSummon()
    {
        if (_raised && Native.GetForegroundWindow() == _hwnd)
        {
            Dismiss();
            return;
        }
        Summon();
    }

    public void Summon()
    {
        var fg = Native.GetForegroundWindow();
        if (fg != _hwnd) _previousForeground = fg;
        _raised = true;
        BeginActivation();
        Native.SetWindowPos(_hwnd, Native.HWND_TOP, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);
        if (!Native.SetForegroundWindow(_hwnd) || Native.GetForegroundWindow() != _hwnd)
        {
            // Windows' foreground lock refused activation: never leave an unfocused overlay covering other apps.
            _raised = false;
            EndActivation();
            return;
        }
        Keyboard.Focus(_view);
    }

    private void Dismiss()
    {
        _raised = false;
        PinToBottom();
        var prev = _previousForeground;
        if (prev != IntPtr.Zero && Native.IsWindow(prev) && Native.IsWindowVisible(prev) && !Native.IsIconic(prev))
            Native.SetForegroundWindow(prev);
    }

    private void OnFocusChanged(bool focused)
    {
        _view.SetFocused(focused);
        if (focused) Keyboard.Focus(_view);
        bool report;
        lock (_term.Sync) report = _term.FocusReporting;
        if (report) _pty?.Write(focused ? "\x1b[I" : "\x1b[O");
        if (!focused && _raised)
        {
            _raised = false;
            PinToBottom();
        }
        SetEcoQoS(!focused);
    }

    /// <summary>
    /// The desktop came forward (Show Desktop, or the last window closed or minimized): re-attach so we come up with it,
    /// then take the keyboard. Another app took the foreground while we were summoned: go back to being the wallpaper.
    /// </summary>
    private void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || hwnd == _hwnd) return;
        if (IsDesktopWindow(hwnd))
        {
            EndActivation();
            // Nothing is in front of the wallpaper any more: take the keyboard so typing goes straight to the shell.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, TakeFocusIfDesktopIsForeground);
            return;
        }
        if (!_raised) return;
        _raised = false;
        PinToBottom();
    }

    private static bool IsDesktopWindow(IntPtr hwnd) => Native.GetClassName(hwnd) is "WorkerW" or "Progman";

    /// <summary>At startup / unlock / Show Desktop, take the keyboard if nothing else has it, so typing works immediately.</summary>
    private void TakeFocusIfDesktopIsForeground()
    {
        var fg = Native.GetForegroundWindow();
        if (_locked || fg == _hwnd) return;
        if (fg == IntPtr.Zero || IsDesktopWindow(fg) || Native.GetClassName(fg) == "Shell_TrayWnd")
        {
            BeginActivation();
            if (Native.SetForegroundWindow(_hwnd)) Keyboard.Focus(_view);
            else EndActivation();
        }
    }

    private void UpdatePowerState()
    {
        bool active = _displayOn && !_locked;
        _view.SetPowerActive(active);
        _burnIn.SetPaused(!active);
    }

    /// <summary>EcoQoS while in the background: the scheduler keeps WallTerm's threads on efficient cores at low clocks.</summary>
    private static void SetEcoQoS(bool enabled)
    {
        var state = new Native.PROCESS_POWER_THROTTLING_STATE
        {
            Version = 1,
            ControlMask = Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = enabled ? Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
        };
        Native.SetProcessInformation(Native.GetCurrentProcess(), Native.ProcessPowerThrottling, ref state, Marshal.SizeOf(state));
    }

    // ───────────── Shell session ─────────────

    private void StartShell()
    {
        _shellExited = false;
        string commandLine, cwd;
        try
        {
            commandLine = ShellCommand.Build(_config, out cwd);
            _pty = new ConPty(commandLine, cwd, _view.Cols, _view.Rows, OnPtyOutput, OnPtyExited);
        }
        catch (Exception ex)
        {
            _pty = null;
            _shellExited = true;
            WriteLocal($"\x1b[91mwallterm: {ex.Message}\x1b[0m\r\n\x1b[90m[press Enter to retry]\x1b[0m\r\n");
        }
    }

    private void OnPtyOutput(ReadOnlySpan<char> text)
    {
        lock (_term.Sync) _term.Feed(text);
        RequestFrame();
    }

    /// <summary>Coalesces any amount of output into at most one render per display frame.</summary>
    private void RequestFrame()
    {
        if (Interlocked.Exchange(ref _frameQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => CompositionTarget.Rendering += OnFrame);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnFrame;
        Volatile.Write(ref _frameQueued, 0);
        _view.Render();
    }

    private void OnPtyExited(ConPty pty, int exitCode)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(pty, _pty)) return;
            _pty = null;
            ThreadPool.QueueUserWorkItem(_ => pty.Dispose());
            _shellExited = true;
            WriteLocal($"\r\n\x1b[0m\x1b[90m[process exited with code {exitCode} — press Enter to start a new session]\x1b[0m");
        });
    }

    private void WriteLocal(string text)
    {
        lock (_term.Sync) _term.Feed(text);
        RequestFrame();
    }

    private void OnViewInput(string text)
    {
        if (_shellExited)
        {
            if (!text.Contains('\r')) return;
            lock (_term.Sync)
            {
                _term.FullReset();
                _term.Feed("\x1b[2J\x1b[H");
            }
            StartShell();
            RequestFrame();
            return;
        }
        _pty?.Write(text);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _watcher.Dispose();
        if (_winEventHook != IntPtr.Zero) Native.UnhookWinEvent(_winEventHook);
        if (_powerNotify != IntPtr.Zero) Native.UnregisterPowerSettingNotification(_powerNotify);
        Native.WTSUnRegisterSessionNotification(_hwnd);
        Native.UnregisterHotKey(_hwnd, HotkeyId);
        var pty = _pty;
        _pty = null;
        pty?.Dispose();
    }
}
