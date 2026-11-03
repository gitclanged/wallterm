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
            var script = $"function global:wallterm {{ & '{self}' @args | Out-Host }}\n{RunFunction}";
            args = $"{args} -NoExit -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}".Trim();
        }

        var dir = Environment.ExpandEnvironmentVariables(config.StartDirectory.Trim().Trim('"'));
        workingDirectory = Directory.Exists(dir) ? dir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return $"{Elevation.Quote(exe)} {args}".Trim();
    }

    // 'run <app>' starts anything listed in Start (Win32 and Store apps) by name: exact match, then prefix, then
    // substring, then all words; the shortest name wins. ('start' is taken: it's PowerShell's alias for Start-Process.)
    // Launching through explorer.exe hands the start to the user's Explorer, so the app runs un-elevated.
    private const string RunFunction = """
        function global:Get-WallTermApps {
            (New-Object -ComObject Shell.Application).NameSpace('shell:::{4234d49b-0245-4df3-b780-3893943456e1}').Items() |
                ForEach-Object { [pscustomobject]@{ Name = $_.Name; Id = $_.Path } } | Sort-Object Name
        }
        function global:run {
            param([Parameter(Position = 0, ValueFromRemainingArguments)][string[]]$Name, [switch]$List)
            $query = ($Name -join ' ').Trim()
            $apps = Get-WallTermApps
            $q = [WildcardPattern]::Escape($query)
            if ($List -or -not $query) { return ($apps | Where-Object { $_.Name -like "*$q*" }).Name }
            $words = @($query -split '\s+' | ForEach-Object { [WildcardPattern]::Escape($_) })
            $match = @($apps | Where-Object Name -eq $query)
            if (-not $match) { $match = @($apps | Where-Object { $_.Name -like "$q*" }) }
            if (-not $match) { $match = @($apps | Where-Object { $_.Name -like "*$q*" }) }
            if (-not $match) { $match = @($apps | Where-Object { $n = $_.Name; -not ($words | Where-Object { $n -notlike "*$_*" }) }) }
            if (-not $match) { Write-Error "No installed app matches '$query'. List them with: run -List"; return }
            $app = $match | Sort-Object { $_.Name.Length }, Name | Select-Object -First 1
            Write-Host "Starting $($app.Name)"
            explorer.exe "shell:AppsFolder\$($app.Id)"
        }
        Register-ArgumentCompleter -CommandName run -ParameterName Name -ScriptBlock {
            param($cmd, $param, $word)
            $w = [WildcardPattern]::Escape($word.Trim('''"'))
            (Get-WallTermApps | Where-Object { $_.Name -like "*$w*" }).Name | ForEach-Object {
                $text = if ($_ -match '[\s''`$(){}@,;&|]') { "'" + ($_ -replace "'", "''") + "'" } else { $_ }
                [System.Management.Automation.CompletionResult]::new($text, $_, 'ParameterValue', $_)
            }
        }
        """;

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
