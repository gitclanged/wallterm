using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace WallTerm;

/// <summary>
/// Launch-at-logon integration. Windows refuses to start programs that require elevation from the
/// HKCU\...\Run key (they are silently skipped at sign-in), so WallTerm registers a Task Scheduler logon task
/// with "Run with highest privileges" instead: it starts elevated, without a UAC prompt, the moment the desktop
/// is ready, and is never stopped for battery/idle/time limits.
/// </summary>
internal static class StartupTask
{
    private const string TaskName = "WallTerm";
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public static string Install()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine executable path.");
        var user = WindowsIdentity.GetCurrent().Name;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>WallTerm - live PowerShell terminal on the desktop background</Description>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exe))}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        var xmlPath = Path.Combine(Path.GetTempPath(), $"wallterm-task-{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(xmlPath, xml, Encoding.Unicode);
            var (code, output) = Schtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            if (code != 0) throw new InvalidOperationException($"schtasks failed ({code}): {output.Trim()}");
        }
        finally
        {
            File.Delete(xmlPath);
        }

        RemoveRunKey();
        return $"Startup enabled: WallTerm will start elevated at logon for {user}.\n  {exe}";
    }

    public static string Uninstall()
    {
        var (code, _) = Schtasks($"/Delete /TN \"{TaskName}\" /F");
        bool removedRunKey = RemoveRunKey();
        return code == 0 || removedRunKey ? "Startup disabled." : "Startup was not enabled.";
    }

    /// <summary>Removes a legacy HKCU Run entry (which cannot launch an elevated app anyway).</summary>
    private static bool RemoveRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(TaskName) == null) return false;
        key.DeleteValue(TaskName, throwOnMissingValue: false);
        return true;
    }

    private static (int Code, string Output) Schtasks(string arguments)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr);
    }
}
