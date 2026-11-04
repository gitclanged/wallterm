using System.Diagnostics;
using System.Text;

namespace WallTerm;

/// <summary>Picks the text editor 'wallterm settings' opens config.ini in.</summary>
internal static class EditorCommand
{
    // Terminal editors first (edit.exe ships with Windows 11), Notepad as the last resort.
    private static readonly string[] AutoCandidates = ["edit", "nvim", "vim", "nano", "micro", "hx"];

    /// <summary>The editor program and its arguments (without the file to open).</summary>
    public static (string Exe, List<string> Args) Resolve(WallTermConfig config)
    {
        var spec = config.Editor.Length == 0 || config.Editor.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable("VISUAL") ?? Environment.GetEnvironmentVariable("EDITOR")
            : config.Editor;
        if (!string.IsNullOrWhiteSpace(spec))
        {
            var expanded = Environment.ExpandEnvironmentVariables(spec.Trim());
            // A path with spaces but no arguments ("C:\Program Files\...\nvim.exe") is one token.
            if (ShellCommand.Find(expanded.Trim('"')) is { } whole) return (whole, []);
            var tokens = Tokenize(expanded);
            if (tokens.Count > 0) return (ShellCommand.Find(tokens[0]) ?? tokens[0], tokens.Skip(1).ToList());
        }
        foreach (var name in AutoCandidates)
            if (ShellCommand.Find(name) is { } found) return (found, []);
        return ("notepad.exe", []);
    }

    /// <summary>
    /// Opens config.ini in its own console window. Used when WallTerm.exe is run directly; inside WallTerm the injected
    /// 'wallterm' function runs the editor in the current terminal instead (see <see cref="ShellCommand"/>).
    /// </summary>
    public static string OpenInNewWindow(WallTermConfig config)
    {
        var (exe, args) = Resolve(config);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(ConfigStore.FilePath);
        Process.Start(psi)?.Dispose();
        return $"Opened {ConfigStore.FilePath} in {Path.GetFileName(exe)}.";
    }

    private static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false, any = false;
        foreach (var ch in s)
        {
            if (ch == '"') { quoted = !quoted; any = true; }
            else if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (any) tokens.Add(cur.ToString());
                cur.Clear();
                any = false;
            }
            else { cur.Append(ch); any = true; }
        }
        if (any) tokens.Add(cur.ToString());
        return tokens;
    }
}
