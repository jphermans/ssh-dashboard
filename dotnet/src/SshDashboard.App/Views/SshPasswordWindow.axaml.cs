using Avalonia.Controls;
using Avalonia.Interactivity;
using SshDashboard.Core;

namespace SshDashboard.App.Views;

public partial class SshPasswordWindow : Window
{
    public SshPasswordWindow(HostRow host)
    {
        InitializeComponent();
        DetailText.Text = $"The remote host requested a password for {Label(host)}. " +
            "It is used only for this transfer; it is not stored in settings or logs.";
        Opened += (_, _) => Box.Focus();
    }

    private TextBox Box => this.FindControl<TextBox>("PasswordBox")!;

    private static string Label(HostRow host) => string.IsNullOrWhiteSpace(host.User)
        ? $"{host.Address}:{host.Port}"
        : $"{host.User}@{host.Address}:{host.Port}";

    private void Continue_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(Box.Text)) return;
        Close(Box.Text);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
