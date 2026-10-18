using Avalonia;
using Avalonia.Threading;
using SshDashboard.Core;

namespace SshDashboard.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--check"))
        {
            var store = new SshStore();
            var snapshot = store.Load();
            Console.WriteLine($"SSH Dashboard {typeof(Program).Assembly.GetName().Version?.ToString(3)}");
            Console.WriteLine($"ssh folder   {store.SshDir}");
            Console.WriteLine($"hosts        {snapshot.Hosts.Count}");
            Console.WriteLine($"config blocks {snapshot.Blocks.Count}");
            Console.WriteLine($"keys         {snapshot.Keys.Count}");
            Console.WriteLine($"backups      {snapshot.BackupCount}");
            Console.WriteLine($".NET         {Environment.Version}");
            return;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash(e.Exception);
            e.SetObserved();
        };
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void LogCrash(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(PlatformInfo.SupportDir);
            File.AppendAllText(PlatformInfo.ErrorLogPath,
                $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch { }
        Console.Error.WriteLine(ex);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = true })
            .LogToTrace();
}
