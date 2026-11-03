using System.Text;

namespace WallTerm.Vt;

/// <summary>VT500-style escape sequence state machine (C0, ESC, CSI, OSC; DCS/SOS/PM/APC are consumed and ignored).</summary>
internal sealed partial class VtTerminal
{
    private enum State : byte { Ground, Escape, EscInter, Csi, Osc, OscEsc, Str, StrEsc }

    private State _state;
    private char _pendingHigh;
    private readonly int[] _params = new int[32];
    private readonly bool[] _sub = new bool[32];
    private int _paramCount, _cur;
    private bool _nextSub;
    private char _private, _inter;
    private readonly StringBuilder _osc = new();

    /// <summary>Feeds decoded output from the PTY. Caller holds <see cref="Sync"/>.</summary>
    public void Feed(ReadOnlySpan<char> data)
    {
        int i = 0;
        if (_pendingHigh != '\0')
        {
            char high = _pendingHigh;
            _pendingHigh = '\0';
            if (data.Length > 0 && char.IsLowSurrogate(data[0])) { Process(char.ConvertToUtf32(high, data[0])); i = 1; }
            else Process(0xFFFD);
        }

        for (; i < data.Length; i++)
        {
            char c = data[i];
            if (char.IsSurrogate(c))
            {
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 == data.Length) { _pendingHigh = c; break; }
                    if (char.IsLowSurrogate(data[i + 1])) { Process(char.ConvertToUtf32(c, data[++i])); continue; }
                }
                Process(0xFFFD);
                continue;
            }
            Process(c);
        }
    }

    private void Process(int cp)
    {
        if (cp is 0x18 or 0x1A) { _state = State.Ground; return; }
        if (cp == 0x1B)
        {
            if (_state == State.Osc) { _state = State.OscEsc; return; }
            if (_state == State.Str) { _state = State.StrEsc; return; }
            _state = State.Escape;
            _inter = '\0';
            return;
        }

        switch (_state)
        {
            case State.Ground:
                if (cp < 0x20 || cp == 0x7F) Execute(cp);
                else if (cp is < 0x80 or > 0x9F) Print(cp);
                return;

            case State.Escape:
                if (cp < 0x20) { Execute(cp); return; }
                switch (cp)
                {
                    case '[': BeginCsi(); _state = State.Csi; return;
                    case ']': _osc.Clear(); _state = State.Osc; return;
                    case 'P': case 'X': case '^': case '_': _state = State.Str; return;
                }
                if (cp <= 0x2F) { _inter = (char)cp; _state = State.EscInter; return; }
                _state = State.Ground;
                EscDispatch(cp, '\0');
                return;

            case State.EscInter:
                if (cp < 0x20) { Execute(cp); return; }
                if (cp <= 0x2F) { _inter = (char)cp; return; }
                _state = State.Ground;
                EscDispatch(cp, _inter);
                return;

            case State.Csi:
                CsiChar(cp);
                return;

            case State.Osc:
                if (cp == 0x07) { _state = State.Ground; OscDispatch(); return; }
                if (cp < 0x20) return;
                if (_osc.Length < 8192)
                {
                    if (cp > 0xFFFF) _osc.Append(char.ConvertFromUtf32(cp));
                    else _osc.Append((char)cp);
                }
                return;

            case State.OscEsc:
                OscDispatch();
                _state = State.Ground;
                if (cp != '\\') { _state = State.Escape; Process(cp); }
                return;

            case State.Str:
                if (cp == 0x07) _state = State.Ground;
                return;

            case State.StrEsc:
                _state = State.Ground;
                if (cp != '\\') { _state = State.Escape; Process(cp); }
                return;
        }
    }

    private void Execute(int cp)
    {
        switch (cp)
        {
            case 0x08: // BS
                _wrapPending = false;
                if (_x > 0) _x--;
                break;
            case 0x09: Tab(); break;
            case 0x0A: case 0x0B: case 0x0C: LineFeed(); break;
            case 0x0D: _x = 0; _wrapPending = false; break;
            case 0x0E: _shiftOut = true; break;
            case 0x0F: _shiftOut = false; break;
        }
    }

    private void EscDispatch(int cp, char inter)
    {
        switch (inter)
        {
            case '\0':
                switch (cp)
                {
                    case '7': SaveCursor(); break;
                    case '8': RestoreCursor(); break;
                    case 'D': LineFeed(); break;
                    case 'E': _x = 0; LineFeed(); break;
                    case 'H': _tabs[_x] = true; break;
                    case 'M': ReverseIndex(); break;
                    case 'c': FullReset(); break;
                }
                break;
            case '(': _g0Graphics = cp == '0'; break;
            case ')': _g1Graphics = cp == '0'; break;
            case '#':
                if (cp == '8') // DECALN screen alignment pattern
                    foreach (var line in _screen) { Array.Fill(line.Cells, new Cell { Rune = 'E' }); line.Version++; }
                break;
        }
    }

    private void BeginCsi()
    {
        _paramCount = 0;
        _cur = 0;
        _nextSub = false;
        _private = '\0';
        _inter = '\0';
    }

    private void CsiChar(int cp)
    {
        if (cp is >= '0' and <= '9') { _cur = Math.Min(_cur * 10 + (cp - '0'), 65535); return; }
        if (cp is ';' or ':') { PushParam(); _nextSub = cp == ':'; return; }
        if (cp is >= 0x3C and <= 0x3F) { if (_paramCount == 0 && _cur == 0) _private = (char)cp; return; }
        if (cp is >= 0x20 and <= 0x2F) { _inter = (char)cp; return; }
        if (cp is >= 0x40 and <= 0x7E) { PushParam(); _state = State.Ground; CsiDispatch((char)cp); return; }
        if (cp < 0x20) Execute(cp);
    }

    private void PushParam()
    {
        if (_paramCount < _params.Length)
        {
            _params[_paramCount] = _cur;
            _sub[_paramCount] = _nextSub;
            _paramCount++;
        }
        _cur = 0;
        _nextSub = false;
    }

    private int P(int i, int def) => i < _paramCount && _params[i] != 0 ? _params[i] : def;

    private void CsiDispatch(char final)
    {
        if (_private == '?')
        {
            if (_inter == '\0' && final is 'h' or 'l')
                for (int i = 0; i < _paramCount; i++) SetPrivateMode(_params[i], final == 'h');
            return;
        }
        if (_private == '>')
        {
            if (final == 'c') Respond?.Invoke("\x1b[>0;10;1c");
            return;
        }
        if (_private != '\0') return;

        if (_inter == ' ')
        {
            if (final == 'q') CursorShape = _paramCount > 0 ? _params[0] : 0; // DECSCUSR
            return;
        }
        if (_inter == '!')
        {
            if (final == 'p') SoftReset(); // DECSTR
            return;
        }
        if (_inter != '\0') return;

        switch (final)
        {
            case '@': InsertCells(_screen[_y], _x, P(0, 1)); break;
            case 'A': CursorUp(P(0, 1)); break;
            case 'B': case 'e': CursorDown(P(0, 1)); break;
            case 'C': case 'a': CursorForward(P(0, 1)); break;
            case 'D': CursorBack(P(0, 1)); break;
            case 'E': CursorDown(P(0, 1)); _x = 0; break;
            case 'F': CursorUp(P(0, 1)); _x = 0; break;
            case 'G': case '`': _x = Math.Clamp(P(0, 1) - 1, 0, Cols - 1); _wrapPending = false; break;
            case 'H': case 'f': CursorTo(P(1, 1) - 1, P(0, 1) - 1); break;
            case 'I': Tab(P(0, 1)); break;
            case 'J': EraseInDisplay(P(0, 0)); break;
            case 'K': EraseInLine(P(0, 0)); break;
            case 'L':
                if (_y >= _top && _y <= _bottom) { ScrollDownRegion(_y, _bottom, P(0, 1)); _x = 0; _wrapPending = false; }
                break;
            case 'M':
                if (_y >= _top && _y <= _bottom) { ScrollUpRegion(_y, _bottom, P(0, 1), false); _x = 0; _wrapPending = false; }
                break;
            case 'P': DeleteCells(_screen[_y], _x, P(0, 1)); _wrapPending = false; break;
            case 'S': ScrollUpRegion(_top, _bottom, P(0, 1), HistoryEligible); break;
            case 'T': if (_paramCount <= 1) ScrollDownRegion(_top, _bottom, P(0, 1)); break;
            case 'X': _screen[_y].Fill(_x, _x + P(0, 1), Erase); _wrapPending = false; break;
            case 'Z': BackTab(P(0, 1)); break;
            case 'b':
                for (int n = Math.Min(P(0, 1), 65535); n > 0; n--) Print(_lastPrinted);
                break;
            case 'c': if (P(0, 0) == 0) Respond?.Invoke("\x1b[?1;2c"); break;
            case 'd':
                _wrapPending = false;
                _y = _originMode ? Math.Clamp(P(0, 1) - 1 + _top, _top, _bottom) : Math.Clamp(P(0, 1) - 1, 0, Rows - 1);
                break;
            case 'g':
                if (P(0, 0) == 0) _tabs[_x] = false;
                else if (P(0, 0) == 3) Array.Clear(_tabs);
                break;
            case 'h': case 'l':
                if (P(0, 0) == 4) _insertMode = final == 'h';
                break;
            case 'm': Sgr(); break;
            case 'n':
                if (P(0, 0) == 5) Respond?.Invoke("\x1b[0n");
                else if (P(0, 0) == 6) Respond?.Invoke($"\x1b[{(_originMode ? _y - _top : _y) + 1};{_x + 1}R");
                break;
            case 'r':
            {
                int top = P(0, 1) - 1, bottom = P(1, Rows) - 1;
                if (top < bottom && bottom < Rows)
                {
                    _top = top;
                    _bottom = bottom;
                    CursorTo(0, 0);
                }
                break;
            }
            case 's': if (_paramCount <= 1) SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 't': if (P(0, 0) == 18) Respond?.Invoke($"\x1b[8;{Rows};{Cols}t"); break;
        }
    }

    private void SetPrivateMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1: AppCursorKeys = on; break;
            case 6: _originMode = on; CursorTo(0, 0); break;
            case 7: _autoWrap = on; if (!on) _wrapPending = false; break;
            case 25: CursorVisible = on; break;
            case 47: case 1047: SetAltScreen(on, false); break;
            case 1049: SetAltScreen(on, true); break;
            case 1000: Mouse = on ? MouseTracking.Click : MouseTracking.None; break;
            case 1002: Mouse = on ? MouseTracking.Drag : MouseTracking.None; break;
            case 1003: Mouse = on ? MouseTracking.Any : MouseTracking.None; break;
            case 1004: FocusReporting = on; break;
            case 1006: MouseSgr = on; break;
            case 2004: BracketedPaste = on; break;
        }
    }

    private void Sgr()
    {
        for (int i = 0; i < _paramCount; i++)
        {
            int p = _params[i];
            switch (p)
            {
                case 0: _pen = default; break;
                case 1: _pen.Flags |= CellFlags.Bold; break;
                case 2: _pen.Flags |= CellFlags.Dim; break;
                case 3: _pen.Flags |= CellFlags.Italic; break;
                case 4:
                    _pen.Flags &= ~(CellFlags.Underline | CellFlags.DoubleUnderline);
                    if (i + 1 < _paramCount && _sub[i + 1])
                    {
                        int style = _params[++i];
                        if (style == 2) _pen.Flags |= CellFlags.DoubleUnderline;
                        else if (style != 0) _pen.Flags |= CellFlags.Underline;
                    }
                    else _pen.Flags |= CellFlags.Underline;
                    break;
                case 5: case 6: _pen.Flags |= CellFlags.Blink; break;
                case 7: _pen.Flags |= CellFlags.Inverse; break;
                case 8: _pen.Flags |= CellFlags.Hidden; break;
                case 9: _pen.Flags |= CellFlags.Strike; break;
                case 21: _pen.Flags = (_pen.Flags & ~CellFlags.Underline) | CellFlags.DoubleUnderline; break;
                case 22: _pen.Flags &= ~(CellFlags.Bold | CellFlags.Dim); break;
                case 23: _pen.Flags &= ~CellFlags.Italic; break;
                case 24: _pen.Flags &= ~(CellFlags.Underline | CellFlags.DoubleUnderline); break;
                case 25: _pen.Flags &= ~CellFlags.Blink; break;
                case 27: _pen.Flags &= ~CellFlags.Inverse; break;
                case 28: _pen.Flags &= ~CellFlags.Hidden; break;
                case 29: _pen.Flags &= ~CellFlags.Strike; break;
                case >= 30 and <= 37: _pen.Fg = TermColor.FromIndex(p - 30); break;
                case 38: if (ExtendedColor(ref i, out var fg)) _pen.Fg = fg; break;
                case 39: _pen.Fg = TermColor.Default; break;
                case >= 40 and <= 47: _pen.Bg = TermColor.FromIndex(p - 40); break;
                case 48: if (ExtendedColor(ref i, out var bg)) _pen.Bg = bg; break;
                case 49: _pen.Bg = TermColor.Default; break;
                case 58: ExtendedColor(ref i, out _); break; // underline color: parsed and skipped
                case >= 90 and <= 97: _pen.Fg = TermColor.FromIndex(p - 90 + 8); break;
                case >= 100 and <= 107: _pen.Bg = TermColor.FromIndex(p - 100 + 8); break;
            }
        }
    }

    /// <summary>Parses 38/48/58 color forms: ;5;n  ;2;r;g;b  :5:n  :2:r:g:b  :2:cs:r:g:b.</summary>
    private bool ExtendedColor(ref int i, out uint color)
    {
        color = 0;
        if (i + 1 < _paramCount && _sub[i + 1])
        {
            int j = i + 1, end = j;
            while (end + 1 < _paramCount && _sub[end + 1]) end++;
            int kind = _params[j], values = end - j;
            i = end;
            if (kind == 5 && values >= 1) { color = TermColor.FromIndex(_params[j + 1]); return true; }
            if (kind == 2 && values >= 3)
            {
                int k = values >= 4 ? j + 2 : j + 1;
                color = TermColor.FromRgb(_params[k], _params[k + 1], _params[k + 2]);
                return true;
            }
            return false;
        }

        if (i + 1 >= _paramCount) return false;
        int mode = _params[i + 1];
        if (mode == 5 && i + 2 < _paramCount)
        {
            color = TermColor.FromIndex(_params[i + 2]);
            i += 2;
            return true;
        }
        if (mode == 2 && i + 4 < _paramCount)
        {
            color = TermColor.FromRgb(_params[i + 2], _params[i + 3], _params[i + 4]);
            i += 4;
            return true;
        }
        i += 1;
        return false;
    }

    private void OscDispatch()
    {
        var s = _osc.ToString();
        int semi = s.IndexOf(';');
        var code = semi < 0 ? s : s[..semi];
        var arg = semi < 0 ? "" : s[(semi + 1)..];
        switch (code)
        {
            case "0": case "2": Title = arg; break;
            case "10": if (arg == "?") Respond?.Invoke($"\x1b]10;{XColor(DefaultForeground)}\x1b\\"); break;
            case "11": if (arg == "?") Respond?.Invoke($"\x1b]11;{XColor(DefaultBackground)}\x1b\\"); break;
        }
    }

    private static string XColor(uint argb)
    {
        uint r = (argb >> 16) & 0xFF, g = (argb >> 8) & 0xFF, b = argb & 0xFF;
        return $"rgb:{r:x2}{r:x2}/{g:x2}{g:x2}/{b:x2}{b:x2}";
    }
}
