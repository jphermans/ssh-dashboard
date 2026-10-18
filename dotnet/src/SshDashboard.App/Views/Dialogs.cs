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

    public static async Task<Settings?> SettingsAsync(Window owner, Settings current)
    {
        var window = Shell(owner, "Settings", 560, 470);
        var root = new StackPanel { Spacing = 16, Margin = new Avalonia.Thickness(26) };
        root.Children.Add(Text("Settings", 20, FontWeight.SemiBold));
        root.Children.Add(Text("Preferences for this Mac. Files in ~/.ssh are never changed by these options.",
            12.5, FontWeight.Normal, "TextMuted"));

        root.Children.Add(Label("APPEARANCE"));
        var themes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var group = "theme-" + Guid.NewGuid().ToString("N");
        var choices = new (string Key, string Label)[]
        {
            ("system", "System"), ("dark", "Dark"), ("light", "Light"),
        };
        var radios = new List<RadioButton>();
        foreach (var (key, label) in choices)
        {
            var radio = new RadioButton
            {
                Content = label,
                GroupName = group,
                IsChecked = current.Theme == key,
                Padding = new Avalonia.Thickness(12, 7),
            };
            radio.Tag = key;
            radios.Add(radio);
            themes.Children.Add(radio);
        }
        root.Children.Add(themes);
        root.Children.Add(Text("System follows the OS appearance.", 11.5, FontWeight.Normal, "TextFaint"));

        root.Children.Add(Label("REMOVAL"));
        var confirm = new CheckBox { Content = "Ask for confirmation before removing entries", IsChecked = current.ConfirmDelete };
        var config = new CheckBox { Content = "Also remove matching ssh_config Host blocks by default", IsChecked = current.RemoveConfigDefault };
        confirm.FontSize = 13;
        config.FontSize = 13;
        root.Children.Add(confirm);
        root.Children.Add(config);
        root.Children.Add(Text("A timestamped backup is written before every change.", 11.5, FontWeight.Normal, "TextFaint"));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
        };
        var cancel = Button("Cancel", "ghost");
        cancel.Click += (_, _) => window.Close(null);
        var save = Button("Save changes", "primary");
        save.Click += (_, _) =>
        {
            var result = new Settings
            {
                Theme = radios.FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "system",
                ConfirmDelete = confirm.IsChecked == true,
                RemoveConfigDefault = config.IsChecked == true,
                LastView = current.LastView,
            };
            window.Close(result);
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);

        window.Content = root;
        return await window.ShowDialog<Settings?>(owner);
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

    public static async Task AboutAsync(Window owner, string version, string sshDir)
    {
        var window = Shell(owner, "About SSH Dashboard", 480, 360);
        var root = new StackPanel { Spacing = 10, Margin = new Avalonia.Thickness(28) };
        root.Children.Add(new TextBlock
        {
            Text = "SSH Dashboard",
            FontSize = 24,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("TextPrimary"),
        });
        root.Children.Add(Text($"Version {version}", 12.5, FontWeight.Normal, "TextMuted"));
        root.Children.Add(Text(
            "A careful desktop dashboard for ~/.ssh. Reads known_hosts, ssh_config and key pairs, " +
            "removes entries with timestamped backups, and never writes outside the SSH folder.",
            12.5, FontWeight.Normal, "TextMuted"));
        root.Children.Add(Text("SSH folder  " + sshDir, 11.5, FontWeight.Normal, "TextFaint", monospace: true));
        root.Children.Add(Text("Built with .NET and Avalonia. © 2026 JPHsystems.", 11.5, FontWeight.Normal, "TextFaint"));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Avalonia.Thickness(0, 12, 0, 0),
        };
        var repo = Button("Open repository", "ghost");
        repo.Click += (_, _) => Process.Start(new ProcessStartInfo(
            "https://github.com/jphermans/ssh-dashboard") { UseShellExecute = true });
        var close = Button("Close", "primary");
        close.Click += (_, _) => window.Close();
        buttons.Children.Add(repo);
        buttons.Children.Add(close);
        root.Children.Add(buttons);
        window.Content = root;
        await window.ShowDialog(owner);
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

    private static IBrush Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush : Brushes.Transparent;

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
