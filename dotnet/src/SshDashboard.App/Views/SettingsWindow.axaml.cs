using Avalonia.Controls;
using Avalonia.Interactivity;
using SshDashboard.Core;

namespace SshDashboard.App.Views;

public partial class SettingsWindow : Window
{
    private readonly Action<string>? _preview;
    private readonly string _lastView;
    private bool _loading = true;

    public SettingsWindow(Settings current, Action<string>? preview = null)
    {
        InitializeComponent();
        _preview = preview;
        _lastView = current.LastView;

        ThemeSystem.IsChecked = current.Theme == "system";
        ThemeDark.IsChecked = current.Theme == "dark";
        ThemeLight.IsChecked = current.Theme == "light";
        ConfirmCheck.IsChecked = current.ConfirmDelete;
        ConfigCheck.IsChecked = current.RemoveConfigDefault;
        _loading = false;
    }

    private void Theme_Checked(object? sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string key }) return;
        _preview?.Invoke(key);
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var theme = ThemeDark.IsChecked == true ? "dark"
            : ThemeLight.IsChecked == true ? "light"
            : "system";
        Close(new Settings
        {
            Theme = theme,
            ConfirmDelete = ConfirmCheck.IsChecked == true,
            RemoveConfigDefault = ConfigCheck.IsChecked == true,
            LastView = _lastView,
        });
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
