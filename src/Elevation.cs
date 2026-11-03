using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace WallTerm;

internal static class Elevation
{
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Re-launches this exe through the UAC prompt with the same arguments. Returns false if the user declined.</summary>
    public static bool RelaunchElevated(string[] args)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine executable path.");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = JoinArguments(args),
            WorkingDirectory = Environment.CurrentDirectory,
        };
        try
        {
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC prompt dismissed
        {
            return false;
        }
    }

    /// <summary>Quotes arguments using the CommandLineToArgvW rules.</summary>
    public static string JoinArguments(IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        foreach (var arg in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(Quote(arg));
        }
        return sb.ToString();
    }

    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (var ch in arg)
        {
            if (ch == '\\') { backslashes++; continue; }
            if (ch == '"') sb.Append('\\', backslashes * 2 + 1);
            else sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(ch);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}
