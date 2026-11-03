using System.Text;

namespace WallTerm.Vt;

internal enum MouseTracking { None, Click, Drag, Any }

/// <summary>
/// Terminal screen model: main/alternate buffers, scrollback ring, cursor, modes. All access goes through
/// <see cref="Sync"/>: the ConPTY reader thread feeds bytes, the UI thread renders and selects.
/// </summary>
internal sealed partial class VtTerminal
{
    public readonly object Sync = new();

    public int Cols { get; private set; }
    public int Rows { get; private set; }

    private Line[] _main;
    private Line[] _alt;
    private Line[] _screen;
    public bool AltScreen { get; private set; }

    // Scrollback ring buffer (oldest at _sbHead).
    private Line[] _sb;
    private int _sbHead, _sbCount;
    /// <summary>Total lines ever pushed off the top of the main screen; anchors absolute line numbers.</summary>
    public long ScrolledOff { get; private set; }
    public int ScrollbackCount => _sbCount;
    /// <summary>How many lines the viewport is scrolled back from the live screen.</summary>
    public int ViewOffset { get; private set; }

    private int _x, _y;
    private bool _wrapPending;
    private Cell _pen;
    private int _top, _bottom;
    private bool[] _tabs;
    private bool _autoWrap = true, _originMode, _insertMode;
    private bool _g0Graphics, _g1Graphics, _shiftOut;
    private int _lastPrinted = ' ';

    public bool AppCursorKeys { get; private set; }
    public bool BracketedPaste { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public bool FocusReporting { get; private set; }
    public MouseTracking Mouse { get; private set; }
    public bool MouseSgr { get; private set; }
    /// <summary>DECSCUSR shape requested by the application (0 = user default).</summary>
    public int CursorShape { get; private set; }
    public string? Title { get; private set; }

    public int CursorX => _x;
    public int CursorY => _y;

    /// <summary>Replies to device queries (DSR, DA, OSC color queries) are written back to the PTY.</summary>
    public Action<string>? Respond;
    public uint DefaultForeground = 0xFFD4D4DC;
    public uint DefaultBackground = 0xFF000000;

    private struct SavedCursor
    {
        public bool Valid;
        public int X, Y;
        public Cell Pen;
        public bool Origin, WrapPending, G0, G1, ShiftOut;
    }

    private SavedCursor _savedMain, _savedAlt;

    public VtTerminal(int cols, int rows, int scrollback)
    {
        Cols = Math.Max(2, cols);
        Rows = Math.Max(1, rows);
        _main = NewScreen(Cols, Rows);
        _alt = NewScreen(Cols, Rows);
        _screen = _main;
        _sb = new Line[Math.Max(0, scrollback)];
        _tabs = new bool[Cols];
        ResetTabs();
        _bottom = Rows - 1;
    }

    private static Line[] NewScreen(int cols, int rows)
    {
        var lines = new Line[rows];
        for (int i = 0; i < rows; i++) lines[i] = new Line(cols);
        return lines;
    }

    /// <summary>Blank cell for erase operations (background color erase).</summary>
    private Cell Erase => new() { Bg = _pen.Bg };

    // ───────────── Viewport / history access ─────────────

    public long DisplayRowToAbs(int row) => ScrolledOff - ViewOffset + row;

    public Line? GetDisplayLine(int row) => GetAbsLine(DisplayRowToAbs(row));

    public Line? GetAbsLine(long abs)
    {
        long rel = abs - ScrolledOff;
        if (rel >= 0) return rel < Rows ? _screen[rel] : null;
        long idx = _sbCount + rel;
        if (idx < 0 || _sb.Length == 0) return null;
        return _sb[(_sbHead + (int)idx) % _sb.Length];
    }

    public long FirstAbsLine => ScrolledOff - _sbCount;

    public bool ScrollView(int delta)
    {
        int max = AltScreen ? 0 : _sbCount;
        int v = Math.Clamp(ViewOffset + delta, 0, max);
        if (v == ViewOffset) return false;
        ViewOffset = v;
        return true;
    }

    public bool ScrollToBottom() => ScrollView(-ViewOffset);

    public void SetScrollbackCapacity(int capacity)
    {
        capacity = Math.Max(0, capacity);
        if (capacity == _sb.Length) return;
        int keep = Math.Min(_sbCount, capacity);
        var ring = new Line[capacity];
        for (int i = 0; i < keep; i++)
            ring[i] = _sb[(_sbHead + _sbCount - keep + i) % _sb.Length];
        _sb = ring;
        _sbHead = 0;
        _sbCount = keep;
        ViewOffset = Math.Min(ViewOffset, _sbCount);
    }

    private Line PushScrollback(Line line)
    {
        ScrolledOff++;
        if (_sb.Length == 0) return line;
        Line recycled;
        if (_sbCount < _sb.Length)
        {
            _sb[(_sbHead + _sbCount) % _sb.Length] = line;
            _sbCount++;
            recycled = new Line(Cols);
        }
        else
        {
            recycled = _sb[_sbHead];
            _sb[_sbHead] = line;
            _sbHead = (_sbHead + 1) % _sb.Length;
        }
        if (ViewOffset > 0) ViewOffset = Math.Min(ViewOffset + 1, _sbCount);
        return recycled;
    }

    private void ClearScrollback()
    {
        Array.Clear(_sb);
        _sbHead = 0;
        _sbCount = 0;
        ViewOffset = 0;
    }

    // ───────────── Scrolling ─────────────

    private void ScrollUpRegion(int top, int bottom, int n, bool toHistory)
    {
        int height = bottom - top + 1;
        n = Math.Clamp(n, 0, height);
        var blank = Erase;
        for (int i = 0; i < n; i++)
        {
            var line = _screen[top];
            if (toHistory) line = PushScrollback(line);
            Array.Copy(_screen, top + 1, _screen, top, height - 1);
            line.Reset(Cols, blank);
            _screen[bottom] = line;
        }
    }

    private void ScrollDownRegion(int top, int bottom, int n)
    {
        int height = bottom - top + 1;
        n = Math.Clamp(n, 0, height);
        var blank = Erase;
        for (int i = 0; i < n; i++)
        {
            var line = _screen[bottom];
            Array.Copy(_screen, top, _screen, top + 1, height - 1);
            line.Reset(Cols, blank);
            _screen[top] = line;
        }
    }

    private bool HistoryEligible => !AltScreen && _top == 0;

    private void LineFeed()
    {
        _wrapPending = false;
        if (_y == _bottom) ScrollUpRegion(_top, _bottom, 1, HistoryEligible);
        else if (_y < Rows - 1) _y++;
    }

    private void ReverseIndex()
    {
        _wrapPending = false;
        if (_y == _top) ScrollDownRegion(_top, _bottom, 1);
        else if (_y > 0) _y--;
    }

    // ───────────── Printing ─────────────

    private static readonly int[] DecGraphics =
    {
        0x00A0, 0x25C6, 0x2592, 0x2409, 0x240C, 0x240D, 0x240A, 0x00B0, 0x00B1, 0x2424, 0x240B, 0x2518, 0x2510, 0x250C, 0x2514, 0x253C,
        0x23BA, 0x23BB, 0x2500, 0x23BC, 0x23BD, 0x251C, 0x2524, 0x2534, 0x252C, 0x2502, 0x2264, 0x2265, 0x03C0, 0x2260, 0x00A3, 0x00B7,
    };

    private void Print(int cp)
    {
        if ((_shiftOut ? _g1Graphics : _g0Graphics) && cp is >= 0x5F and <= 0x7E) cp = DecGraphics[cp - 0x5F];

        int width = CharWidth.Of(cp);
        if (width == 0)
        {
            AttachCombining(cp);
            return;
        }

        if (_wrapPending)
        {
            _wrapPending = false;
            if (_autoWrap)
            {
                _screen[_y].Wrapped = true;
                _x = 0;
                LineFeed();
            }
        }

        if (width == 2 && _x == Cols - 1)
        {
            if (_autoWrap)
            {
                var l = _screen[_y];
                l.Fill(_x, Cols, Erase);
                l.Wrapped = true;
                _x = 0;
                LineFeed();
            }
            else
            {
                _x = Math.Max(0, Cols - 2);
            }
        }

        var line = _screen[_y];
        if (_insertMode) InsertCells(line, _x, width);
        var cells = line.Cells;
        SplitWide(cells, _x);
        if (width == 2) SplitWide(cells, _x + 1);

        var flags = _pen.Flags & ~CellFlags.LayoutMask;
        cells[_x] = new Cell { Rune = cp, Fg = _pen.Fg, Bg = _pen.Bg, Flags = width == 2 ? flags | CellFlags.Wide : flags };
        line.Combining?.Remove(_x);
        if (width == 2)
        {
            cells[_x + 1] = new Cell { Rune = 0, Fg = _pen.Fg, Bg = _pen.Bg, Flags = flags | CellFlags.WideTail };
            line.Combining?.Remove(_x + 1);
        }
        line.Version++;
        _lastPrinted = cp;

        int nx = _x + width;
        if (nx >= Cols)
        {
            _x = Cols - 1;
            _wrapPending = _autoWrap;
        }
        else
        {
            _x = nx;
        }
    }

    /// <summary>Overwriting either half of a wide character erases the other half.</summary>
    private void SplitWide(Cell[] cells, int x)
    {
        if (x >= cells.Length) return;
        var f = cells[x].Flags;
        if ((f & CellFlags.WideTail) != 0 && x > 0)
        {
            cells[x - 1].Rune = 0;
            cells[x - 1].Flags &= ~CellFlags.LayoutMask;
        }
        else if ((f & CellFlags.Wide) != 0 && x + 1 < cells.Length)
        {
            cells[x + 1].Rune = 0;
            cells[x + 1].Flags &= ~CellFlags.LayoutMask;
        }
    }

    private void AttachCombining(int cp)
    {
        int col = _wrapPending ? _x : _x - 1;
        if (col < 0) return;
        var line = _screen[_y];
        if ((line.Cells[col].Flags & CellFlags.WideTail) != 0 && col > 0) col--;
        if (line.Cells[col].Rune == 0) return;
        line.Combining ??= new Dictionary<int, string>();
        line.Combining.TryGetValue(col, out var existing);
        if (existing is { Length: > 16 }) return;
        line.Combining[col] = existing + char.ConvertFromUtf32(cp);
        line.Version++;
    }

    private void InsertCells(Line line, int x, int n)
    {
        var cells = line.Cells;
        n = Math.Min(n, Cols - x);
        if (n <= 0) return;
        Array.Copy(cells, x, cells, x + n, Cols - x - n);
        Array.Fill(cells, Erase, x, n);
        line.Combining = null;
        line.Version++;
    }

    private void DeleteCells(Line line, int x, int n)
    {
        var cells = line.Cells;
        n = Math.Min(n, Cols - x);
        if (n <= 0) return;
        Array.Copy(cells, x + n, cells, x, Cols - x - n);
        Array.Fill(cells, Erase, Cols - n, n);
        line.Combining = null;
        line.Version++;
    }

    // ───────────── Cursor ─────────────

    private void CursorTo(int x, int y)
    {
        _wrapPending = false;
        _x = Math.Clamp(x, 0, Cols - 1);
        if (_originMode) _y = Math.Clamp(y + _top, _top, _bottom);
        else _y = Math.Clamp(y, 0, Rows - 1);
    }

    private void CursorUp(int n)
    {
        _wrapPending = false;
        int min = _y >= _top ? _top : 0;
        _y = Math.Max(min, _y - n);
    }

    private void CursorDown(int n)
    {
        _wrapPending = false;
        int max = _y <= _bottom ? _bottom : Rows - 1;
        _y = Math.Min(max, _y + n);
    }

    private void CursorForward(int n)
    {
        _wrapPending = false;
        _x = Math.Min(Cols - 1, _x + n);
    }

    private void CursorBack(int n)
    {
        _wrapPending = false;
        _x = Math.Max(0, _x - n);
    }

    private void Tab(int count = 1)
    {
        while (count-- > 0)
        {
            int x = _x + 1;
            while (x < Cols - 1 && !_tabs[x]) x++;
            _x = Math.Min(x, Cols - 1);
        }
    }

    private void BackTab(int count)
    {
        while (count-- > 0 && _x > 0)
        {
            int x = _x - 1;
            while (x > 0 && !_tabs[x]) x--;
            _x = x;
        }
        _wrapPending = false;
    }

    private void ResetTabs()
    {
        for (int i = 0; i < _tabs.Length; i++) _tabs[i] = i > 0 && i % 8 == 0;
    }

    private void SaveCursor()
    {
        var s = new SavedCursor
        {
            Valid = true, X = _x, Y = _y, Pen = _pen, Origin = _originMode, WrapPending = _wrapPending,
            G0 = _g0Graphics, G1 = _g1Graphics, ShiftOut = _shiftOut,
        };
        if (AltScreen) _savedAlt = s;
        else _savedMain = s;
    }

    private void RestoreCursor()
    {
        var s = AltScreen ? _savedAlt : _savedMain;
        if (!s.Valid)
        {
            _x = _y = 0;
            _pen = default;
            _originMode = false;
            _wrapPending = false;
            return;
        }
        _x = Math.Min(s.X, Cols - 1);
        _y = Math.Min(s.Y, Rows - 1);
        _pen = s.Pen;
        _originMode = s.Origin;
        _wrapPending = s.WrapPending && _x == Cols - 1;
        _g0Graphics = s.G0;
        _g1Graphics = s.G1;
        _shiftOut = s.ShiftOut;
    }

    // ───────────── Erase ─────────────

    private void EraseInDisplay(int mode)
    {
        var blank = Erase;
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (int y = _y + 1; y < Rows; y++) _screen[y].Reset(Cols, blank);
                break;
            case 1:
                for (int y = 0; y < _y; y++) _screen[y].Reset(Cols, blank);
                EraseInLine(1);
                break;
            case 2:
                for (int y = 0; y < Rows; y++) _screen[y].Reset(Cols, blank);
                break;
            case 3:
                ClearScrollback();
                break;
        }
    }

    private void EraseInLine(int mode)
    {
        _wrapPending = false;
        var line = _screen[_y];
        var blank = Erase;
        switch (mode)
        {
            case 0: line.Fill(_x, Cols, blank); line.Wrapped = false; break;
            case 1: line.Fill(0, _x + 1, blank); break;
            case 2: line.Fill(0, Cols, blank); line.Wrapped = false; break;
        }
    }

    // ───────────── Modes / resets ─────────────

    private void SetAltScreen(bool on, bool saveRestoreCursor)
    {
        if (on == AltScreen) return;
        if (on)
        {
            if (saveRestoreCursor) SaveCursor();
            _screen = _alt;
            AltScreen = true;
            var blank = Erase;
            foreach (var line in _alt) line.Reset(Cols, blank);
            ViewOffset = 0;
        }
        else
        {
            _screen = _main;
            AltScreen = false;
            if (saveRestoreCursor) RestoreCursor();
        }
        _wrapPending = false;
    }

    private void SoftReset()
    {
        CursorVisible = true;
        _originMode = false;
        _autoWrap = true;
        _insertMode = false;
        AppCursorKeys = false;
        _top = 0;
        _bottom = Rows - 1;
        _pen = default;
        _g0Graphics = _g1Graphics = _shiftOut = false;
        _savedMain = _savedAlt = default;
    }

    public void FullReset()
    {
        SoftReset();
        if (AltScreen) { _screen = _main; AltScreen = false; }
        foreach (var line in _main) line.Reset(Cols, default);
        foreach (var line in _alt) line.Reset(Cols, default);
        _x = _y = 0;
        _wrapPending = false;
        BracketedPaste = false;
        FocusReporting = false;
        Mouse = MouseTracking.None;
        MouseSgr = false;
        CursorShape = 0;
        Title = null;
        ResetTabs();
        ViewOffset = 0;
        _state = State.Ground;
    }

    // ───────────── Resize ─────────────

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(2, cols);
        rows = Math.Max(1, rows);
        if (cols == Cols && rows == Rows) return;

        int shiftMain = ResizeScreen(ref _main, cols, rows, AltScreen ? -1 : _y, toHistory: true);
        int shiftAlt = ResizeScreen(ref _alt, cols, rows, AltScreen ? _y : -1, toHistory: false);
        _screen = AltScreen ? _alt : _main;
        Cols = cols;
        Rows = rows;

        _y -= AltScreen ? shiftAlt : shiftMain;
        _x = Math.Clamp(_x, 0, Cols - 1);
        _y = Math.Clamp(_y, 0, Rows - 1);
        _wrapPending = false;
        _top = 0;
        _bottom = Rows - 1;
        _tabs = new bool[Cols];
        ResetTabs();
        _savedMain.X = Math.Min(_savedMain.X, Cols - 1);
        _savedMain.Y = Math.Clamp(_savedMain.Y - shiftMain, 0, Rows - 1);
        _savedAlt.X = Math.Min(_savedAlt.X, Cols - 1);
        _savedAlt.Y = Math.Clamp(_savedAlt.Y - shiftAlt, 0, Rows - 1);
        ViewOffset = Math.Min(ViewOffset, _sbCount);
    }

    /// <summary>Returns how many lines were removed from the top (pushed into history for the main screen).</summary>
    private int ResizeScreen(ref Line[] screen, int cols, int rows, int cursorY, bool toHistory)
    {
        var lines = new List<Line>(screen);
        int shift = 0;
        int excess = lines.Count - rows;
        // 1) drop blank lines below the cursor, 2) move lines above the cursor into history, 3) truncate the rest.
        while (excess > 0 && lines.Count - 1 > cursorY && lines[^1].IsBlank()) { lines.RemoveAt(lines.Count - 1); excess--; }
        while (excess > 0 && shift < Math.Max(0, cursorY))
        {
            var top = lines[0];
            lines.RemoveAt(0);
            if (toHistory) PushScrollback(top);
            shift++;
            excess--;
        }
        if (excess > 0) lines.RemoveRange(lines.Count - excess, excess);
        while (lines.Count < rows) lines.Add(new Line(cols));
        foreach (var line in lines) line.Resize(cols);
        screen = lines.ToArray();
        return shift;
    }

    // ───────────── Selection helpers ─────────────

    public string GetText(long startAbs, int startCol, long endAbs, int endCol)
    {
        var sb = new StringBuilder();
        var row = new StringBuilder();
        for (long a = startAbs; a <= endAbs; a++)
        {
            var line = GetAbsLine(a);
            if (line == null) continue;
            int from = a == startAbs ? startCol : 0;
            int to = Math.Min(a == endAbs ? endCol : line.Length - 1, line.Length - 1);
            row.Clear();
            for (int c = Math.Max(0, from); c <= to; c++)
            {
                ref readonly var cell = ref line.Cells[c];
                if ((cell.Flags & CellFlags.WideTail) != 0) continue;
                if (cell.Rune == 0) row.Append(' ');
                else row.Append(char.ConvertFromUtf32(cell.Rune));
                if (line.Combining != null && line.Combining.TryGetValue(c, out var extra)) row.Append(extra);
            }
            bool continues = line.Wrapped && a != endAbs;
            var text = continues ? row.ToString() : row.ToString().TrimEnd(' ');
            sb.Append(text);
            if (a != endAbs && !continues) sb.Append("\r\n");
        }
        return sb.ToString();
    }

    public (int Start, int End) WordAt(long abs, int col)
    {
        var line = GetAbsLine(abs);
        if (line == null || col >= line.Length) return (col, col);
        static bool IsWord(int r) => r != 0 && (char.IsLetterOrDigit((char)Math.Min(r, 0xFFFF)) || r > 0xFFFF || "_-./\\:~@?&=%+#$".Contains((char)r));
        bool target = IsWord(line.Cells[col].Rune);
        int s = col, e = col;
        while (s > 0 && IsWord(line.Cells[s - 1].Rune) == target && (target || line.Cells[s - 1].Rune == line.Cells[col].Rune)) s--;
        while (e < line.Length - 1 && IsWord(line.Cells[e + 1].Rune) == target && (target || line.Cells[e + 1].Rune == line.Cells[col].Rune)) e++;
        return (s, e);
    }
}
