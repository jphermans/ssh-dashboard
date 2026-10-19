using Avalonia.Controls;
using Avalonia.Interactivity;
using SshDashboard.Core;

namespace SshDashboard.App.Views;

public partial class BackupsWindow : Window
{
    private readonly SshStore _store;
    private readonly Action _changed;

    public BackupsWindow(SshStore store, Action changed)
    {
        InitializeComponent();
        _store = store;
        _changed = changed;
        Reload();
    }

    private void Reload()
    {
        var items = _store.ListBackups()
            .Select(path => new BackupItem(
                path,
                Path.GetFileName(path),
                File.Exists(path) ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss") : "",
                File.Exists(path) ? $"{new FileInfo(path).Length / 1024.0:0} KB" : ""))
            .ToList();
        FilesList.ItemsSource = items;
        SummaryText.Text = items.Count == 0
            ? "No backups yet — one is written before every change."
            : $"{items.Count} backup file(s) in ~/.ssh/.sshdashboard/backups";
    }

    private async void Restore_Click(object? sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItem is not BackupItem item)
        {
            StatusText.Text = "Select a backup first.";
            return;
        }
        var ok = await Dialogs.ConfirmAsync(this, "Restore backup",
            "Restore this backup?",
            "The current file is overwritten. Restore is logged in Activity.",
            item.Name, "Restore", danger: true);
        if (!ok) return;
        try
        {
            _store.RestoreBackup(item.Path);
            _changed();
            Reload();
            StatusText.Text = "Restored " + item.Name;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Restore failed: " + ex.Message;
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private sealed record BackupItem(string Path, string Name, string When, string Size);
}
