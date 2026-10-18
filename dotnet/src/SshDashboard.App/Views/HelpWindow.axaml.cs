using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SshDashboard.App.Views;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        VersionText.Text = "SSH Dashboard " +
            (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "2.0.2") +
            " · macOS, Windows and Linux";
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void Repo_Click(object? sender, RoutedEventArgs e) => Open(
        "https://github.com/jphermans/ssh-dashboard");

    private void Issues_Click(object? sender, RoutedEventArgs e) => Open(
        "https://github.com/jphermans/ssh-dashboard/issues");

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }
}
