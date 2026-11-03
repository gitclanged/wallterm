using System.Text;

namespace WallTerm;

/// <summary>Resolves the configured shell (pwsh → powershell fallback) and builds its command line.</summary>
internal static class ShellCommand
{
    public static string Build(WallTermConfig config, out string workingDirectory)
    {
        var exe = Resolve(config.Shell) ?? throw new FileNotFoundException(
            $"Shell '{config.Shell}' was not found (also tried pwsh.exe and powershell.exe). Set one with: wallterm set shell <path>");
        var args = config.ShellArgs.Trim();

        var name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
        if (name is "pwsh" or "powershell" && !HasScriptArgument(args))
        {
            // Define a 'wallterm' function in the session so 'wallterm set …' runs synchronously and prints inline
            // (a GUI-subsystem exe would otherwise return to the prompt before its output appears).
            var self = (Environment.ProcessPath ?? "WallTerm.exe").Replace("'", "''");
            var script = $"function global:wallterm {{ & '{self}' @args | Out-Host }}";
            args = $"{args} -NoExit -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}".Trim();
        }

        var dir = Environment.ExpandEnvironmentVariables(config.StartDirectory.Trim().Trim('"'));
        workingDirectory = Directory.Exists(dir) ? dir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return $"{Elevation.Quote(exe)} {args}".Trim();
    }

    private static bool HasScriptArgument(string args)
    {
        foreach (var token in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.ToLowerInvariant();
            if (t is "-c" or "-command" or "-f" or "-file" or "-e" or "-ec" or "-encodedcommand" or "-noexit" or "-commandwithargs" or "-cwa")
                return true;
        }
        return false;
    }

    public static string? Resolve(string configured)
    {
        var candidates = new List<string> { Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"')) };
        var name = Path.GetFileNameWithoutExtension(candidates[0]).ToLowerInvariant();
        if (name is "pwsh" or "powershell")
        {
            candidates.Add("pwsh.exe");
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "pwsh.exe"));
            candidates.Add(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"));
        }

        foreach (var candidate in candidates)
            if (Find(candidate) is { } found) return found;
        return null;
    }

    private static string? Find(string file)
    {
        if (Path.IsPathRooted(file)) return File.Exists(file) ? file : null;
        var withExt = Path.HasExtension(file) ? file : file + ".exe";
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Prepend(Environment.SystemDirectory);
        foreach (var dir in dirs)
        {
            try
            {
                var path = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim('"')), withExt);
                if (File.Exists(path)) return path;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}
