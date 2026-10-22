using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using SshDashboard.App.ViewModels;
using SshDashboard.App.Views;
using SshDashboard.Core;

namespace SshDashboard.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    private void AboutMenuItem_OnClick(object? sender, EventArgs e)
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "2.1.3";
        var window = new AboutWindow(version);
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime
            { MainWindow: { IsVisible: true } owner })
            window.Show(owner);
        else
            window.Show();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = Settings.Load();
            ThemeManager.Apply(settings.Theme);
            desktop.MainWindow = new MainWindow(new MainViewModel(settings));

            var args = desktop.Args ?? Array.Empty<string>();
            var main = desktop.MainWindow as MainWindow;

            if (args.Contains("--self-test")) main?.ProbeFirstForCapture();
            if (args.Contains("--ssh-first")) main?.OpenFirstSessionForCapture();
            var sessionShot = Array.IndexOf(args, "--session-shot");
            if (sessionShot >= 0 && sessionShot + 1 < args.Length)
            {
                var target = args[sessionShot + 1];
                DispatcherTimer.RunOnce(() =>
                {
                    try
                    {
                        if (SshLauncher.LastWindow is { } session && session.IsVisible)
                        {
                            var bitmap = new RenderTargetBitmap(
                                new PixelSize((int)session.Width, (int)session.Height), new Vector(96, 96));
                            bitmap.Render(session);
                            bitmap.Save(target);
                            Console.WriteLine("wrote " + target);
                        }
                    }
                    catch (Exception ex) { Console.Error.WriteLine(ex); }
                }, TimeSpan.FromSeconds(3));
            }

            if (Array.IndexOf(args, "--theme") is var themeIndex && themeIndex >= 0 && themeIndex + 1 < args.Length)
                ThemeManager.Apply(args[themeIndex + 1]);
            if (Array.IndexOf(args, "--view") is var viewIndex && viewIndex >= 0 && viewIndex + 1 < args.Length
                && desktop.MainWindow is MainWindow viewWindow)
                viewWindow.ShowViewForCapture(args[viewIndex + 1]);

            var shortcutsShot = Array.IndexOf(args, "--shortcuts-shot");
            if (shortcutsShot >= 0 && shortcutsShot + 1 < args.Length && main is not null)
            {
                var target = args[shortcutsShot + 1];
                DispatcherTimer.RunOnce(() =>
                {
                    _ = Dialogs.ConfirmAsync(main, "Keyboard shortcuts", "Keyboard shortcuts",
                        "F5 rescans. Delete removes the selection. Cmd/Ctrl+A selects all hosts.",
                        "", "Close", danger: false);
                }, TimeSpan.FromSeconds(1));
                DispatcherTimer.RunOnce(() =>
                {
                    try
                    {
                        var dialog = desktop.Windows.LastOrDefault();
                        if (dialog is not null)
                        {
                            var bitmap = new RenderTargetBitmap(
                                new PixelSize((int)dialog.Width, (int)dialog.Height), new Vector(96, 96));
                            bitmap.Render(dialog);
                            bitmap.Save(target);
                            Console.WriteLine("wrote " + target);
                        }
                    }
                    catch (Exception ex) { Console.Error.WriteLine(ex); }
                    desktop.Shutdown();
                }, TimeSpan.FromSeconds(3));
            }

            var transferShot = Array.IndexOf(args, "--transfer-shot");
            if (transferShot >= 0 && transferShot + 1 < args.Length)
            {
                var target = args[transferShot + 1];
                var demo = new HostRow
                {
                    Label = "lab-server",
                    Address = "192.0.2.50",
                    User = "jean",
                    Port = 22,
                };
                var dialog = new TransferWindow(demo);
                dialog.SetCaptureState();
                dialog.Show();
                DispatcherTimer.RunOnce(() =>
                {
                    try
                    {
                        var bitmap = new RenderTargetBitmap(
                            new PixelSize((int)dialog.Width, (int)dialog.Height), new Vector(96, 96));
                        bitmap.Render(dialog);
                        bitmap.Save(target);
                        Console.WriteLine("wrote " + target);
                    }
                    catch (Exception ex) { Console.Error.WriteLine(ex); }
                    desktop.Shutdown();
                }, TimeSpan.FromSeconds(2));
            }

            var remoteShot = Array.IndexOf(args, "--remote-shot");
            if (remoteShot >= 0 && remoteShot + 1 < args.Length)
            {
                var target = args[remoteShot + 1];
                var demo = new HostRow
                {
                    Label = "lab-server",
                    Address = "192.0.2.50",
                    User = "jean",
                    Port = 22,
                };
                var dialog = new RemoteBrowserWindow(demo, RemoteBrowseMode.Download);
                dialog.Show();
                DispatcherTimer.RunOnce(() =>
                {
                    try
                    {
                        var bitmap = new RenderTargetBitmap(
                            new PixelSize((int)dialog.Width, (int)dialog.Height), new Vector(96, 96));
                        bitmap.Render(dialog);
                        bitmap.Save(target);
                        Console.WriteLine("wrote " + target);
                    }
                    catch (Exception ex) { Console.Error.WriteLine(ex); }
                    desktop.Shutdown();
                }, TimeSpan.FromSeconds(2));
            }

            var passwordShot = Array.IndexOf(args, "--password-shot");
            if (passwordShot >= 0 && passwordShot + 1 < args.Length)
            {
                var target = args[passwordShot + 1];
                var demo = new HostRow
                {
                    Label = "root@lab-server",
                    Address = "192.0.2.50",
                    User = "root",
                    Port = 2222,
                };
                var dialog = new SshPasswordWindow(demo);
                dialog.Show();
                DispatcherTimer.RunOnce(() =>
                {
                    try
                    {
                        var bitmap = new RenderTargetBitmap(
                            new PixelSize((int)dialog.Width, (int)dialog.Height), new Vector(96, 96));
                        bitmap.Render(dialog);
                        bitmap.Save(target);
                        Console.WriteLine("wrote " + target);
                    }
                    catch (Exception ex) { Console.Error.WriteLine(ex); }
                    desktop.Shutdown();
                }, TimeSpan.FromSeconds(1));
            }

            var settingsShot = Array.IndexOf(args, "--settings-shot");
            if (settingsShot >= 0 && settingsShot + 1 < args.Length && main is not null)
            {
                var target = args[settingsShot + 1];
                var dialog = new SettingsWindow(Settings.Load(), t => ThemeManager.Apply(t));
                dialog.Show();
                DispatcherTimer.RunOnce(() =>
                {
                    try
                    {
                        var bitmap = new RenderTargetBitmap(
                            new PixelSize((int)dialog.Width, (int)dialog.Height), new Vector(96, 96));
                        bitmap.Render(dialog);
                        bitmap.Save(target);
                        Console.WriteLine("wrote " + target);
                    }
                    catch (Exception ex) { Console.Error.WriteLine(ex); }
                    desktop.Shutdown();
                }, TimeSpan.FromSeconds(2));
            }

            var shot = Array.IndexOf(args, "--screenshot");
            if (shot >= 0 && shot + 1 < args.Length)
            {
                var target = args[shot + 1];
                DispatcherTimer.RunOnce(() =>
                {
                    try
                    {
                        var window = desktop.MainWindow;
                        if (window is not null)
                        {
                            var sizeIndex = Array.IndexOf(args, "--size");
                            if (sizeIndex >= 0 && sizeIndex + 1 < args.Length)
                            {
                                var parts = args[sizeIndex + 1].Split('x');
                                if (parts.Length == 2 && double.TryParse(parts[0], out var w) && double.TryParse(parts[1], out var h))
                                {
                                    window.Width = w;
                                    window.Height = h;
                                }
                            }
                            var size = new PixelSize((int)window.Width, (int)window.Height);
                            var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
                            bitmap.Render(window);
                            bitmap.Save(target);
                            var grid = window.FindControl<Avalonia.Controls.DataGrid>("HostsGrid");
                            Console.WriteLine($"grid bounds={grid?.Bounds} visible={grid?.IsEffectivelyVisible} items={grid?.ItemsSource?.Cast<object>().Count()} cols={grid?.Columns.Count}");
                            Console.WriteLine("wrote " + target);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(ex);
                    }
                    desktop.Shutdown();
                }, TimeSpan.FromSeconds(2));
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}

public static class ThemeManager
{
    public static string Current { get; private set; } = "dark";

    public static void Apply(string preference)
    {
        var resolved = PlatformInfo.ResolveTheme(preference);
        Current = resolved;
        if (Current is null) return;
        if (Application.Current is { } app)
            app.RequestedThemeVariant = resolved == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
    }
}
