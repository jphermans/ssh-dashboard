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

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = Settings.Load();
            ThemeManager.Apply(settings.Theme);
            desktop.MainWindow = new MainWindow(new MainViewModel(settings));

            var args = desktop.Args ?? Array.Empty<string>();
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
