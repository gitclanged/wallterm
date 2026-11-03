using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WallTerm.Vt;

namespace WallTerm.Rendering;

/// <summary>DrawingVisual with text/edge options fixed at the visual level (visual children don't inherit TextOptions).</summary>
internal sealed class TermVisual : DrawingVisual
{
    public TermVisual()
    {
        VisualTextRenderingMode = TextRenderingMode.Auto;
        VisualTextHintingMode = TextHintingMode.Fixed;
        VisualClearTypeHint = ClearTypeHint.Enabled;
        VisualEdgeMode = EdgeMode.Aliased;
    }
}

/// <summary>
/// Retained-mode terminal renderer: one DrawingVisual per row, re-recorded only when that row's Line object or
/// version changed. Text is drawn as GlyphRuns on a device-pixel-snapped cell grid; glyphs missing from the
/// primary font fall back to FormattedText (WPF font fallback). The cursor and selection are separate visuals,
/// so blinking only toggles an opacity.
/// </summary>
internal sealed class TerminalView : FrameworkElement
{
    private readonly VtTerminal _term;
    private readonly VisualCollection _children;
    private TermVisual[] _rows = [];
    private Line?[] _rowLine = [];
    private int[] _rowVersion = [];
    private readonly TermVisual _selectionVisual = new();
    private readonly TermVisual _cursorVisual = new();
    private readonly DispatcherTimer _blinkTimer;

    private Theme _theme = Theme.From(new WallTermConfig().Normalize());
    private string _fontName = "";
    private double _fontSize = 14;
    private FontFamily _fontFamily = new("Consolas");
    private readonly GlyphTypeface[] _faces = new GlyphTypeface[4]; // regular, bold, italic, bold-italic
    private readonly Typeface[] _typefaces = new Typeface[4];
    private double _emSize, _cellW, _cellH, _baseline, _ppd = 1;
    private double _underlineY, _strikeY, _lineThickness;
    private string _cursorStyle = "Block";
    private bool _cursorBlinkConfig = true;
    private bool _focused, _powerActive = true, _forceRedraw = true;
    private double _zoom;

    private readonly Dictionary<uint, SolidColorBrush> _brushes = new();
    private readonly Dictionary<(string, uint, CellFlags), FormattedText> _fallbackCache = new();
    private readonly List<ushort> _glyphs = new(256);
    private readonly List<double> _advances = new(256);

    // Selection in absolute line coordinates (stable while output scrolls).
    private bool _hasSelection, _selecting, _selectionMoved;
    private (long Line, int Col) _selAnchor, _selEnd;
    private (long, int, long, int, int, long)? _selRendered;

    private (int Col, int Row)? _lastMouseCell;
    private int _mouseButtonDown = -1;

    public int Cols { get; private set; }
    public int Rows { get; private set; }

    /// <summary>Text to send to the shell (keystrokes, paste, mouse reports).</summary>
    public event Action<string>? Input;
    public event Action<int, int>? GridResized;

    public TerminalView(VtTerminal term)
    {
        _term = term;
        _children = new VisualCollection(this) { _selectionVisual, _cursorVisual };
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = false;
        Cursor = Cursors.IBeam;
        _blinkTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(530) };
        _blinkTimer.Tick += (_, _) => _cursorVisual.Opacity = _cursorVisual.Opacity > 0 ? 0 : 1;
        UpdateMetrics();
    }

    // ───────────── Configuration ─────────────

    public void Apply(WallTermConfig config, Theme theme)
    {
        _theme = theme;
        _brushes.Clear();
        _fallbackCache.Clear();
        _cursorStyle = config.CursorStyle;
        _cursorBlinkConfig = config.CursorBlink;
        if (config.FontFamily != _fontName || Math.Abs(config.FontSize - _fontSize) > 0.001)
        {
            _fontName = config.FontFamily;
            _fontSize = config.FontSize;
            _zoom = 0;
            UpdateMetrics();
            RecomputeGrid();
        }
        _forceRedraw = true;
        Render();
        UpdateBlink();
    }

    public void SetFocused(bool focused)
    {
        _focused = focused;
        lock (_term.Sync) RenderCursor();
        UpdateBlink();
    }

    /// <summary>False while the display is off or the workstation is locked: stops cursor blinking entirely.</summary>
    public void SetPowerActive(bool active)
    {
        _powerActive = active;
        UpdateBlink();
    }

    private void Zoom(double delta)
    {
        double next = Math.Clamp(_fontSize + _zoom + delta, 4, 96) - _fontSize;
        if (Math.Abs(next - _zoom) < 0.001) return;
        _zoom = next;
        UpdateMetrics();
        RecomputeGrid();
        _forceRedraw = true;
        Render();
    }

    private void ResetZoom()
    {
        if (_zoom == 0) return;
        Zoom(-_zoom);
    }

    // ───────────── Font metrics ─────────────

    private void UpdateMetrics()
    {
        _ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        string[] candidates = [_fontName, "Cascadia Mono", "JetBrainsMono NF", "JetBrains Mono", "Consolas", "Courier New"];
        GlyphTypeface? regular = null;
        foreach (var name in candidates)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var ff = new FontFamily(name);
            regular = TryFace(ff, name, FontWeights.Normal, FontStyles.Normal);
            if (regular == null) continue;
            _fontFamily = ff;
            break;
        }
        regular ??= TryAnyFace(new FontFamily("Consolas"))!;

        _faces[0] = regular;
        _faces[1] = TryFace(_fontFamily, null, FontWeights.Bold, FontStyles.Normal) ?? new GlyphTypeface(regular.FontUri, StyleSimulations.BoldSimulation);
        _faces[2] = TryFace(_fontFamily, null, FontWeights.Normal, FontStyles.Italic) ?? new GlyphTypeface(regular.FontUri, StyleSimulations.ItalicSimulation);
        _faces[3] = TryFace(_fontFamily, null, FontWeights.Bold, FontStyles.Italic) ?? new GlyphTypeface(regular.FontUri, StyleSimulations.BoldItalicSimulation);
        _typefaces[0] = new Typeface(_fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _typefaces[1] = new Typeface(_fontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        _typefaces[2] = new Typeface(_fontFamily, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
        _typefaces[3] = new Typeface(_fontFamily, FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);

        _emSize = (_fontSize + _zoom) * 96.0 / 72.0; // points → DIPs
        double advance = regular.CharacterToGlyphMap.TryGetValue('M', out var gm) ? regular.AdvanceWidths[gm] : 0.6;
        _cellW = SnapUp(advance * _emSize);
        double ascent = regular.Baseline * _emSize;
        double height = regular.Height * _emSize;
        _cellH = SnapUp(height);
        _baseline = Snap(ascent + (_cellH - height) / 2);
        _lineThickness = Math.Max(1 / _ppd, Snap(regular.UnderlineThickness * _emSize));
        _underlineY = Math.Min(_cellH - _lineThickness, Snap(_baseline - regular.UnderlinePosition * _emSize));
        _strikeY = Snap(_baseline - regular.StrikethroughPosition * _emSize);
        _fallbackCache.Clear();
        _forceRedraw = true;
    }

    private static GlyphTypeface? TryFace(FontFamily family, string? requiredName, FontWeight weight, FontStyle style)
    {
        var tf = new Typeface(family, style, weight, FontStretches.Normal);
        if (!tf.TryGetGlyphTypeface(out var gt)) return null;
        if (requiredName != null)
        {
            // WPF silently substitutes a fallback family; make sure the requested font really exists.
            bool match = gt.FamilyNames.Values.Concat(gt.Win32FamilyNames.Values)
                .Any(n => n.Equals(requiredName, StringComparison.OrdinalIgnoreCase));
            if (!match) return null;
        }
        if (requiredName == null && (gt.Weight != weight || gt.Style != style) && gt.StyleSimulations == StyleSimulations.None)
            return null;
        return gt;
    }

    private static GlyphTypeface? TryAnyFace(FontFamily family) =>
        new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out var gt) ? gt : null;

    private double Snap(double v) => Math.Round(v * _ppd) / _ppd;
    private double SnapUp(double v) => Math.Max(1, Math.Ceiling(v * _ppd - 0.01)) / _ppd;

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateMetrics();
        RecomputeGrid();
        Render();
    }

    // ───────────── Layout ─────────────

    protected override int VisualChildrenCount => _children.Count;
    protected override Visual GetVisualChild(int index) => _children[index];
    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    // The whole grid takes the mouse, not just the cells that have glyphs drawn on them.
    protected override HitTestResult HitTestCore(PointHitTestParameters p) =>
        new Rect(RenderSize).Contains(p.HitPoint) ? new PointHitTestResult(this, p.HitPoint) : null!;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RecomputeGrid();
        Render();
    }

    private void RecomputeGrid()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        int cols = Math.Max(2, (int)Math.Floor(ActualWidth / _cellW + 1e-6));
        int rows = Math.Max(1, (int)Math.Floor(ActualHeight / _cellH + 1e-6));

        if (rows != _rows.Length)
        {
            foreach (var v in _rows) _children.Remove(v);
            _rows = new TermVisual[rows];
            for (int r = 0; r < rows; r++)
            {
                _rows[r] = new TermVisual();
                _children.Insert(r, _rows[r]);
            }
            _rowLine = new Line?[rows];
            _rowVersion = new int[rows];
        }
        for (int r = 0; r < rows; r++) _rows[r].Offset = new Vector(0, r * _cellH);
        _forceRedraw = true;

        if (cols == Cols && rows == Rows) return;
        Cols = cols;
        Rows = rows;
        lock (_term.Sync) _term.Resize(cols, rows);
        GridResized?.Invoke(cols, rows);
    }

    // ───────────── Rendering ─────────────

    /// <summary>Re-records rows whose content changed since the last frame. Called once per frame at most.</summary>
    public void Render()
    {
        if (_rows.Length == 0) return;
        lock (_term.Sync)
        {
            int rows = Math.Min(_rows.Length, _term.Rows);
            for (int r = 0; r < _rows.Length; r++)
            {
                var line = r < rows ? _term.GetDisplayLine(r) : null;
                int version = line?.Version ?? 0;
                if (!_forceRedraw && ReferenceEquals(line, _rowLine[r]) && version == _rowVersion[r]) continue;
                _rowLine[r] = line;
                _rowVersion[r] = version;
                using var dc = _rows[r].RenderOpen();
                if (line != null) DrawRow(dc, line);
            }
            _forceRedraw = false;
            RenderSelection();
            RenderCursor();
        }
        UpdateBlink();
    }

    private void DrawRow(DrawingContext dc, Line line)
    {
        var cells = line.Cells;
        int n = Math.Min(cells.Length, Cols);

        // 1) background runs (default background is the window itself and is never painted)
        for (int c = 0; c < n;)
        {
            uint bg = CellBackground(in cells[c]);
            int start = c++;
            while (c < n && CellBackground(in cells[c]) == bg) c++;
            if (bg != 0) dc.DrawRectangle(Brush(bg), null, new Rect(start * _cellW, 0, (c - start) * _cellW, _cellH));
        }

        // 2) glyph runs: consecutive cells with the same face and color become one GlyphRun
        GlyphTypeface? runFace = null;
        uint runColor = 0;
        double runX = 0;
        _glyphs.Clear();
        _advances.Clear();
        for (int c = 0; c < n; c++)
        {
            ref readonly var cell = ref cells[c];
            if ((cell.Flags & CellFlags.WideTail) != 0) continue;
            int rune = cell.Rune;
            uint fg = CellForeground(in cell);
            if (rune is 0 or ' ' || fg == 0 || (cell.Flags & CellFlags.Hidden) != 0)
            {
                Flush();
                continue;
            }
            bool wide = (cell.Flags & CellFlags.Wide) != 0;
            var face = _faces[FaceIndex(cell.Flags)];
            string? extra = null;
            if ((line.Combining != null && line.Combining.TryGetValue(c, out extra)) || !face.CharacterToGlyphMap.TryGetValue(rune, out var glyph))
            {
                Flush();
                DrawFallback(dc, rune, extra, c, wide, fg, cell.Flags, 0);
                continue;
            }
            if (_glyphs.Count > 0 && (face != runFace || fg != runColor)) Flush();
            if (_glyphs.Count == 0)
            {
                runFace = face;
                runColor = fg;
                runX = c * _cellW;
            }
            _glyphs.Add(glyph);
            _advances.Add(wide ? _cellW * 2 : _cellW);
        }
        Flush();

        // 3) underline / double underline / strikethrough
        for (int c = 0; c < n;)
        {
            var deco = cells[c].Flags & (CellFlags.Underline | CellFlags.DoubleUnderline | CellFlags.Strike);
            uint fg = deco == 0 ? 0 : CellForeground(in cells[c]);
            int start = c++;
            while (c < n && (cells[c].Flags & (CellFlags.Underline | CellFlags.DoubleUnderline | CellFlags.Strike)) == deco &&
                   (deco == 0 || CellForeground(in cells[c]) == fg)) c++;
            if (deco == 0 || fg == 0) continue;
            var brush = Brush(fg);
            double x = start * _cellW, w = (c - start) * _cellW;
            if ((deco & CellFlags.Underline) != 0) dc.DrawRectangle(brush, null, new Rect(x, _underlineY, w, _lineThickness));
            if ((deco & CellFlags.DoubleUnderline) != 0)
            {
                double y2 = Math.Max(0, _underlineY - 2 * _lineThickness);
                dc.DrawRectangle(brush, null, new Rect(x, y2, w, _lineThickness));
                dc.DrawRectangle(brush, null, new Rect(x, _underlineY, w, _lineThickness));
            }
            if ((deco & CellFlags.Strike) != 0) dc.DrawRectangle(brush, null, new Rect(x, _strikeY, w, _lineThickness));
        }

        void Flush()
        {
            if (_glyphs.Count == 0 || runFace == null) return;
            var run = new GlyphRun(runFace, 0, false, _emSize, (float)_ppd, _glyphs.ToArray(), new Point(runX, _baseline),
                _advances.ToArray(), null, null, null, null, null, null);
            dc.DrawGlyphRun(Brush(runColor), run);
            _glyphs.Clear();
            _advances.Clear();
        }
    }

    private void DrawFallback(DrawingContext dc, int rune, string? extra, int col, bool wide, uint fg, CellFlags flags, double y)
    {
        var text = char.ConvertFromUtf32(rune) + extra;
        var key = (text, fg, flags & (CellFlags.Bold | CellFlags.Italic));
        if (!_fallbackCache.TryGetValue(key, out var ft))
        {
            if (_fallbackCache.Count > 512) _fallbackCache.Clear();
            ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typefaces[FaceIndex(flags)], _emSize, Brush(fg), _ppd);
            _fallbackCache[key] = ft;
        }
        double width = (wide ? 2 : 1) * _cellW;
        double x = col * _cellW + Math.Max(0, (width - ft.WidthIncludingTrailingWhitespace) / 2);
        dc.DrawText(ft, new Point(x, y + _baseline - ft.Baseline));
    }

    private static int FaceIndex(CellFlags f) => ((f & CellFlags.Bold) != 0 ? 1 : 0) + ((f & CellFlags.Italic) != 0 ? 2 : 0);

    private uint CellBackground(in Cell cell)
    {
        if ((cell.Flags & CellFlags.Inverse) != 0) return _theme.Resolve(cell.Fg, _theme.Foreground);
        return cell.Bg == 0 ? 0 : _theme.Resolve(cell.Bg, 0);
    }

    private uint CellForeground(in Cell cell)
    {
        uint fg = (cell.Flags & CellFlags.Inverse) != 0
            ? (cell.Bg == 0 ? _theme.Background : _theme.Resolve(cell.Bg, _theme.Background))
            : _theme.Resolve(cell.Fg, _theme.Foreground);
        if ((cell.Flags & CellFlags.Dim) != 0) fg = (fg & 0x00FFFFFF) | 0x99000000;
        return fg;
    }

    private SolidColorBrush Brush(uint argb)
    {
        if (_brushes.TryGetValue(argb, out var b)) return b;
        if (_brushes.Count > 1024) _brushes.Clear();
        b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        _brushes[argb] = b;
        return b;
    }

    private void RenderSelection()
    {
        var key = _hasSelection ? (_selAnchor.Line, _selAnchor.Col, _selEnd.Line, _selEnd.Col, Rows, _term.DisplayRowToAbs(0)) : ((long, int, long, int, int, long)?)null;
        if (key == _selRendered && !_forceRedraw) return;
        _selRendered = key;
        using var dc = _selectionVisual.RenderOpen();
        if (!_hasSelection) return;
        var (sl, sc, el, ec) = NormalizedSelection();
        var brush = Brush(_theme.Selection);
        for (int r = 0; r < Rows; r++)
        {
            long abs = _term.DisplayRowToAbs(r);
            if (abs < sl || abs > el) continue;
            int c0 = abs == sl ? sc : 0;
            int c1 = abs == el ? ec : Cols - 1;
            if (c1 < c0) continue;
            dc.DrawRectangle(brush, null, new Rect(c0 * _cellW, r * _cellH, (c1 - c0 + 1) * _cellW, _cellH));
        }
    }

    private void RenderCursor()
    {
        using var dc = _cursorVisual.RenderOpen();
        int row = _term.CursorY + _term.ViewOffset;
        if (!_term.CursorVisible || row >= Rows || _rows.Length == 0) return;
        int col = Math.Min(_term.CursorX, Cols - 1);
        var line = _term.GetDisplayLine(row);
        Cell cell = default;
        if (line != null && col < line.Length)
        {
            if ((line.Cells[col].Flags & CellFlags.WideTail) != 0 && col > 0) col--;
            cell = line.Cells[col];
        }
        double w = (cell.Flags & CellFlags.Wide) != 0 ? _cellW * 2 : _cellW;
        double x = col * _cellW, y = row * _cellH;
        var brush = Brush(_theme.Cursor);

        string style = _term.CursorShape switch
        {
            1 or 2 => "Block",
            3 or 4 => "Underline",
            5 or 6 => "Beam",
            _ => _cursorStyle,
        };

        if (!_focused)
        {
            // Unfocused: a thin hollow box, so it's obvious keystrokes are not going to the terminal.
            double t = 1 / _ppd;
            dc.DrawRectangle(null, new Pen(brush, t), new Rect(x + t / 2, y + t / 2, w - t, _cellH - t));
            return;
        }

        switch (style)
        {
            case "Beam":
                dc.DrawRectangle(brush, null, new Rect(x, y, Math.Max(1 / _ppd, Snap(_cellW / 7)), _cellH));
                break;
            case "Underline":
            {
                double h = Math.Max(1 / _ppd, Snap(_cellH / 10));
                dc.DrawRectangle(brush, null, new Rect(x, y + _cellH - h, w, h));
                break;
            }
            default:
                dc.DrawRectangle(brush, null, new Rect(x, y, w, _cellH));
                if (cell.Rune is not (0 or ' '))
                {
                    var face = _faces[FaceIndex(cell.Flags)];
                    string? extra = null;
                    if ((line?.Combining == null || !line.Combining.TryGetValue(col, out extra)) && face.CharacterToGlyphMap.TryGetValue(cell.Rune, out var g))
                    {
                        var run = new GlyphRun(face, 0, false, _emSize, (float)_ppd, [g], new Point(x, y + _baseline), [w], null, null, null, null, null, null);
                        dc.DrawGlyphRun(Brush(_theme.Background), run);
                    }
                    else
                    {
                        DrawFallback(dc, cell.Rune, extra, col, w > _cellW, _theme.Background, cell.Flags, y);
                    }
                }
                break;
        }
    }

    private void UpdateBlink()
    {
        int shape = _term.CursorShape;
        bool steady = shape is 2 or 4 or 6;
        bool run = _focused && _powerActive && _cursorBlinkConfig && !steady && _term.CursorVisible;
        _cursorVisual.Opacity = 1;
        _blinkTimer.Stop();
        if (run) _blinkTimer.Start();
    }

    // ───────────── Selection ─────────────

    private ((long, int) Start, (long, int) End) Ordered() =>
        _selAnchor.Line < _selEnd.Line || (_selAnchor.Line == _selEnd.Line && _selAnchor.Col <= _selEnd.Col)
            ? (_selAnchor, _selEnd) : (_selEnd, _selAnchor);

    private (long, int, long, int) NormalizedSelection()
    {
        var (s, e) = Ordered();
        return (s.Item1, s.Item2, e.Item1, e.Item2);
    }

    public bool HasSelection => _hasSelection;

    public void ClearSelection()
    {
        if (!_hasSelection) return;
        _hasSelection = false;
        _selecting = false;
        Render();
    }

    private string SelectedText()
    {
        if (!_hasSelection) return "";
        var (sl, sc, el, ec) = NormalizedSelection();
        lock (_term.Sync) return _term.GetText(sl, sc, el, ec);
    }

    public void CopySelection()
    {
        var text = SelectedText();
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }

    public void Paste()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (System.Runtime.InteropServices.ExternalException) { return; }
        if (text.Length == 0) return;
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        bool bracketed;
        lock (_term.Sync) bracketed = _term.BracketedPaste;
        if (bracketed) text = "\x1b[200~" + text.Replace("\x1b[201~", "") + "\x1b[201~";
        Send(text);
    }

    private (int Col, int Row) CellAt(Point p) =>
        (Math.Clamp((int)Math.Floor(p.X / _cellW), 0, Math.Max(0, Cols - 1)),
         Math.Clamp((int)Math.Floor(p.Y / _cellH), 0, Math.Max(0, Rows - 1)));

    // ───────────── Input ─────────────

    private void Send(string text)
    {
        if (text.Length == 0) return;
        bool scrolled;
        lock (_term.Sync) scrolled = _term.ScrollToBottom();
        if (_hasSelection) _hasSelection = false;
        if (scrolled || _selRendered != null) Render();
        UpdateBlink();
        Input?.Invoke(text);
    }

    private void ScrollViewport(int lines)
    {
        bool changed;
        lock (_term.Sync) changed = _term.ScrollView(lines);
        if (changed) Render();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.ImeProcessed or Key.DeadCharProcessed or Key.None) return;
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0, alt = (mods & ModifierKeys.Alt) != 0;

        if (alt && key == Key.F4) { e.Handled = true; return; } // the wallpaper never closes from the keyboard
        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        if (ctrl && !alt)
        {
            if (key == Key.C && (shift || _hasSelection))
            {
                CopySelection();
                ClearSelection();
                e.Handled = true;
                return;
            }
            if (key == Key.V)
            {
                Paste();
                e.Handled = true;
                return;
            }
            if (key is Key.D0 or Key.NumPad0) { ResetZoom(); e.Handled = true; return; }
            if (shift && key is Key.Up or Key.Down) { ScrollViewport(key == Key.Up ? 1 : -1); e.Handled = true; return; }
        }
        if (shift && !ctrl && !alt && key is Key.PageUp or Key.PageDown)
        {
            bool alt2;
            lock (_term.Sync) alt2 = _term.AltScreen;
            if (!alt2)
            {
                ScrollViewport(key == Key.PageUp ? Math.Max(1, Rows - 1) : -Math.Max(1, Rows - 1));
                e.Handled = true;
                return;
            }
        }

        bool appCursor;
        lock (_term.Sync) appCursor = _term.AppCursorKeys;
        var seq = KeyEncoder.Encode(key, mods, appCursor);
        if (seq != null)
        {
            Send(seq);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        string? text = null;
        if (!string.IsNullOrEmpty(e.Text)) text = e.Text;
        else if (!string.IsNullOrEmpty(e.SystemText)) text = "\x1b" + e.SystemText; // Alt+key → ESC prefix
        if (text == null || text.Any(ch => ch < 0x20 && ch != 0x1b) || text == "\x1b") return;
        Send(text);
        e.Handled = true;
    }

    private bool MouseReporting(MouseEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return false; // Shift forces local selection
        lock (_term.Sync) return _term.Mouse != MouseTracking.None && _term.ViewOffset == 0;
    }

    private void SendMouse(int button, bool press, bool motion, int col, int row)
    {
        var mods = Keyboard.Modifiers;
        int cb = button + (motion ? 32 : 0)
                 + ((mods & ModifierKeys.Shift) != 0 ? 4 : 0)
                 + ((mods & ModifierKeys.Alt) != 0 ? 8 : 0)
                 + ((mods & ModifierKeys.Control) != 0 ? 16 : 0);
        bool sgr;
        lock (_term.Sync) sgr = _term.MouseSgr;
        if (sgr)
        {
            Input?.Invoke($"\x1b[<{cb};{col + 1};{row + 1}{(press ? 'M' : 'm')}");
        }
        else
        {
            if (!press) cb = 3 + (cb & ~3 & ~32) + (motion ? 32 : 0);
            if (col + 33 > 255 || row + 33 > 255) return;
            Input?.Invoke($"\x1b[M{(char)(32 + cb)}{(char)(33 + col)}{(char)(33 + row)}");
        }
    }

    private static int ButtonCode(MouseButton b) => b switch
    {
        MouseButton.Left => 0,
        MouseButton.Middle => 1,
        MouseButton.Right => 2,
        _ => -1,
    };

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var (col, row) = CellAt(e.GetPosition(this));

        if (MouseReporting(e))
        {
            int b = ButtonCode(e.ChangedButton);
            if (b >= 0)
            {
                _mouseButtonDown = b;
                _lastMouseCell = (col, row);
                SendMouse(b, true, false, col, row);
                CaptureMouse();
            }
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            long abs;
            lock (_term.Sync) abs = _term.DisplayRowToAbs(row);
            if (e.ClickCount == 2)
            {
                (int s, int en) word;
                lock (_term.Sync) word = _term.WordAt(abs, col);
                _selAnchor = (abs, word.s);
                _selEnd = (abs, word.en);
                _hasSelection = true;
                _selecting = false;
            }
            else if (e.ClickCount >= 3)
            {
                _selAnchor = (abs, 0);
                _selEnd = (abs, Cols - 1);
                _hasSelection = true;
                _selecting = false;
            }
            else
            {
                _selAnchor = _selEnd = (abs, col);
                _hasSelection = false;
                _selecting = true;
                _selectionMoved = false;
                CaptureMouse();
            }
            Render();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            if (_hasSelection)
            {
                CopySelection();
                ClearSelection();
            }
            else
            {
                Paste();
            }
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        var (col, row) = CellAt(p);

        if (_mouseButtonDown >= 0 || (MouseReporting(e) && !_selecting))
        {
            MouseTracking mode;
            lock (_term.Sync) mode = _term.Mouse;
            if (_lastMouseCell == (col, row)) return;
            _lastMouseCell = (col, row);
            if (_mouseButtonDown >= 0 && mode is MouseTracking.Drag or MouseTracking.Any) SendMouse(_mouseButtonDown, true, true, col, row);
            else if (_mouseButtonDown < 0 && mode == MouseTracking.Any) SendMouse(3, true, true, col, row);
            return;
        }

        if (!_selecting || e.LeftButton != MouseButtonState.Pressed) return;
        // Dragging past the top/bottom edge scrolls the history one line per mouse move.
        if (p.Y < 0) ScrollViewport(1);
        else if (p.Y > ActualHeight) ScrollViewport(-1);
        long abs;
        lock (_term.Sync) abs = _term.DisplayRowToAbs(row);
        if (_selEnd == (abs, col)) return;
        _selEnd = (abs, col);
        _selectionMoved = true;
        _hasSelection = true;
        Render();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_mouseButtonDown >= 0)
        {
            var (col, row) = CellAt(e.GetPosition(this));
            SendMouse(_mouseButtonDown, false, false, col, row);
            _mouseButtonDown = -1;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (_selecting && e.ChangedButton == MouseButton.Left)
        {
            _selecting = false;
            ReleaseMouseCapture();
            if (!_selectionMoved) ClearSelection();
            e.Handled = true;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;
        int notches = e.Delta / 120;
        if (notches == 0) notches = Math.Sign(e.Delta);

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Zoom(notches);
            return;
        }

        var (col, row) = CellAt(e.GetPosition(this));
        if (MouseReporting(e))
        {
            for (int i = 0; i < Math.Abs(notches); i++) SendMouse(notches > 0 ? 64 : 65, true, false, col, row);
            return;
        }

        bool altScreen, appCursor;
        lock (_term.Sync) { altScreen = _term.AltScreen; appCursor = _term.AppCursorKeys; }
        if (altScreen)
        {
            // Alternate-scroll: full-screen apps without mouse support (less, editors) get arrow keys.
            var arrow = (appCursor ? "\x1bO" : "\x1b[") + (notches > 0 ? "A" : "B");
            Input?.Invoke(string.Concat(Enumerable.Repeat(arrow, Math.Abs(notches) * 3)));
            return;
        }
        ScrollViewport(notches * 3);
    }
}
