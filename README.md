# WallTerm

A live, fully interactive PowerShell terminal as your Windows 11 desktop background. It is built for battery-powered,
high-DPI OLED laptops: a single ~0.5 MB exe, no Electron or WebView, about 0% CPU when idle, and no polling loops.

- **Real ConPTY backend.** WallTerm calls `CreatePseudoConsole` and spawns `pwsh.exe -NoLogo`, falling back to `powershell.exe`. It starts in `%USERPROFILE%`. PSReadLine, tab completion and ANSI colors all work.
- **VT/xterm emulator.** Supports truecolor, 256 and 16 colors, bold/italic/underline/strike, the alternate screen, scroll regions, DEC line drawing, wide CJK/emoji characters, bracketed paste, SGR mouse reporting, focus events, and DSR/DA replies.
- **Part of the desktop.** The window is owned by Explorer's desktop window (`Progman`), so Windows keeps it directly above the desktop.
  - It stays below every other window, and stays out of Alt+Tab and the taskbar.
  - **Show desktop** (Win+D, Win+M, the taskbar's corner button) brings the terminal up together with the desktop instead of hiding it, and gives it the keyboard so you can start typing right away. Pressing Win+D again restores your windows as usual.
- **Click to type.** Click anywhere on the terminal (text, empty space or padding) and it takes the keyboard. Click another window and the terminal goes back to being the wallpaper.
- **Summon hotkey.** **Alt+`** brings the terminal above all windows. Press it again (or click elsewhere) to send it back.
- **Always Administrator.** The manifest specifies `requireAdministrator`.
- **Rendering.** WPF with one `DrawingVisual` per row, re-recording only the rows that changed. Rendering is coalesced to at most one pass per display frame. Transparency is per-pixel through DWM.
- **Battery and OLED.**
  - Cursor blinking stops when unfocused, when the display is off, and when the session is locked.
  - EcoQoS throttling applies while WallTerm is in the background.
  - Optional burn-in "wiggle" shifts the text a few pixels every few minutes.
- **Makes room for your windows.** Snap a window to half or a quarter of the screen and the terminal shrinks into the free part, with the shell re-wrapping its text to fit. It grows back when the window is closed, minimized or moved away. Set `avoid all` to also make room for floating windows.
- **Live config.** Run `wallterm settings` to edit the commented `config.ini` in a terminal editor, or change one value with `wallterm set …`. Changes apply as soon as the file is saved, without restarting.

## Build

Requires the .NET 8 SDK on Windows 10/11. The project builds for the host architecture (`win-arm64` on Snapdragon, `win-x64` on Intel/AMD).

```powershell
dotnet publish -c Release -p:PublishSingleFile=true
# → bin\Release\net8.0-windows\<rid>\publish\WallTerm.exe
```

The default publish is framework-dependent: one small exe that uses the installed .NET 8 Desktop Runtime. For a fully standalone exe (~70 MB), add `--self-contained`.

### Release package

```powershell
powershell -ExecutionPolicy Bypass -File build-release.ps1
# → release\WallTerm-<version>\  and  release\WallTerm-<version>.zip
```

The package holds x64 and ARM64 exes and three double-click scripts: `Install.cmd`, `Install (run at startup).cmd` and `Uninstall.cmd`. They install WallTerm to `C:\Program Files\WallTerm`, add a Start Menu shortcut, add the folder to PATH and register WallTerm in Installed apps. The startup variant also registers the sign-in task. The scripts are in `installer\`.

`dotnet run -c Release` only works from an **elevated** terminal. The manifest requires admin rights, and `dotnet run` cannot show a UAC prompt.

## Run at sign-in

```powershell
WallTerm.exe --install-startup     # Task Scheduler logon task, "run with highest privileges"
WallTerm.exe --uninstall-startup
```

Windows silently refuses to start elevated programs from the `HKCU\…\Run` key. WallTerm therefore registers a Task Scheduler logon task instead, which starts it elevated with no UAC prompt. The task has no battery restrictions and no time limit. The task stores the exe's current path, so copy the exe to its permanent location first.

## Using it

| Action | Keys / mouse |
|---|---|
| Focus the terminal | Click anywhere on it |
| Summon above all windows / send back | **Alt+`** (configurable, see `hotkey`) |
| Bring it forward from anywhere | Run `WallTerm.exe` again (it summons the running instance) |
| Copy | Select with the mouse, then **Ctrl+C**. With no selection, Ctrl+C sends SIGINT. **Ctrl+Shift+C** and right-click also copy |
| Paste | **Ctrl+V**, **Ctrl+Shift+V**, or right-click with no selection |
| Select word / line | Double-click / triple-click |
| Scroll history | Mouse wheel, **Shift+PgUp/PgDn**, **Ctrl+Shift+↑/↓** |
| Zoom (this session only) | **Ctrl+wheel**; **Ctrl+0** resets |
| Mouse selection while an app captures the mouse | Hold **Shift** |
| Close WallTerm | `wallterm quit` (Alt+F4 is deliberately ignored) |

If the shell exits, press Enter to start a new session.

## Settings commands

Inside WallTerm, a `wallterm` command is available in the PowerShell session. The exe accepts the same commands from any other shell. Every `set` is saved to `config.ini`, and the running terminal applies it immediately. The exceptions are `shell`, `args` and `dir`, which apply to the next shell session.

> **PowerShell and `#`:** an unquoted `#` starts a comment, so `wallterm set color background #000000` arrives without the color. Write `'#000000'` in quotes, or leave the `#` off: `wallterm set color background 000000`.

### Commands

| Command | What it does |
|---|---|
| `wallterm` | Start WallTerm, or summon the running instance |
| `wallterm settings` | Open `config.ini` in a terminal text editor; reports any mistakes when you exit |
| `wallterm set <key> <value>` | Change a setting (keys below) |
| `wallterm set color <name> <hex>` | Change a color (names below) |
| `wallterm get` | Print `config.ini` |
| `wallterm get <key>` | Print one setting, e.g. `wallterm get font` or `wallterm get color red` |
| `wallterm check` | Report invalid values and unknown keys in `config.ini` |
| `wallterm reset` | Restore all default settings (rewrites `config.ini`) |
| `wallterm path` | Print the config file path |
| `wallterm quit` | Close the running instance (also `exit`, `stop`) |
| `wallterm --install-startup` | Start elevated at every sign-in |
| `wallterm --uninstall-startup` | Remove the sign-in task |
| `wallterm --version` | Print the version |
| `wallterm help` | Print the built-in help |

### Starting apps

Inside WallTerm, `run <name>` starts any app listed in the Start menu, including Store apps. Examples: `run waterfox`, `run calc`, `run python`. It looks for an exact name first, then a name that starts with what you typed, then one that contains it. If several match, the shortest name wins. Tab completes app names. `run -List [text]` lists the matching app names.

WallTerm runs as Administrator, but apps launched with `run` start through Explorer, so they run without elevation. (The command is `run` because `start` is PowerShell's built-in alias for `Start-Process`.)

### Settings

| Setting | Values | Default | Example |
|---|---|---|---|
| `font` | Any installed font family | `Cascadia Mono` (falls back to JetBrainsMono NF, then Consolas) | `wallterm set font "JetBrains Mono"` |
| `size` | Points, 4–96 | `14` | `wallterm set size 15` |
| `opacity` | `0`–`1` or `0%`–`100%`; multiplies the background color's alpha | `0.85` | `wallterm set opacity 1` |
| `backdrop` | `none`, `acrylic`, `mica` | `none` | `wallterm set backdrop mica` |
| `padding` | One value (all sides), two (`horizontal,vertical`) or four (`left,top,right,bottom`), in pixels | `48` | `wallterm set padding 32,24` |
| `cursor` | `block`, `beam` (or `bar`), `underline` | `block` | `wallterm set cursor beam` |
| `blink` | `true` / `false` | `true` | `wallterm set blink false` |
| `wiggle` | `true` / `false` — OLED burn-in protection | `false` | `wallterm set wiggle true` |
| `wiggle-min` | Seconds between moves, at least 10 | `180` | `wallterm set wiggle-min 120` |
| `wiggle-max` | Seconds between moves, at least 10 | `300` | `wallterm set wiggle-max 240` |
| `wiggle-pixels` | Maximum offset, 1–8 physical pixels | `3` | `wallterm set wiggle-pixels 2` |
| `scrollback` | Lines of history, 0–200000 | `5000` | `wallterm set scrollback 20000` |
| `shell` | Executable to run | `pwsh.exe` (falls back to `powershell.exe`) | `wallterm set shell powershell.exe` |
| `args` | Arguments for the shell | `-NoLogo` | `wallterm set args "-NoLogo -NoProfile"` |
| `dir` | Start directory | `%USERPROFILE%` | `wallterm set dir C:\Code` |
| `hotkey` | Modifier(s) + key, or `none` | ``Alt+` `` | `wallterm set hotkey Ctrl+Alt+T` |
| `avoid` | `snapped` (windows touching the screen edges), `all` (floating windows too), `off` | `snapped` | `wallterm set avoid all` |
| `editor` | Editor for `wallterm settings`: `auto`, or a program name/path with arguments | `auto` (`$env:EDITOR`, then `edit`, `nvim`, `vim`, `nano`, Notepad) | `wallterm set editor nvim` |
| `background` | Shortcut for `color background` | `#000000` | `wallterm set background 000000` |

The `true`/`false` settings also accept `on`/`off`, `yes`/`no`, `1`/`0` and `enable`/`disable`. `wiggle` also answers to `burnin` and `oled`.

### Colors

```powershell
wallterm set color <name> <RRGGBB | AARRGGBB>
```

| Name | Default | Notes |
|---|---|---|
| `background` (or `bg`) | `#000000` | Pure black. The window background; its alpha is multiplied by `opacity` |
| `foreground` (or `fg`) | `#D8DAE3` | Default text color |
| `cursor` | `#F2F3F7` | |
| `selection` | `#663D4F7A` | Use `AARRGGBB` for a translucent selection |
| `black` / `0` | `#1C1D24` | ANSI palette, normal |
| `red` / `1` | `#FF5F73` | |
| `green` / `2` | `#4FE3A0` | |
| `yellow` / `3` | `#F7D774` | |
| `blue` / `4` | `#5CA8FF` | |
| `magenta` / `5` | `#C792FF` | |
| `cyan` / `6` | `#4FD6E8` | |
| `white` / `7` | `#D8DAE3` | |
| `brightblack` / `8` | `#5C6170` | ANSI palette, bright |
| `brightred` / `9` | `#FF8A99` | |
| `brightgreen` / `10` | `#7FF2BF` | |
| `brightyellow` / `11` | `#FFE6A1` | |
| `brightblue` / `12` | `#8EC3FF` | |
| `brightmagenta` / `13` | `#DDB8FF` | |
| `brightcyan` / `14` | `#8CE8F4` | |
| `brightwhite` / `15` | `#FFFFFF` | |

Names are case-insensitive, and `-`/`_` are ignored, so `bright-blue` works too. Examples:

```powershell
wallterm set color red ff5f73
wallterm set color brightblue '#8EC3FF'
wallterm set color selection 663D4F7A
wallterm get color red
```

On OLED, an opaque pure-black background draws the least power: `wallterm set opacity 1`. Mica and Acrylic look good, but Windows shows them only while the window is focused, and they cost some GPU time.

## config.ini

`%USERPROFILE%\.config\wallterm\config.ini` holds every setting, grouped into sections. Each section starts with a short description and the values it accepts. `wallterm settings` opens it in a terminal editor (Microsoft Edit, which ships with Windows 11, unless `$env:EDITOR` or the `editor` setting says otherwise). Changes apply as soon as the file is saved. A missing or invalid value uses its default, and `wallterm check` lists any mistakes. `wallterm set` only rewrites the values it changes, so your own comments stay. An existing `config.json` from WallTerm 1.1 is converted on first start and kept as `config.json.bak`.

```ini
; Keep the terminal out from under other windows: it shrinks to the largest free part of the screen and the
; shell re-wraps its text to fit.
;   Avoid    Snapped  windows snapped to the screen edges (halves, quarters, Snap layouts)
;            All      snapped and floating windows
;            Off      always cover the whole screen
[Layout]
Avoid = Snapped
```

Sections: `[Font]` (Family, Size), `[Window]` (Opacity, Backdrop, Padding, Hotkey), `[Layout]` (Avoid), `[Cursor]` (Style, Blink), `[Colors]` (Background, Foreground, Cursor, Selection, Black … BrightWhite), `[Shell]` (Program, Arguments, StartDirectory, Scrollback, Editor), `[BurnIn]` (Wiggle, MinSeconds, MaxSeconds, Pixels).

## Making room for other windows

The terminal grid is rectangular, so WallTerm resizes itself to the largest rectangle of the work area that no avoided window overlaps, and ConPTY re-wraps the shell's text to the new width.

- **Snapped** windows are those that span the full height or width of the screen or sit in one of its corners (Snap, Snap layouts, tiling tools). Maximized windows are ignored: they hide the desktop anyway.
- If only slivers are left (for example two windows snapped side by side), the terminal covers the whole screen instead.
- Windows hidden behind the desktop (Show Desktop) don't count, so Win+D brings the terminal up at full size.
- WallTerm only watches window events while `avoid` is not `off`, and it waits for a drag to end before re-laying out.

WallTerm logs errors that would otherwise crash it to `%USERPROFILE%\.config\wallterm\wallterm.log` and keeps running.

## Known limitation

If the terminal itself has keyboard focus when you press Win+D, it stays visible. The next Win+D may not restore your other windows, though; use the taskbar or Alt+Tab instead. Pressing Win+D while any other app has focus works normally.

## How the desktop integration works

- WallTerm is a borderless `WS_EX_TOOLWINDOW`, so it stays out of Alt+Tab and the taskbar. It is pinned to the bottom of the z-order in `WM_WINDOWPOSCHANGING`.
- Its owner is the desktop window (`Progman`). Windows always keeps an owned window above its owner, so when Show Desktop raises the desktop, the terminal rises with it. If Explorer restarts, WallTerm re-attaches when it receives `TaskbarCreated`.
- While unfocused it is also `WS_EX_NOACTIVATE`, so Windows never gives it focus on its own.
  - While the mouse is over it, it temporarily becomes an ordinary unowned window, so a click activates it normally. It reverts when the mouse leaves or it loses focus.
  - This matters because activating a window while it is owned by the desktop makes Explorer forward the desktop's own activation on Win+D to it, which cancels Win+D's "restore windows" toggle.

## Project layout

```
WallTerm.csproj            .NET 8 WPF, single-file/ReadyToRun publish settings
app.manifest               requireAdministrator, PerMonitorV2 DPI
src/Program.cs             entry point: CLI dispatch, elevation, single instance, summon/quit events
src/Cli.cs                 wallterm set/get/reset/quit/… and console output for a GUI-subsystem exe
src/Config.cs              config model, validation, theme, atomic save, debounced FileSystemWatcher
src/IniConfig.cs           config.ini sections, help text, parsing and comment-preserving updates
src/EditorCommand.cs       editor choice for 'wallterm settings'
src/FreeArea.cs            which windows to avoid, largest free rectangle
src/Elevation.cs           admin check and runas relaunch
src/StartupTask.cs         Task Scheduler logon task (install/uninstall)
src/ConPty.cs              pseudo console, process spawn, blocking-read output thread, exit wait
src/ShellCommand.cs        pwsh → powershell resolution, injected `wallterm` function
src/Native.cs              Win32 interop
src/Hotkey.cs              hotkey string parsing
src/MainWindow.cs          desktop ownership, z-order pinning, focus, hotkey, power/session events, frame scheduling
src/Vt/                    terminal model (Line/Cell, VtTerminal) and escape-sequence parser
src/Rendering/             TerminalView renderer, key encoding, burn-in guard
```
