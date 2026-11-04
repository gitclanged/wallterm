using System.Globalization;
using System.Reflection;

namespace WallTerm;

/// <summary>
/// Command-line interface. Runs from any shell (including the one inside WallTerm); config changes are written to
/// config.ini and the running instance hot-reloads them via its file watcher.
/// </summary>
internal static class Cli
{
    private const string Usage = """
        WallTerm — live PowerShell terminal on the desktop background

        Usage:
          wallterm                          Start (or summon the running instance)
          wallterm settings                 Edit config.ini in a terminal text editor (every setting is documented there)
          wallterm set <key> <value>        Change a setting; the running terminal reloads it live
          wallterm set color <name> <#hex>  Change a color (background, foreground, cursor, selection, red, brightblue, 0-15 …)
          wallterm get [key]                Show the config file (or one key)
          wallterm check                    Report mistakes in config.ini
          wallterm reset                    Restore default settings (rewrites config.ini)
          wallterm path                     Print the config file path
          wallterm quit                     Close the running instance
          wallterm --install-startup        Start elevated at every logon (Task Scheduler, no UAC prompt)
          wallterm --uninstall-startup      Remove the logon task
          wallterm --version | --help

        Keys:
          font <family>          size <points>          opacity <0..1>          backdrop <none|acrylic|mica>
          padding <all | h,v | l,t,r,b>                 cursor <block|beam|underline>   blink <true|false>
          wiggle <true|false>    wiggle-min <seconds>   wiggle-max <seconds>    wiggle-pixels <1..8>
          scrollback <lines>     shell <exe>            args <arguments>        dir <start directory>
          hotkey <e.g. Alt+` | Ctrl+Alt+T | none>       avoid <snapped|all|off>         editor <auto | program>

        Examples:
          wallterm set font "JetBrains Mono"
          wallterm set size 15
          wallterm set opacity 0.85
          wallterm set wiggle true
          wallterm set avoid all                 (also make room for floating windows)
          wallterm set color background 000000   (in PowerShell, quote a leading #: '#000000')
        """;

    /// <summary>Returns an exit code, or null when the GUI should start.</summary>
    public static int? Run(string[] args)
    {
        if (args.Length == 0) return null;
        var cmd = args[0].TrimStart('-', '/').ToLowerInvariant();
        switch (cmd)
        {
            case "no-elevate":
                return args.Length == 1 ? null : Fail($"unexpected arguments after {args[0]}");
            case "help": case "h": case "?":
                return Print(Usage);
            case "version": case "v":
                return Print("WallTerm " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"));
            case "path":
                return Print(ConfigStore.FilePath);
            case "get":
                return Get(args.Skip(1).ToArray());
            case "set":
                return Set(args.Skip(1).ToArray());
            case "reset":
                ConfigStore.Reset();
                return Print($"Defaults restored ({ConfigStore.FilePath}).");
            case "settings": case "config": case "edit":
                return Settings(args.Skip(1).ToArray());
            case "check":
                return Check();
            case "quit": case "exit": case "stop":
                return Quit();
            case "install-startup":
                return Elevated(args, () => Print(StartupTask.Install()));
            case "uninstall-startup":
                return Elevated(args, () => Print(StartupTask.Uninstall()));
            default:
                return Fail($"unknown command '{args[0]}'. Run 'wallterm help'.");
        }
    }

    private static int Elevated(string[] args, Func<int> action)
    {
        if (!Elevation.IsAdministrator())
            return Elevation.RelaunchElevated(args) ? 0 : Fail("administrator rights are required.");
        try { return action(); }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    private static int Quit()
    {
        if (!EventWaitHandle.TryOpenExisting(Program.QuitEventName, out var quit))
            return Print("WallTerm is not running.");
        using (quit) quit.Set();
        return Print("WallTerm closed.");
    }

    /// <summary>
    /// Opens config.ini in an editor. "--resolve" (used by the injected 'wallterm' function, which runs the editor in
    /// its own terminal) prints the editor command line instead, one argument per line, the file last.
    /// </summary>
    private static int Settings(string[] args)
    {
        var config = ConfigStore.LoadOrCreate();
        if (!File.Exists(ConfigStore.FilePath)) return Fail($"could not create {ConfigStore.FilePath}.");
        if (args is ["--resolve"])
        {
            var (exe, editorArgs) = EditorCommand.Resolve(config);
            return Print(string.Join("\n", editorArgs.Prepend(exe).Append(ConfigStore.FilePath)));
        }
        if (args.Length > 0) return Fail("usage: wallterm settings");
        try { return Print(EditorCommand.OpenInNewWindow(config)); }
        catch (Exception ex) { return Fail($"could not start the editor: {ex.Message}. Set one with: wallterm set editor <program>"); }
    }

    private static int Check()
    {
        if (!ConfigStore.TryLoad(out _, out var error, out var warnings))
            return Fail($"{ConfigStore.FilePath}: {error}");
        if (warnings.Count == 0) return Print("Settings OK.");
        return Fail($"{ConfigStore.FileName} has problems (these use their defaults):\n  " + string.Join("\n  ", warnings));
    }

    private static int Get(string[] args)
    {
        var config = ConfigStore.LoadOrCreate();
        if (args.Length == 0)
            return File.Exists(ConfigStore.FilePath) ? Print(File.ReadAllText(ConfigStore.FilePath).TrimEnd()) : Print(IniConfig.Update(null, config));
        var key = string.Join(' ', args).Trim().ToLowerInvariant();
        string? value = key switch
        {
            "font" or "fontfamily" => config.FontFamily,
            "size" or "fontsize" => config.FontSize.ToString(CultureInfo.InvariantCulture),
            "opacity" => config.Opacity.ToString(CultureInfo.InvariantCulture),
            "backdrop" or "backdropeffect" => config.BackdropEffect,
            "padding" => $"{config.Padding.Left},{config.Padding.Top},{config.Padding.Right},{config.Padding.Bottom}",
            "cursor" or "cursorstyle" => config.CursorStyle,
            "blink" or "cursorblink" => config.CursorBlink.ToString().ToLowerInvariant(),
            "wiggle" => config.Wiggle.ToString().ToLowerInvariant(),
            "wiggle-min" => config.WiggleMinSeconds.ToString(CultureInfo.InvariantCulture),
            "wiggle-max" => config.WiggleMaxSeconds.ToString(CultureInfo.InvariantCulture),
            "wiggle-pixels" => config.WigglePixels.ToString(CultureInfo.InvariantCulture),
            "scrollback" => config.Scrollback.ToString(CultureInfo.InvariantCulture),
            "shell" => config.Shell,
            "args" or "shellargs" => config.ShellArgs,
            "dir" or "startdirectory" => config.StartDirectory,
            "hotkey" => config.Hotkey,
            "avoid" or "avoidwindows" => config.AvoidWindows,
            "editor" => config.Editor.Length == 0 ? "auto" : config.Editor,
            "color background" or "background" or "backgroundcolor" => config.BackgroundColor,
            _ when key.StartsWith("color ", StringComparison.Ordinal) =>
                ColorKey(key[6..].Trim()) is { } ck && config.Colors.TryGetValue(ck, out var cv) ? cv : null,
            _ => null,
        };
        return value == null ? Fail($"unknown key '{key}'.") : Print(value);
    }

    private static int Set(string[] args)
    {
        if (args.Length < 2) return Fail("usage: wallterm set <key> <value>   (see 'wallterm help')");

        if (!ConfigStore.TryLoad(out var config, out var loadError))
        {
            if (File.Exists(ConfigStore.FilePath))
                return Fail($"{ConfigStore.FilePath} could not be read ({loadError}); fix or run 'wallterm reset'.");
            config = new WallTermConfig().Normalize();
        }

        var key = args[0].ToLowerInvariant();
        var value = string.Join(' ', args.Skip(1)).Trim();
        string? error = Apply(config, key, value, out var applied);
        if (error != null) return Fail(error);

        config.Normalize();
        try { ConfigStore.Save(config); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Fail($"could not save config: {ex.Message}"); }
        return Print(applied);
    }

    /// <summary>Applies one setting. Returns an error message, or null on success.</summary>
    internal static string? Apply(WallTermConfig c, string key, string value, out string applied)
    {
        applied = "";
        switch (key)
        {
            case "font": case "fontfamily":
                c.FontFamily = value.Trim('"', '\'');
                applied = $"font = {c.FontFamily}";
                return null;

            case "size": case "fontsize":
                if (!TryDouble(value, out var size) || size is < 4 or > 96) return "size must be a number between 4 and 96.";
                c.FontSize = size;
                applied = $"size = {size.ToString(CultureInfo.InvariantCulture)}";
                return null;

            case "opacity":
                if (!TryDouble(value.TrimEnd('%'), out var op)) return "opacity must be between 0 and 1 (or 0%..100%).";
                if (value.EndsWith('%') || op > 1) op /= 100;
                if (op is < 0 or > 1) return "opacity must be between 0 and 1.";
                c.Opacity = op;
                applied = $"opacity = {op.ToString(CultureInfo.InvariantCulture)}";
                return null;

            case "backdrop": case "backdropeffect":
                if (!OneOf(value, out var bd, "None", "Acrylic", "Mica")) return "backdrop must be none, acrylic or mica.";
                c.BackdropEffect = bd;
                applied = $"backdrop = {bd}";
                return null;

            case "padding":
            {
                var parts = value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
                var nums = new double[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                    if (!TryDouble(parts[i], out nums[i]) || nums[i] < 0) return "padding must be non-negative numbers.";
                switch (nums.Length)
                {
                    case 1: c.Padding = new PaddingConfig { Left = nums[0], Top = nums[0], Right = nums[0], Bottom = nums[0] }; break;
                    case 2: c.Padding = new PaddingConfig { Left = nums[0], Top = nums[1], Right = nums[0], Bottom = nums[1] }; break;
                    case 4: c.Padding = new PaddingConfig { Left = nums[0], Top = nums[1], Right = nums[2], Bottom = nums[3] }; break;
                    default: return "padding takes 1 (all), 2 (horizontal,vertical) or 4 (left,top,right,bottom) values.";
                }
                applied = $"padding = {c.Padding.Left},{c.Padding.Top},{c.Padding.Right},{c.Padding.Bottom}";
                return null;
            }

            case "cursor": case "cursorstyle":
                if (value.Equals("bar", StringComparison.OrdinalIgnoreCase)) value = "Beam";
                if (!OneOf(value, out var cs, "Block", "Beam", "Underline")) return "cursor must be block, beam or underline.";
                c.CursorStyle = cs;
                applied = $"cursor = {cs}";
                return null;

            case "blink": case "cursorblink":
                if (!TryBool(value, out var blink)) return "blink must be true or false.";
                c.CursorBlink = blink;
                applied = $"blink = {blink.ToString().ToLowerInvariant()}";
                return null;

            case "wiggle": case "burnin": case "oled":
                if (!TryBool(value, out var wiggle)) return "wiggle must be true or false.";
                c.Wiggle = wiggle;
                applied = $"wiggle = {wiggle.ToString().ToLowerInvariant()}";
                return null;

            case "wiggle-min":
                if (!int.TryParse(value, out var wmin) || wmin < 10) return "wiggle-min must be at least 10 seconds.";
                c.WiggleMinSeconds = wmin;
                if (c.WiggleMaxSeconds < wmin) c.WiggleMaxSeconds = wmin;
                applied = $"wiggle-min = {wmin}s";
                return null;

            case "wiggle-max":
                if (!int.TryParse(value, out var wmax) || wmax < 10) return "wiggle-max must be at least 10 seconds.";
                c.WiggleMaxSeconds = wmax;
                if (c.WiggleMinSeconds > wmax) c.WiggleMinSeconds = wmax;
                applied = $"wiggle-max = {wmax}s";
                return null;

            case "wiggle-pixels":
                if (!int.TryParse(value, out var px) || px is < 1 or > 8) return "wiggle-pixels must be 1..8.";
                c.WigglePixels = px;
                applied = $"wiggle-pixels = {px}";
                return null;

            case "scrollback":
                if (!int.TryParse(value, out var sb) || sb is < 0 or > 200_000) return "scrollback must be 0..200000 lines.";
                c.Scrollback = sb;
                applied = $"scrollback = {sb}";
                return null;

            case "shell":
                c.Shell = value.Trim('"');
                applied = $"shell = {c.Shell}  (applies to the next shell session)";
                return null;

            case "args": case "shellargs":
                c.ShellArgs = value;
                applied = $"args = {value}  (applies to the next shell session)";
                return null;

            case "dir": case "startdirectory":
                c.StartDirectory = value.Trim('"');
                applied = $"dir = {c.StartDirectory}  (applies to the next shell session)";
                return null;

            case "hotkey":
                if (!value.Equals("none", StringComparison.OrdinalIgnoreCase) && !Hotkey.TryParse(value, out _, out _))
                    return $"'{value}' is not a valid hotkey (examples: Alt+`, Ctrl+Alt+T, Win+F12, none).";
                c.Hotkey = value;
                applied = $"hotkey = {value}";
                return null;

            case "avoid": case "avoidwindows":
            {
                var v = value.Trim().ToLowerInvariant() switch
                {
                    "none" or "false" or "no" or "0" => "Off",
                    "true" or "yes" or "1" or "snap" => "Snapped",
                    "floating" or "windows" => "All",
                    _ => value,
                };
                if (!OneOf(v, out var avoid, "Off", "Snapped", "All")) return "avoid must be snapped, all or off.";
                c.AvoidWindows = avoid;
                applied = $"avoid = {avoid}";
                return null;
            }

            case "editor":
                c.Editor = value.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "" : value;
                applied = $"editor = {(c.Editor.Length == 0 ? "auto" : c.Editor)}";
                return null;

            case "background": case "backgroundcolor":
                return Apply(c, "color", "background " + value, out applied);

            case "color": case "colour":
            {
                var parts = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length != 2)
                    return "usage: wallterm set color <name> <RRGGBB | AARRGGBB>\n" +
                           "  note: in PowerShell an unquoted '#' starts a comment, so write '#050508' in quotes or just 050508.";
                var hex = parts[1].StartsWith('#') ? parts[1] : "#" + parts[1];
                if (!ColorUtil.TryParse(hex, out _)) return $"'{parts[1]}' is not a color (use #RRGGBB or #AARRGGBB).";
                var name = parts[0].ToLowerInvariant();
                if (name is "background" or "bg")
                {
                    c.BackgroundColor = hex.ToUpperInvariant();
                    applied = $"background = {c.BackgroundColor}";
                    return null;
                }
                var colorKey = ColorKey(name);
                if (colorKey == null)
                    return $"unknown color '{parts[0]}'. Use background, foreground, cursor, selection, {string.Join(", ", WallTermConfig.AnsiNames).ToLowerInvariant()} or 0-15.";
                c.Colors[colorKey] = hex.ToUpperInvariant();
                applied = $"{colorKey} = {hex.ToUpperInvariant()}";
                return null;
            }

            default:
                return $"unknown key '{key}'. Run 'wallterm help' for the list of settings.";
        }
    }

    private static string? ColorKey(string name)
    {
        if (int.TryParse(name, out var idx) && idx is >= 0 and < 16) return WallTermConfig.AnsiNames[idx];
        var n = name.Replace("-", "").Replace("_", "").Replace(" ", "");
        if (n is "fg") n = "foreground";
        string[] special = ["Foreground", "Cursor", "Selection"];
        return special.Concat(WallTermConfig.AnsiNames).FirstOrDefault(k => k.Equals(n, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryDouble(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

    private static bool TryBool(string s, out bool v)
    {
        switch (s.Trim().ToLowerInvariant())
        {
            case "true": case "on": case "yes": case "1": case "enable": case "enabled": v = true; return true;
            case "false": case "off": case "no": case "0": case "disable": case "disabled": v = false; return true;
            default: v = false; return false;
        }
    }

    private static bool OneOf(string value, out string match, params string[] options)
    {
        match = options.FirstOrDefault(o => o.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "";
        return match.Length > 0;
    }

    private static int Print(string text)
    {
        ConsoleOutput.Attach();
        Console.WriteLine(text);
        return 0;
    }

    private static int Fail(string message)
    {
        ConsoleOutput.Attach();
        Console.WriteLine("wallterm: " + message);
        return 1;
    }
}

/// <summary>
/// WallTerm is a GUI-subsystem exe, so it has no console of its own. When stdout is redirected (as the injected
/// 'wallterm' PowerShell function does) we write to that pipe; otherwise we attach to the parent's console.
/// </summary>
internal static class ConsoleOutput
{
    private static bool _done;

    public static void Attach()
    {
        if (_done) return;
        _done = true;
        var h = Native.GetStdHandle(Native.STD_OUTPUT_HANDLE);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
    }
}
