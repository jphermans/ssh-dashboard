using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SshDashboard.App.Views;

public partial class ShortcutsWindow : Window
{
    public ShortcutsWindow() => InitializeComponent();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
