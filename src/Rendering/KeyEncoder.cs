using System.Windows.Input;

namespace WallTerm.Rendering;

/// <summary>Translates non-text keys into xterm input sequences. Printable text goes through WPF TextInput instead.</summary>
internal static class KeyEncoder
{
    public static string? Encode(Key key, ModifierKeys mods, bool appCursorKeys)
    {
        bool shift = (mods & ModifierKeys.Shift) != 0;
        bool alt = (mods & ModifierKeys.Alt) != 0;
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        int m = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);

        switch (key)
        {
            case Key.Up: return Cursor('A');
            case Key.Down: return Cursor('B');
            case Key.Right: return Cursor('C');
            case Key.Left: return Cursor('D');
            case Key.Home: return Cursor('H');
            case Key.End: return Cursor('F');
            case Key.Insert: return Tilde(2);
            case Key.Delete: return Tilde(3);
            case Key.PageUp: return Tilde(5);
            case Key.PageDown: return Tilde(6);
            case Key.F1: return Ss3Fn('P');
            case Key.F2: return Ss3Fn('Q');
            case Key.F3: return Ss3Fn('R');
            case Key.F4: return Ss3Fn('S');
            case Key.F5: return Tilde(15);
            case Key.F6: return Tilde(17);
            case Key.F7: return Tilde(18);
            case Key.F8: return Tilde(19);
            case Key.F9: return Tilde(20);
            case Key.F10: return Tilde(21);
            case Key.F11: return Tilde(23);
            case Key.F12: return Tilde(24);
            case Key.Back: return ctrl ? "\x08" : alt ? "\x1b\x7f" : "\x7f";
            case Key.Tab: return shift ? "\x1b[Z" : "\t";
            case Key.Enter: return alt ? "\x1b\r" : "\r";
            case Key.Escape: return "\x1b";
            case Key.Space: return ctrl ? (alt ? "\x1b\0" : "\0") : null;
        }

        if (ctrl && !alt) return ControlChar(key, shift);
        return null;

        string Cursor(char final) =>
            m == 1 ? (appCursorKeys ? "\x1bO" : "\x1b[") + final : $"\x1b[1;{m}{final}";
        string Tilde(int code) => m == 1 ? $"\x1b[{code}~" : $"\x1b[{code};{m}~";
        string Ss3Fn(char final) => m == 1 ? "\x1bO" + final : $"\x1b[1;{m}{final}";
    }

    private static string? ControlChar(Key key, bool shift)
    {
        if (key is >= Key.A and <= Key.Z) return ((char)(key - Key.A + 1)).ToString();
        int vk = KeyInterop.VirtualKeyFromKey(key);
        char c = (char)(Native.MapVirtualKey((uint)vk, Native.MAPVK_VK_TO_CHAR) & 0xFFFF);
        return c switch
        {
            '2' or '@' or ' ' => "\0",
            '[' => "\x1b",
            '\\' => "\x1c",
            ']' => "\x1d",
            '6' or '^' => "\x1e",
            '-' or '_' or '/' => "\x1f",
            '?' => "\x7f",
            _ => null,
        };
    }
}
