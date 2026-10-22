using Avalonia;
using Avalonia.Threading;
using SshDashboard.App.Views;
using SshDashboard.Core;

namespace SshDashboard.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--pty-probe"))
        {
            RunPtyProbe();
            return;
        }

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
        PpkFile.CleanupTemporaryKeys();
        SshAskPass.CleanupStale();
        if (args.Length == 0)
        {
            using var single = new Mutex(true, "SshDashboard.SingleInstance", out var first);
            if (!first) return;
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void RunPtyProbe()
    {
        var text = new System.Text.StringBuilder();
        using var done = new ManualResetEventSlim();
        using var pty = new PtySession("/bin/sh",
            new[] { "-c", "echo START $(stty size); sleep 0.7; echo END $(stty size)" },
            80, 24);
        pty.Output += bytes => text.Append(System.Text.Encoding.UTF8.GetString(bytes));
        pty.Exited += _ => done.Set();
        _ = Task.Run(async () => { await Task.Delay(300); pty.Resize(100, 40); });
        var finished = done.Wait(TimeSpan.FromSeconds(5));
        Console.WriteLine("pty=" + finished + " exit=" + pty.GetType().Name);
        Console.WriteLine(text.ToString().Trim());
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
