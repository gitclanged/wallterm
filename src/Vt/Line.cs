using System.Globalization;

namespace WallTerm.Vt;

[Flags]
internal enum CellFlags : ushort
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    DoubleUnderline = 1 << 4,
    Blink = 1 << 5,
    Inverse = 1 << 6,
    Hidden = 1 << 7,
    Strike = 1 << 8,
    Wide = 1 << 9,      // first half of a double-width character
    WideTail = 1 << 10, // placeholder cell covered by the preceding wide character

    LayoutMask = Wide | WideTail,
}

/// <summary>Colors are tagged: 0 = default, 0x01xxxxxx = palette index, 0x02RRGGBB = 24-bit color.</summary>
internal static class TermColor
{
    public const uint Default = 0;
    public const uint Indexed = 0x01000000;
    public const uint Rgb = 0x02000000;
    public const uint TagMask = 0xFF000000;

    public static uint FromIndex(int index) => Indexed | (uint)(index & 0xFF);
    public static uint FromRgb(int r, int g, int b) => Rgb | (uint)((r & 0xFF) << 16 | (g & 0xFF) << 8 | (b & 0xFF));
}

internal struct Cell
{
    public int Rune; // Unicode scalar; 0 = empty
    public uint Fg;
    public uint Bg;
    public CellFlags Flags;
}

internal sealed class Line
{
    public Cell[] Cells;
    public bool Wrapped;
    /// <summary>Bumped on every mutation so the renderer can skip unchanged rows.</summary>
    public int Version;
    /// <summary>Combining marks / ZWJ sequences attached to a column (rare, so stored sparsely).</summary>
    public Dictionary<int, string>? Combining;

    public Line(int cols) => Cells = new Cell[cols];

    public int Length => Cells.Length;

    public void Reset(int cols, in Cell blank)
    {
        if (Cells.Length != cols) Cells = new Cell[cols];
        Array.Fill(Cells, blank);
        Wrapped = false;
        Combining = null;
        Version++;
    }

    public void Fill(int from, int to, in Cell blank)
    {
        from = Math.Max(0, from);
        to = Math.Min(Cells.Length, to);
        if (from >= to) return;
        // Never leave half of a wide character behind.
        if (from > 0 && (Cells[from].Flags & CellFlags.WideTail) != 0) from--;
        if (to < Cells.Length && (Cells[to].Flags & CellFlags.WideTail) != 0) Cells[to] = blank;
        Array.Fill(Cells, blank, from, to - from);
        if (Combining != null)
            for (int c = from; c < to; c++) Combining.Remove(c);
        Version++;
    }

    public void Resize(int cols)
    {
        if (Cells.Length == cols) return;
        var cells = new Cell[cols];
        Array.Copy(Cells, cells, Math.Min(cols, Cells.Length));
        if (cols > 0 && (cells[cols - 1].Flags & CellFlags.Wide) != 0) cells[cols - 1] = default;
        Cells = cells;
        if (Combining != null)
            foreach (var k in Combining.Keys.Where(k => k >= cols).ToArray()) Combining.Remove(k);
        Version++;
    }

    public bool IsBlank()
    {
        foreach (ref readonly var c in Cells.AsSpan())
            if (c.Rune != 0 || c.Bg != 0) return false;
        return true;
    }
}

/// <summary>Terminal column width of a Unicode scalar (wcwidth): 0 for combining/format, 2 for East Asian wide and emoji.</summary>
internal static class CharWidth
{
    private static readonly int[] Wide =
    {
        0x1100, 0x115F, 0x231A, 0x231B, 0x2329, 0x232A, 0x23E9, 0x23EC, 0x23F0, 0x23F0, 0x23F3, 0x23F3,
        0x25FD, 0x25FE, 0x2614, 0x2615, 0x2648, 0x2653, 0x267F, 0x267F, 0x2693, 0x2693, 0x26A1, 0x26A1,
        0x26AA, 0x26AB, 0x26BD, 0x26BE, 0x26C4, 0x26C5, 0x26CE, 0x26CE, 0x26D4, 0x26D4, 0x26EA, 0x26EA,
        0x26F2, 0x26F3, 0x26F5, 0x26F5, 0x26FA, 0x26FA, 0x26FD, 0x26FD, 0x2705, 0x2705, 0x270A, 0x270B,
        0x2728, 0x2728, 0x274C, 0x274C, 0x274E, 0x274E, 0x2753, 0x2755, 0x2757, 0x2757, 0x2795, 0x2797,
        0x27B0, 0x27B0, 0x27BF, 0x27BF, 0x2B1B, 0x2B1C, 0x2B50, 0x2B50, 0x2B55, 0x2B55, 0x2E80, 0x303E,
        0x3041, 0x33FF, 0x3400, 0x4DBF, 0x4E00, 0x9FFF, 0xA000, 0xA4CF, 0xA960, 0xA97F, 0xAC00, 0xD7A3,
        0xF900, 0xFAFF, 0xFE10, 0xFE19, 0xFE30, 0xFE6F, 0xFF00, 0xFF60, 0xFFE0, 0xFFE6, 0x16FE0, 0x16FE4,
        0x17000, 0x18AFF, 0x1B000, 0x1B2FF, 0x1F004, 0x1F004, 0x1F0CF, 0x1F0CF, 0x1F18E, 0x1F18E,
        0x1F191, 0x1F19A, 0x1F200, 0x1F251, 0x1F300, 0x1F320, 0x1F32D, 0x1F335, 0x1F337, 0x1F37C,
        0x1F37E, 0x1F393, 0x1F3A0, 0x1F3CA, 0x1F3CF, 0x1F3D3, 0x1F3E0, 0x1F3F0, 0x1F3F4, 0x1F3F4,
        0x1F3F8, 0x1F43E, 0x1F440, 0x1F440, 0x1F442, 0x1F4FC, 0x1F4FF, 0x1F53D, 0x1F54B, 0x1F54E,
        0x1F550, 0x1F567, 0x1F57A, 0x1F57A, 0x1F595, 0x1F596, 0x1F5A4, 0x1F5A4, 0x1F5FB, 0x1F64F,
        0x1F680, 0x1F6C5, 0x1F6CC, 0x1F6CC, 0x1F6D0, 0x1F6D2, 0x1F6D5, 0x1F6D7, 0x1F6DC, 0x1F6DF,
        0x1F6EB, 0x1F6EC, 0x1F6F4, 0x1F6FC, 0x1F7E0, 0x1F7EB, 0x1F7F0, 0x1F7F0, 0x1F90C, 0x1F93A,
        0x1F93C, 0x1F945, 0x1F947, 0x1F9FF, 0x1FA70, 0x1FAFF, 0x20000, 0x2FFFD, 0x30000, 0x3FFFD,
    };

    public static int Of(int cp)
    {
        if (cp < 0x7F) return cp >= 0x20 ? 1 : 0;
        if (cp < 0xA0) return 0;
        if (cp == 0xAD) return 1;
        if (cp is >= 0x1160 and <= 0x11FF) return 0; // Hangul medial vowels / final consonants
        var cat = CharUnicodeInfo.GetUnicodeCategory(cp);
        if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        return IsWide(cp) ? 2 : 1;
    }

    private static bool IsWide(int cp)
    {
        if (cp < Wide[0]) return false;
        int lo = 0, hi = Wide.Length / 2 - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (cp < Wide[mid * 2]) hi = mid - 1;
            else if (cp > Wide[mid * 2 + 1]) lo = mid + 1;
            else return true;
        }
        return false;
    }
}
