using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SshDashboard.Core;

namespace SshDashboard.App.Views;

public sealed record BlockEditResult(IReadOnlyList<string> Patterns, IReadOnlyDictionary<string, string> Values);

public static class Dialogs
{
    public static async Task<bool> ConfirmAsync(Window owner, string title, string heading,
        string message, string detail, string confirmLabel, bool danger = true)
    {
        var window = Shell(owner, title, 520, 330);
        var root = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(26) };
        root.Children.Add(Text(heading, 20, FontWeight.SemiBold));
        root.Children.Add(Text(message, 13, FontWeight.Normal, "TextMuted"));
        if (!string.IsNullOrWhiteSpace(detail))
        {
            var box = new Border
            {
                Background = Brush("ElevatedBg"),
                BorderBrush = Brush("BorderSoft"),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(8),
                Padding = new Avalonia.Thickness(12),
            };
            box.Child = Text(detail, 12, FontWeight.Normal, "TextMuted", monospace: true);
            root.Children.Add(box);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Avalonia.Thickness(0, 10, 0, 0),
        };
        var cancel = Button("Cancel", "ghost");
        cancel.Click += (_, _) => window.Close(false);
        var confirm = Button(confirmLabel, danger ? "danger" : "primary");
        confirm.Click += (_, _) => window.Close(true);
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        root.Children.Add(buttons);

        window.Content = root;
        return await window.ShowDialog<bool>(owner);
    }

    public static async Task<Settings?> SettingsAsync(Window owner, Settings current,
        Action<string>? previewTheme = null)
    {
        var dialog = new SettingsWindow(current, previewTheme);
        return await dialog.ShowDialog<Settings?>(owner);
    }


    public static async Task<BlockEditResult?> AddBlockAsync(Window owner)
    {
        var window = Shell(owner, "Add Host block", 560, 540);
        var root = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(26) };
        root.Children.Add(Text("Add a Host block", 20, FontWeight.SemiBold));
        root.Children.Add(Text("Appended to ~/.ssh/config with a timestamped backup. Existing blocks are not changed.",
            12.5, FontWeight.Normal, "TextMuted"));

        var patterns = Input("Patterns (space separated)", "", root);
        patterns.Watermark = "myserver  myserver.example.com";
        var hostname = Input("HostName", "", root);
        hostname.Watermark = "192.0.2.10 or server.example.com";
        var user = Input("User", "", root);
        user.Watermark = "optional";
        var port = Input("Port", "", root);
        port.Watermark = "22";
        var identity = Input("IdentityFile", "", root);
        identity.Watermark = "~/.ssh/id_ed25519";

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Avalonia.Thickness(0, 12, 0, 0),
        };
        var cancel = Button("Cancel", "ghost");
        cancel.Click += (_, _) => window.Close(null);
        var save = Button("Add block", "primary");
        save.Click += (_, _) =>
        {
            var list = (patterns.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (list.Count == 0)
            {
                patterns.BorderBrush = Brush("DangerBrush");
                return;
            }
            if (!string.IsNullOrWhiteSpace(port.Text) &&
                (!int.TryParse(port.Text, out var number) || number is < 1 or > 65535))
            {
                port.BorderBrush = Brush("DangerBrush");
                return;
            }
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["hostname"] = hostname.Text ?? "",
                ["user"] = user.Text ?? "",
                ["port"] = port.Text ?? "",
                ["identityfile"] = identity.Text ?? "",
            };
            window.Close(new BlockEditResult(list, values));
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);

        window.Content = root;
        return await window.ShowDialog<BlockEditResult?>(owner);
    }

    public static async Task<BlockEditResult?> EditBlockAsync(Window owner, ConfigBlock block)
    {
        var window = Shell(owner, "Edit Host block", 560, 470);
        var root = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(26) };
        root.Children.Add(Text("Edit " + block.Label, 20, FontWeight.SemiBold));
        root.Children.Add(Text($"Lines {block.StartLine + 1}–{block.EndLine + 1} of ~/.ssh/config",
            12.5, FontWeight.Normal, "TextMuted"));

        var patterns = Input("Patterns (space separated)", string.Join(" ", block.Patterns), root);
        var hostname = Input("HostName", block.HostName, root);
        var user = Input("User", block.User, root);
        var port = Input("Port", block.PortText, root);
        var identity = Input("IdentityFile", block.IdentityFile, root);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Avalonia.Thickness(0, 12, 0, 0),
        };
        var cancel = Button("Cancel", "ghost");
        cancel.Click += (_, _) => window.Close(null);
        var save = Button("Save changes", "primary");
        save.Click += (_, _) =>
        {
            var list = (patterns.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (list.Count == 0)
            {
                patterns.BorderBrush = Brush("DangerBrush");
                return;
            }
            if (!string.IsNullOrWhiteSpace(port.Text) && !int.TryParse(port.Text, out _))
            {
                port.BorderBrush = Brush("DangerBrush");
                return;
            }
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["hostname"] = hostname.Text ?? "",
                ["user"] = user.Text ?? "",
                ["port"] = port.Text ?? "",
                ["identityfile"] = identity.Text ?? "",
            };
            window.Close(new BlockEditResult(list, values));
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);

        window.Content = root;
        return await window.ShowDialog<BlockEditResult?>(owner);
    }


    private static Window Shell(Window owner, string title, double width, double height) => new()
    {
        Title = title,
        Width = width,
        Height = height,
        MinWidth = width,
        MinHeight = height,
        CanResize = true,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = Brush("AppBg"),
        ShowInTaskbar = false,
    };

    private static IBrush Brush(string key)
    {
        var app = Application.Current;
        if (app is not null)
        {
            if (app.TryFindResource(key, app.ActualThemeVariant, out var themed) && themed is IBrush first)
                return first;
            if (app.TryFindResource(key, out var plain) && plain is IBrush second)
                return second;
        }
        return key switch
        {
            "AppBg" => new SolidColorBrush(Color.Parse("#11171F")),
            "TextPrimary" => Brushes.White,
            "TextMuted" => new SolidColorBrush(Color.Parse("#93A4B8")),
            "BorderSoft" => new SolidColorBrush(Color.Parse("#26313F")),
            "ElevatedBg" => new SolidColorBrush(Color.Parse("#1B2531")),
            "AccentBrush" => new SolidColorBrush(Color.Parse("#4C8DFF")),
            "DangerBrush" => new SolidColorBrush(Color.Parse("#F26D6D")),
            "SuccessBrush" => new SolidColorBrush(Color.Parse("#3FCF8E")),
            _ => Brushes.Transparent,
        };
    }

    private static TextBlock Text(string text, double size, FontWeight weight,
        string color = "TextPrimary", bool monospace = false) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(color),
        FontFamily = monospace ? new FontFamily("SF Mono, Menlo, Consolas, monospace") : FontFamily.Default,
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 10.5,
        FontWeight = FontWeight.SemiBold,
        Foreground = Brush("TextFaint"),
        Margin = new Avalonia.Thickness(0, 8, 0, 0),
    };

    private static Button Button(string text, string kind) => new()
    {
        Content = text,
        Classes = { kind },
    };

    private static TextBox Input(string label, string value, Panel parent)
    {
        parent.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            Foreground = Brush("TextMuted"),
        });
        var box = new TextBox { Text = value, FontSize = 13 };
        parent.Children.Add(box);
        return box;
    }
}
