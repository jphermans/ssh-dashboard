using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SshDashboard.App.Views;

public partial class PassphraseWindow : Window
{
    public PassphraseWindow(string keyName)
    {
        InitializeComponent();
        DetailText.Text = $"“{keyName}” is protected. Enter its passphrase to convert it for this session.";
        Opened += (_, _) => Box.Focus();
    }

    private TextBox Box => this.FindControl<TextBox>("PassphraseBox")!;

    private void Unlock_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(Box.Text)) return;
        Close(Box.Text);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
