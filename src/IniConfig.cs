using System.Globalization;
using System.Text;

namespace WallTerm;

/// <summary>
/// config.ini: one section per topic, each headed by a short description and the values it accepts. Values go through
/// <see cref="Cli.Apply"/>, so the file and 'wallterm set' accept exactly the same input.
/// </summary>
internal static class IniConfig
{
    private sealed record Entry(string Key, Func<WallTermConfig, string> Get, Func<WallTermConfig, string, string?> Set);

    private sealed record Section(string Name, string Help, Entry[] Entries);

    private static Entry E(string key, string cliKey, Func<WallTermConfig, string> get) =>
        new(key, get, (c, v) => Cli.Apply(c, cliKey, v, out _));

    private static Entry Color(string name) =>
        new(name, c => c.Colors[name], (c, v) => Cli.Apply(c, "color", $"{name} {v}", out _));

    private static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Bool(bool v) => v ? "true" : "false";

    private static string Padding(PaddingConfig p) =>
        p.Left == p.Top && p.Top == p.Right && p.Right == p.Bottom ? Num(p.Left) : $"{Num(p.Left)},{Num(p.Top)},{Num(p.Right)},{Num(p.Bottom)}";

    private static readonly Section[] Sections =
    [
        new("Font", """
            Terminal typeface.
              Family   any installed font; monospaced ones work best (Cascadia Mono, Consolas, JetBrains Mono ...)
              Size     4 - 96, in points
            """,
        [
            E("Family", "font", c => c.FontFamily),
            E("Size", "size", c => Num(c.FontSize)),
        ]),

        new("Window", """
            How the terminal looks on the desktop.
              Opacity   0 - 1 (1 = opaque; opaque black draws the least power on OLED)
              Backdrop  None | Acrylic | Mica (Windows only shows these while WallTerm has focus)
              Padding   pixels around the text: all | horizontal,vertical | left,top,right,bottom
              Hotkey    summon / send back: Alt+` | Ctrl+Alt+T | Win+F12 | ... | None
            """,
        [
            E("Opacity", "opacity", c => Num(c.Opacity)),
            E("Backdrop", "backdrop", c => c.BackdropEffect),
            E("Padding", "padding", c => Padding(c.Padding)),
            E("Hotkey", "hotkey", c => c.Hotkey),
        ]),

        new("Layout", """
            Keep the terminal out from under other windows: it shrinks to the largest free part of the screen and the
            shell re-wraps its text to fit.
              Avoid    Snapped  windows snapped to the screen edges (halves, quarters, Snap layouts)
                       All      snapped and floating windows
                       Off      always cover the whole screen
            """,
        [
            E("Avoid", "avoid", c => c.AvoidWindows),
        ]),

        new("Cursor", """
            Text cursor.
              Style    Block | Beam | Underline
              Blink    true | false
            """,
        [
            E("Style", "cursor", c => c.CursorStyle),
            E("Blink", "blink", c => Bool(c.CursorBlink)),
        ]),

        new("Colors", """
            #RRGGBB or #AARRGGBB.
              Background                        window background; its alpha is multiplied by Window.Opacity
              Foreground, Cursor, Selection     default text, cursor and mouse selection (give Selection some alpha)
              Black ... BrightWhite             the 16 ANSI colors
            """,
        [
            new("Background", c => c.BackgroundColor, (c, v) => Cli.Apply(c, "color", "background " + v, out _)),
            Color("Foreground"), Color("Cursor"), Color("Selection"),
            .. WallTermConfig.AnsiNames.Select(Color),
        ]),

        new("Shell", """
            The program running in the terminal. Program, Arguments and StartDirectory apply to the next session.
              Program          pwsh.exe (falls back to powershell.exe) | cmd.exe | full path to any console program
              Arguments        command-line arguments for Program
              StartDirectory   starting folder; %VARIABLES% are expanded
              Scrollback       0 - 200000 lines of history
              Editor           editor for 'wallterm settings': auto ($env:EDITOR, then edit, nvim, vim, nano, notepad)
                               | a program name or path, with arguments if needed
            """,
        [
            E("Program", "shell", c => c.Shell),
            E("Arguments", "args", c => c.ShellArgs),
            E("StartDirectory", "dir", c => c.StartDirectory),
            E("Scrollback", "scrollback", c => c.Scrollback.ToString(CultureInfo.InvariantCulture)),
            E("Editor", "editor", c => c.Editor.Length == 0 ? "auto" : c.Editor),
        ]),

        new("BurnIn", """
            OLED burn-in protection: shift the text a few pixels at random intervals.
              Wiggle       true | false
              MinSeconds   10 - 3600, shortest time between shifts
              MaxSeconds   MinSeconds - 7200, longest time between shifts
              Pixels       1 - 8, how far the text moves
            """,
        [
            E("Wiggle", "wiggle", c => Bool(c.Wiggle)),
            E("MinSeconds", "wiggle-min", c => c.WiggleMinSeconds.ToString(CultureInfo.InvariantCulture)),
            E("MaxSeconds", "wiggle-max", c => c.WiggleMaxSeconds.ToString(CultureInfo.InvariantCulture)),
            E("Pixels", "wiggle-pixels", c => c.WigglePixels.ToString(CultureInfo.InvariantCulture)),
        ]),
    ];

    private const string Header = """
        ; WallTerm settings. Changes apply as soon as the file is saved.
        ; Edit with 'wallterm settings', or change one value with 'wallterm set <key> <value>'.
        ; Lines starting with ; or # are comments. A missing or invalid value uses its default.
        """;

    /// <summary>Reads settings from config.ini lines. Unknown keys and invalid values are reported, never fatal.</summary>
    public static WallTermConfig Parse(IEnumerable<string> lines, List<string> warnings)
    {
        var config = new WallTermConfig();
        Section? section = null;
        int n = 0;
        foreach (var raw in lines)
        {
            n++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[')
            {
                var name = line.TrimStart('[').TrimEnd(']').Trim();
                section = Sections.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (section == null) warnings.Add($"line {n}: unknown section [{name}]");
                continue;
            }
            if (section == null) continue;
            if (!TrySplit(line, out var key, out var value))
            {
                warnings.Add($"line {n}: expected Key = value");
                continue;
            }
            var entry = section.Entries.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                warnings.Add($"line {n}: unknown setting '{key}' in [{section.Name}]");
                continue;
            }
            if (value.Length == 0 && entry.Key != "Arguments") continue; // empty = default
            if (entry.Set(config, value) is { } error) warnings.Add($"line {n}: {section.Name}.{entry.Key}: {error}");
        }
        return config;
    }

    /// <summary>
    /// Returns config.ini text holding <paramref name="config"/>. With existing lines, only values that changed are
    /// rewritten (comments and layout kept); missing keys and sections are added. Without, a fully commented file.
    /// </summary>
    public static string Update(string[]? existing, WallTermConfig config)
    {
        if (existing == null)
        {
            var sb = new StringBuilder(Header).Append("\r\n");
            foreach (var s in Sections) AppendSection(sb.Append("\r\n"), s, config);
            return sb.ToString();
        }

        var lines = existing.ToList();
        foreach (var section in Sections)
        {
            int start = lines.FindIndex(l => IsHeader(l, section.Name));
            if (start < 0)
            {
                var block = new StringBuilder();
                AppendSection(block, section, config);
                if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
                lines.AddRange(block.ToString().TrimEnd().Split("\r\n"));
                continue;
            }
            int end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
            if (end < 0) end = lines.Count;

            foreach (var entry in section.Entries)
            {
                var value = entry.Get(config);
                int at = -1;
                for (int i = start + 1; i < end && at < 0; i++)
                    if (TrySplit(lines[i].Trim(), out var k, out _) && k.Equals(entry.Key, StringComparison.OrdinalIgnoreCase) && !IsComment(lines[i]))
                        at = i;
                if (at >= 0)
                {
                    TrySplit(lines[at].Trim(), out var key, out var old);
                    if (old != value) lines[at] = $"{key} = {value}";
                    continue;
                }
                // Missing: add it after the section's last setting line.
                int insert = end;
                while (insert > start + 1 && (lines[insert - 1].Trim().Length == 0 || IsComment(lines[insert - 1]))) insert--;
                lines.Insert(insert, $"{entry.Key} = {value}");
                end++;
            }
        }
        return string.Join("\r\n", lines) + "\r\n";
    }

    private static void AppendSection(StringBuilder sb, Section section, WallTermConfig config)
    {
        foreach (var help in section.Help.Split('\n'))
            sb.Append("; ").Append(help.TrimEnd('\r')).Append("\r\n");
        sb.Append('[').Append(section.Name).Append("]\r\n");
        foreach (var e in section.Entries)
            sb.Append(e.Key).Append(" = ").Append(e.Get(config)).Append("\r\n");
    }

    private static bool IsHeader(string line, string name)
    {
        var t = line.Trim();
        return t.StartsWith('[') && t.TrimStart('[').TrimEnd(']').Trim().Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsComment(string line) => line.TrimStart() is { Length: > 0 } t && t[0] is ';' or '#';

    /// <summary>Splits "Key = value", dropping one pair of quotes around the whole value.</summary>
    private static bool TrySplit(string line, out string key, out string value)
    {
        int eq = line.IndexOf('=');
        key = eq > 0 ? line[..eq].Trim() : "";
        value = eq > 0 ? line[(eq + 1)..].Trim() : "";
        if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0] && value.IndexOf(value[0], 1) == value.Length - 1)
            value = value[1..^1];
        return key.Length > 0;
    }
}
