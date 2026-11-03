using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using WallTerm.Vt;

namespace WallTerm;

public sealed class PaddingConfig
{
    public double Left { get; set; } = 48;
    public double Top { get; set; } = 48;
    public double Right { get; set; } = 48;
    public double Bottom { get; set; } = 48;
}

public sealed class WallTermConfig
{
    public string FontFamily { get; set; } = "Cascadia Mono";
    /// <summary>Points (like Windows Terminal).</summary>
    public double FontSize { get; set; } = 14;
    public PaddingConfig Padding { get; set; } = new();
    /// <summary>#RRGGBB or #AARRGGBB; the alpha is multiplied by <see cref="Opacity"/>.</summary>
    public string BackgroundColor { get; set; } = "#000000";
    public double Opacity { get; set; } = 0.85;
    /// <summary>None, Acrylic or Mica.</summary>
    public string BackdropEffect { get; set; } = "None";
    /// <summary>Block, Beam or Underline.</summary>
    public string CursorStyle { get; set; } = "Block";
    public bool CursorBlink { get; set; } = true;
    /// <summary>OLED burn-in protection: shift the text canvas by a few pixels every few minutes.</summary>
    public bool Wiggle { get; set; }
    public int WiggleMinSeconds { get; set; } = 180;
    public int WiggleMaxSeconds { get; set; } = 300;
    public int WigglePixels { get; set; } = 3;
    public int Scrollback { get; set; } = 5000;
    public string Shell { get; set; } = "pwsh.exe";
    public string ShellArgs { get; set; } = "-NoLogo";
    public string StartDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    /// <summary>Global summon/dismiss hotkey, e.g. "Alt+`", "Ctrl+Alt+T", or "None".</summary>
    public string Hotkey { get; set; } = "Alt+`";
    public Dictionary<string, string> Colors { get; set; } = DefaultColors();

    public static readonly string[] AnsiNames =
    {
        "Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White",
        "BrightBlack", "BrightRed", "BrightGreen", "BrightYellow", "BrightBlue", "BrightMagenta", "BrightCyan", "BrightWhite",
    };

    public static Dictionary<string, string> DefaultColors() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Foreground"] = "#D8DAE3",
        ["Cursor"] = "#F2F3F7",
        ["Selection"] = "#663D4F7A",
        ["Black"] = "#1C1D24",
        ["Red"] = "#FF5F73",
        ["Green"] = "#4FE3A0",
        ["Yellow"] = "#F7D774",
        ["Blue"] = "#5CA8FF",
        ["Magenta"] = "#C792FF",
        ["Cyan"] = "#4FD6E8",
        ["White"] = "#D8DAE3",
        ["BrightBlack"] = "#5C6170",
        ["BrightRed"] = "#FF8A99",
        ["BrightGreen"] = "#7FF2BF",
        ["BrightYellow"] = "#FFE6A1",
        ["BrightBlue"] = "#8EC3FF",
        ["BrightMagenta"] = "#DDB8FF",
        ["BrightCyan"] = "#8CE8F4",
        ["BrightWhite"] = "#FFFFFF",
    };

    /// <summary>Clamps values and fills anything missing so the rest of the app never sees invalid settings.</summary>
    public WallTermConfig Normalize()
    {
        var defaults = new WallTermConfig();
        if (string.IsNullOrWhiteSpace(FontFamily)) FontFamily = defaults.FontFamily;
        FontSize = double.IsFinite(FontSize) ? Math.Clamp(FontSize, 4, 96) : defaults.FontSize;
        Padding ??= new PaddingConfig();
        Padding.Left = ClampPad(Padding.Left);
        Padding.Top = ClampPad(Padding.Top);
        Padding.Right = ClampPad(Padding.Right);
        Padding.Bottom = ClampPad(Padding.Bottom);
        if (!ColorUtil.TryParse(BackgroundColor, out _)) BackgroundColor = defaults.BackgroundColor;
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0, 1) : 1;
        BackdropEffect = NormalizeChoice(BackdropEffect, ["None", "Acrylic", "Mica"], "None");
        CursorStyle = NormalizeChoice(CursorStyle?.Equals("Bar", StringComparison.OrdinalIgnoreCase) == true ? "Beam" : CursorStyle, ["Block", "Beam", "Underline"], "Block");
        WiggleMinSeconds = Math.Clamp(WiggleMinSeconds, 10, 3600);
        WiggleMaxSeconds = Math.Clamp(WiggleMaxSeconds, WiggleMinSeconds, 7200);
        WigglePixels = Math.Clamp(WigglePixels, 1, 8);
        Scrollback = Math.Clamp(Scrollback, 0, 200_000);
        if (string.IsNullOrWhiteSpace(Shell)) Shell = defaults.Shell;
        ShellArgs ??= "";
        if (string.IsNullOrWhiteSpace(StartDirectory)) StartDirectory = defaults.StartDirectory;
        if (string.IsNullOrWhiteSpace(Hotkey)) Hotkey = "None";

        var colors = DefaultColors();
        if (Colors != null)
            foreach (var (k, v) in Colors)
                if (ColorUtil.TryParse(v, out _)) colors[k] = v;
        Colors = colors;
        return this;

        static double ClampPad(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1000) : 0;
    }

    private static string NormalizeChoice(string? value, string[] choices, string fallback) =>
        choices.FirstOrDefault(c => c.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? fallback;
}

[JsonSourceGenerationOptions(WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(WallTermConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

internal static class ColorUtil
{
    /// <summary>Parses #RGB, #RRGGBB or #AARRGGBB into 0xAARRGGBB.</summary>
    public static bool TryParse(string? text, out uint argb)
    {
        argb = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('#');
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        switch (s.Length)
        {
            case 3:
                uint r = (v >> 8) & 0xF, g = (v >> 4) & 0xF, b = v & 0xF;
                argb = 0xFF000000 | (r * 17) << 16 | (g * 17) << 8 | (b * 17);
                return true;
            case 6:
                argb = 0xFF000000 | v;
                return true;
            case 8:
                argb = v;
                return true;
            default:
                return false;
        }
    }

    public static uint Parse(string? text, uint fallback) => TryParse(text, out var v) ? v : fallback;
}

/// <summary>Resolved colors for rendering (all values 0xAARRGGBB).</summary>
internal sealed class Theme
{
    public readonly uint[] Palette = new uint[256];
    public uint Foreground, Cursor, Selection;
    /// <summary>Opaque background, used where text is drawn inverted.</summary>
    public uint Background;
    /// <summary>Window background including alpha (BackgroundColor alpha Ã— Opacity).</summary>
    public uint WindowBackground;

    public static Theme From(WallTermConfig c)
    {
        var t = new Theme();
        var defaults = WallTermConfig.DefaultColors();
        uint Get(string key) => ColorUtil.Parse(c.Colors.GetValueOrDefault(key), ColorUtil.Parse(defaults[key], 0xFFFFFFFF));

        for (int i = 0; i < 16; i++) t.Palette[i] = Get(WallTermConfig.AnsiNames[i]) | 0xFF000000;
        int[] steps = { 0, 95, 135, 175, 215, 255 };
        for (int i = 0; i < 216; i++)
            t.Palette[16 + i] = 0xFF000000 | (uint)(steps[i / 36] << 16 | steps[i / 6 % 6] << 8 | steps[i % 6]);
        for (int i = 0; i < 24; i++)
        {
            uint v = (uint)(8 + i * 10);
            t.Palette[232 + i] = 0xFF000000 | v << 16 | v << 8 | v;
        }

        t.Foreground = Get("Foreground") | 0xFF000000;
        t.Cursor = Get("Cursor") | 0xFF000000;
        t.Selection = Get("Selection");
        uint bg = ColorUtil.Parse(c.BackgroundColor, 0xFF000000);
        t.Background = bg | 0xFF000000;
        uint alpha = (uint)Math.Round((bg >> 24) * c.Opacity);
        t.WindowBackground = (alpha << 24) | (bg & 0xFFFFFF);
        return t;
    }

    public uint Resolve(uint color, uint fallback) => (color & TermColor.TagMask) switch
    {
        TermColor.Indexed => Palette[color & 0xFF],
        TermColor.Rgb => 0xFF000000 | (color & 0xFFFFFF),
        _ => fallback,
    };
}

/// <summary>Loads, saves and watches %USERPROFILE%\.config\wallterm\config.json.</summary>
internal static class ConfigStore
{
    // Relaxed escaping keeps values like "Alt+`" readable in the file instead of +`.
    private static readonly ConfigJsonContext Json = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public static string DirectoryPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "wallterm");

    public static string FilePath { get; } = Path.Combine(DirectoryPath, "config.json");

    public static WallTermConfig LoadOrCreate()
    {
        if (TryLoad(out var config, out _)) return config;
        if (!File.Exists(FilePath))
        {
            config = new WallTermConfig().Normalize();
            try { Save(config); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return config;
        }
        return new WallTermConfig().Normalize(); // file exists but is invalid: run with defaults, never overwrite the user's file
    }

    public static bool TryLoad(out WallTermConfig config, out string? error)
    {
        config = new WallTermConfig();
        error = null;
        try
        {
            if (!File.Exists(FilePath)) { error = "config file does not exist"; return false; }
            var json = File.ReadAllText(FilePath);
            config = (JsonSerializer.Deserialize(json, Json.WallTermConfig) ?? new WallTermConfig()).Normalize();
            return true;
        }
        catch (JsonException ex)
        {
            error = $"invalid JSON: {ex.Message}";
            return false;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Atomic save (write temp + rename) so the watcher never observes a half-written file.</summary>
    public static void Save(WallTermConfig config)
    {
        Directory.CreateDirectory(DirectoryPath);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Json.WallTermConfig));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public static string ToJson(WallTermConfig config) => JsonSerializer.Serialize(config, Json.WallTermConfig);
}

/// <summary>Raises <see cref="Changed"/> (on a thread-pool thread) after config.json is modified. Event-driven, debounced.</summary>
internal sealed class ConfigWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;
    private int _retries;

    public event Action<WallTermConfig>? Changed;

    public ConfigWatcher()
    {
        Directory.CreateDirectory(ConfigStore.DirectoryPath);
        _debounce = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(ConfigStore.DirectoryPath)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            IncludeSubdirectories = false,
        };
        _watcher.Changed += OnFsEvent;
        _watcher.Created += OnFsEvent;
        _watcher.Renamed += OnFsEvent;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        if (!string.Equals(e.Name, "config.json", StringComparison.OrdinalIgnoreCase)) return;
        _retries = 0;
        _debounce.Change(120, Timeout.Infinite);
    }

    private void Reload()
    {
        if (ConfigStore.TryLoad(out var config, out _))
        {
            Changed?.Invoke(config);
        }
        else if (File.Exists(ConfigStore.FilePath) && ++_retries < 5)
        {
            // The file may still be locked by the editor that is saving it; try again shortly.
            _debounce.Change(250, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _debounce.Dispose();
    }
}
