using System.Windows;
using System.Windows.Threading;

namespace WallTerm;

internal static class Program
{
    internal const string InstanceMutexName = @"Local\WallTerm.Instance";
    internal const string SummonEventName = @"Local\WallTerm.Summon";
    internal const string QuitEventName = @"Local\WallTerm.Quit";

    [STAThread]
    private static int Main(string[] args)
    {
        if (Cli.Run(args) is { } exitCode) return exitCode;

        bool noElevate = args.Any(a => a.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase));
        if (!noElevate && !Elevation.IsAdministrator())
        {
            // Normally unreachable (the manifest requires elevation), but covers a stripped manifest or a
            // launch through CreateProcess from an elevated-but-filtered context.
            Elevation.RelaunchElevated(args);
            return 0;
        }

        using var instance = new Mutex(true, InstanceMutexName, out bool firstInstance);
        if (!firstInstance)
        {
            // Already running: bring the existing terminal forward instead of opening a second one. This process was
            // launched by the user, so it may hand its foreground right to the running instance.
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);
            if (EventWaitHandle.TryOpenExisting(SummonEventName, out var summon))
                using (summon) summon.Set();
            return 0;
        }

        using var summonEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SummonEventName);
        using var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);

        var config = ConfigStore.LoadOrCreate();
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            // A wallpaper must not crash: log and keep running.
            Log(e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);

        var window = new MainWindow(config);

        var summonWait = ThreadPool.RegisterWaitForSingleObject(summonEvent,
            (_, _) => app.Dispatcher.BeginInvoke(window.Summon), null, Timeout.Infinite, executeOnlyOnce: false);
        var quitWait = ThreadPool.RegisterWaitForSingleObject(quitEvent,
            (_, _) => app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => window.Close()), null, Timeout.Infinite, executeOnlyOnce: true);

        try
        {
            return app.Run(window);
        }
        finally
        {
            summonWait.Unregister(null);
            quitWait.Unregister(null);
        }
    }

    internal static void Log(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(ConfigStore.DirectoryPath);
            var path = Path.Combine(ConfigStore.DirectoryPath, "wallterm.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
