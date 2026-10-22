using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SshDashboard.Core;

namespace SshDashboard.App.Views;

public enum RemoteBrowseMode
{
    Download,
    UploadDestination,
}

public sealed record RemoteEntry(string Name, string Path, bool IsDirectory)
{
    public string Icon => IsDirectory ? "▸" : "·";
}

public sealed record RemoteBrowseResult(string Path, bool IsDirectory);

public partial class RemoteBrowserWindow : Window
{
    private readonly HostRow _host;
    private readonly RemoteBrowseMode _mode;
    private readonly ObservableCollection<RemoteEntry> _entries = new();
    private int _loadVersion;

    public RemoteBrowserWindow(HostRow host, RemoteBrowseMode mode)
    {
        InitializeComponent();
        _host = host;
        _mode = mode;
        Title = mode == RemoteBrowseMode.UploadDestination
            ? "Choose remote folder"
            : "Choose remote file or folder";
        HeadingText.Text = Title;
        TargetText.Text = string.IsNullOrWhiteSpace(host.User)
            ? $"{host.Address}:{host.Port}"
            : $"{host.User}@{host.Address}:{host.Port}";
        Entries.ItemsSource = _entries;
        ChooseButton.Content = mode == RemoteBrowseMode.UploadDestination
            ? "Use selected folder"
            : "Choose selection";
        Opened += async (_, _) => await LoadAsync("~");
    }

    private async void Up_Click(object? sender, RoutedEventArgs e)
    {
        var current = Normalize(PathBox.Text);
        await LoadAsync(Parent(current));
    }

    private async void Go_Click(object? sender, RoutedEventArgs e) =>
        await LoadAsync(Normalize(PathBox.Text));

    private async void Refresh_Click(object? sender, RoutedEventArgs e) =>
        await LoadAsync(Normalize(PathBox.Text));

    private async void PathBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await LoadAsync(Normalize(PathBox.Text));
    }

    private async void Entries_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Entries.SelectedItem is not RemoteEntry { IsDirectory: true } entry) return;
        e.Handled = true;
        await LoadAsync(entry.Path);
    }

    private void Entries_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var entry = Entries.SelectedItem as RemoteEntry;
        SelectionText.Text = entry is null
            ? "No item selected"
            : (entry.IsDirectory ? "Folder: " : "File: ") + entry.Path;
        ChooseButton.IsEnabled = entry is not null ||
            (_mode == RemoteBrowseMode.UploadDestination && _entries.Count > 0);
    }

    private void Choose_Click(object? sender, RoutedEventArgs e)
    {
        var current = Normalize(PathBox.Text);
        var selected = Entries.SelectedItem as RemoteEntry;
        var path = _mode == RemoteBrowseMode.UploadDestination
            ? (selected is { IsDirectory: true } ? selected.Path : current)
            : selected?.Path ?? current;
        Close(new RemoteBrowseResult(path, selected?.IsDirectory ?? true));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private async Task LoadAsync(string path)
    {
        path = Normalize(path);
        PathBox.Text = path;
        var version = ++_loadVersion;
        SetBusy(true, "Reading remote folder…");
        var (entries, error) = await RemoteListing.ListAsync(_host, path);
        if (version != _loadVersion) return;
        _entries.Clear();
        if (error is not null)
        {
            SetBusy(false, error);
            ChooseButton.IsEnabled = _mode == RemoteBrowseMode.UploadDestination;
            return;
        }
        foreach (var entry in entries) _entries.Add(entry);
        SetBusy(false, entries.Count == 0 ? "This folder is empty" : $"{entries.Count} item(s)");
        ChooseButton.IsEnabled = _mode == RemoteBrowseMode.UploadDestination || _entries.Count > 0;
        SelectionText.Text = _mode == RemoteBrowseMode.UploadDestination
            ? "Choose the folder where the upload should land"
            : "Choose a file or folder to download";
    }

    private void SetBusy(bool busy, string status)
    {
        StatusText.Text = status;
        ChooseButton.IsEnabled = !busy && ChooseButton.IsEnabled;
        Entries.IsEnabled = !busy;
    }

    private static string Normalize(string? path)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0 || path == ".") return "~";
        if (path.Length > 1) path = path.TrimEnd('/');
        return path.Length == 0 ? "/" : path;
    }

    private static string Parent(string path)
    {
        if (path is "~" or "/" || path.Length == 0) return path;
        var index = path.LastIndexOf('/');
        if (index < 0) return "~";
        if (index == 0) return "/";
        var parent = path[..index];
        return parent == "" ? "/" : parent;
    }
}

public static class RemoteListing
{
    public static async Task<(IReadOnlyList<RemoteEntry> Entries, string? Error)> ListAsync(
        HostRow host, string path)
    {
        try
        {
            var command = "cd " + SshLauncher.EscapeRemotePath(path) + " && LC_ALL=C /bin/ls -1Ap";
            var psi = new ProcessStartInfo("ssh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in SshLauncher.BuildSshCommandArguments(host, command))
                psi.ArgumentList.Add(arg);
            using var process = new Process { StartInfo = psi };
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (Array.Empty<RemoteEntry>(), "The remote server did not respond in time.");
            }
            var stdout = await stdoutTask;
            var stderr = (await stderrTask).Trim();
            if (process.ExitCode != 0)
                return (Array.Empty<RemoteEntry>(),
                    stderr.Length > 0 ? stderr : $"ssh exited with code {process.ExitCode}.");

            var entries = new List<RemoteEntry>();
            foreach (var rawLine in stdout.Split('\n'))
            {
                var raw = rawLine.TrimEnd('\r');
                if (raw.Length == 0 || raw is "." or "..") continue;
                var isDirectory = raw.EndsWith('/');
                var name = raw.TrimEnd('/', '@', '*');
                if (name.Length == 0) continue;
                entries.Add(new RemoteEntry(name, Join(path, name), isDirectory));
            }
            entries.Sort((a, b) =>
            {
                if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return (entries, null);
        }
        catch (Exception ex)
        {
            return (Array.Empty<RemoteEntry>(), ex.Message);
        }
    }

    private static string Join(string parent, string name)
    {
        parent = parent.TrimEnd('/');
        return parent.Length == 0 ? "/" + name : parent + "/" + name;
    }
}
