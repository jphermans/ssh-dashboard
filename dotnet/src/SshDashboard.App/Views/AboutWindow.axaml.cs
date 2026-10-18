using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SshDashboard.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow(string version, string sshFolder)
    {
        InitializeComponent();
        VersionBadge.Text = "Version " + version;
        VersionValue.Text = version;
        SshFolderValue.Text = sshFolder;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void Help_Click(object? sender, RoutedEventArgs e) => new HelpWindow().Show(this);

    private void Repo_Click(object? sender, RoutedEventArgs e) => Open(
        "https://github.com/jphermans/ssh-dashboard");

    private void Issues_Click(object? sender, RoutedEventArgs e) => Open(
        "https://github.com/jphermans/ssh-dashboard/issues");

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
