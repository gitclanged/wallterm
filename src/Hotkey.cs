using System.Windows.Input;

namespace WallTerm;

/// <summary>Parses hotkey strings such as "Alt+`", "Ctrl+Alt+T" or "Win+F12" into RegisterHotKey arguments.</summary>
internal static class Hotkey
{
    public static bool TryParse(string? text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return false;

        // Split on '+', but allow '+' itself as the key ("Ctrl++").
        var parts = new List<string>();
        var s = text.Trim();
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '+' && i > start) { parts.Add(s[start..i].Trim()); start = i + 1; }
        }
        parts.Add(s[start..].Trim());

        for (int i = 0; i < parts.Count - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= Native.MOD_CONTROL; break;
                case "alt": modifiers |= Native.MOD_ALT; break;
                case "shift": modifiers |= Native.MOD_SHIFT; break;
                case "win": case "windows": case "super": modifiers |= Native.MOD_WIN; break;
                default: return false;
            }
        }

        var key = parts[^1];
        if (key.Length == 0) return false;
        switch (key.ToLowerInvariant())
        {
            case "`": case "~": case "backtick": case "backquote": case "tilde": case "grave": vk = 0xC0; break;
            case "space": vk = 0x20; break;
            case "enter": case "return": vk = 0x0D; break;
            case "tab": vk = 0x09; break;
            case "esc": case "escape": vk = 0x1B; break;
            default:
                if (key.Length == 1)
                {
                    short scan = Native.VkKeyScan(key[0]);
                    if (scan == -1) return false;
                    vk = (uint)(scan & 0xFF);
                }
                else if (Enum.TryParse<Key>(key, ignoreCase: true, out var k) && k != Key.None)
                {
                    vk = (uint)KeyInterop.VirtualKeyFromKey(k);
                }
                else return false;
                break;
        }
        return vk != 0 && (modifiers != 0 || vk is >= 0x70 and <= 0x87); // bare keys only allowed for F1-F24
    }
}
