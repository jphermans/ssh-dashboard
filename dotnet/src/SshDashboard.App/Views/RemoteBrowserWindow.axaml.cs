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

public sealed class RemoteEntry : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isSelected;

    public RemoteEntry(string name, string path, bool isDirectory)
    {
        Name = name;
        Path = path;
        IsDirectory = isDirectory;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }
    public string Path { get; }
    public bool IsDirectory { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
    public bool ShowCheckBox { get; set; }
    public string Icon => IsDirectory ? "▸" : "·";
}

public sealed record RemoteBrowseResult(string Path, bool IsDirectory,
    IReadOnlyList<string>? Paths = null);

public partial class RemoteBrowserWindow : Window
{
    private readonly HostRow _host;
    private readonly RemoteBrowseMode _mode;
    private readonly ObservableCollection<RemoteEntry> _entries = new();
    private int _loadVersion;
    private string? _password;
    private bool _busy;
    private int _captureSelectionCount;
    private Task<PreparedPpkKey>? _ppkTask;
    private PreparedPpkKey? _ppk;

    public RemoteBrowserWindow(HostRow host, RemoteBrowseMode mode)
    {
        InitializeComponent();
        _host = host;
        _mode = mode;
        Title = mode == RemoteBrowseMode.UploadDestination
            ? "Choose remote folder"
            : "Choose remote files or folders";
        HeadingText.Text = Title;
        TargetText.Text = string.IsNullOrWhiteSpace(host.User)
            ? $"{host.Address}:{host.Port}"
            : $"{host.User}@{host.Address}:{host.Port}";
        Entries.ItemsSource = _entries;
        ChooseButton.Content = mode == RemoteBrowseMode.UploadDestination
            ? "Use selected folder"
            : "Choose selection";
        Closing += (_, _) =>
        {
            _password = null;
            _ppk?.Dispose();
            _ppk = null;
        };
        Opened += async (_, _) => await LoadAsync("~");
    }

    /// <summary>Marks the first rows for README captures.</summary>
    public void SelectForCapture(int count) => _captureSelectionCount = count;

    private async void Up_Click(object? sender, RoutedEventArgs e)
    {
        var current = Normalize(PathBox.Text);
        await LoadAsync(ParentOf(current));
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

    private void Entries_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UpdateSelectionSummary();

    private void EntryCheckBox_Click(object? sender, RoutedEventArgs e) =>
        UpdateSelectionSummary();

    private void Choose_Click(object? sender, RoutedEventArgs e)
    {
        var current = Normalize(PathBox.Text);
        var selected = Entries.SelectedItem as RemoteEntry;
        if (_mode == RemoteBrowseMode.Download)
        {
            var checkedEntries = _entries.Where(entry => entry.IsSelected).ToList();
            if (checkedEntries.Count > 0)
            {
                Close(new RemoteBrowseResult(checkedEntries[0].Path,
                    checkedEntries[0].IsDirectory,
                    checkedEntries.Select(entry => entry.Path).ToList()));
                return;
            }
            Close(new RemoteBrowseResult(selected?.Path ?? current,
                selected?.IsDirectory ?? true));
            return;
        }

        var path = selected is { IsDirectory: true } ? selected.Path : current;
        Close(new RemoteBrowseResult(path, selected?.IsDirectory ?? true));
    }

    private void UpdateSelectionSummary()
    {
        var selected = Entries.SelectedItem as RemoteEntry;
        var checkedEntries = _entries.Where(entry => entry.IsSelected).ToList();
        if (_mode == RemoteBrowseMode.Download && checkedEntries.Count > 0)
        {
            var names = string.Join(", ", checkedEntries.Take(3).Select(entry => entry.Name));
            SelectionText.Text = checkedEntries.Count == 1
                ? "Selected: " + checkedEntries[0].Path
                : $"{checkedEntries.Count} items selected · {names}" +
                  (checkedEntries.Count > 3 ? "…" : "");
            ChooseButton.Content = checkedEntries.Count > 1
                ? $"Choose {checkedEntries.Count} items"
                : "Choose selection";
        }
        else
        {
            SelectionText.Text = selected is null
                ? "No item selected"
                : (selected.IsDirectory ? "Folder: " : "File: ") + selected.Path;
            ChooseButton.Content = _mode == RemoteBrowseMode.UploadDestination
                ? "Use selected folder"
                : "Choose selection";
        }
        ChooseButton.IsEnabled = !_busy &&
            (checkedEntries.Count > 0 || selected is not null ||
             (_mode == RemoteBrowseMode.UploadDestination && _entries.Count > 0));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private async Task LoadAsync(string path)
    {
        path = Normalize(path);
        PathBox.Text = path;
        _ppkTask ??= PpkTransferKey.PrepareAsync(_host, this);
        _ppk = await _ppkTask;
        if (_ppk.Cancelled)
        {
            SetBusy(false, "Cancelled");
            return;
        }
        if (_ppk.Error is not null)
        {
            SetBusy(false, "PuTTY key error");
            SelectionText.Text = _ppk.Error;
            return;
        }

        var version = ++_loadVersion;
        SetBusy(true, "Reading remote folder…");
        var (entries, error) = await ListWithPasswordAsync(path);
        if (version != _loadVersion) return;
        _entries.Clear();
        if (error is not null)
        {
            SetBusy(false, error);
            UpdateSelectionSummary();
            return;
        }
        foreach (var entry in entries)
        {
            entry.ShowCheckBox = _mode == RemoteBrowseMode.Download;
            if (_mode == RemoteBrowseMode.Download && _captureSelectionCount > 0)
            {
                entry.IsSelected = true;
                _captureSelectionCount--;
            }
            entry.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(RemoteEntry.IsSelected)) UpdateSelectionSummary();
            };
            _entries.Add(entry);
        }
        SetBusy(false, entries.Count == 0 ? "This folder is empty" : $"{entries.Count} item(s)");
        UpdateSelectionSummary();
    }

    private async Task<(IReadOnlyList<RemoteEntry> Entries, string? Error)> ListWithPasswordAsync(
        string path)
    {
        var result = _password is null
            ? await RemoteListing.ListAsync(_host, path, identityOverride: _ppk?.KeyPath)
            : await ListWithAskPassAsync(path, _password);
        if (result.Error is null || !SshLauncher.IsPasswordFailure(255, result.Error))
            return result;

        if (_password is not null)
        {
            // A cached password was rejected; ask once more so the user can correct it.
            _password = null;
        }
        var password = await new SshPasswordWindow(_host).ShowDialog<string?>(this);
        if (string.IsNullOrEmpty(password))
            return (Array.Empty<RemoteEntry>(), "Password required to read the remote folder.");
        _password = password;
        return await ListWithAskPassAsync(path, password);
    }

    private async Task<(IReadOnlyList<RemoteEntry> Entries, string? Error)> ListWithAskPassAsync(
        string path, string password)
    {
        using var askpass = SshAskPass.Create(password);
        return await RemoteListing.ListAsync(_host, path, batchMode: false, askpass,
            identityOverride: _ppk?.KeyPath);
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        StatusText.Text = status;
        Entries.IsEnabled = !busy;
        UpdateSelectionSummary();
    }

    private static string Normalize(string? path)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0 || path == ".") return "~";
        if (path.Length > 1) path = path.TrimEnd('/');
        return path.Length == 0 ? "/" : path;
    }

    private static string ParentOf(string path)
    {
        if (path is "~" or "/" || path.Length == 0) return path;
        var index = path.LastIndexOf('/');
        if (index < 0) return "~";
        if (index == 0) return "/";
        var parent = path[..index];
        return parent == "" ? "/" : parent;
    }
}

internal static class RemoteListing
{
    public static async Task<(IReadOnlyList<RemoteEntry> Entries, string? Error)> ListAsync(
        HostRow host, string path, bool batchMode = true, SshAskPass? askpass = null,
        string? identityOverride = null)
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
            foreach (var arg in SshLauncher.BuildSshCommandArguments(host, command, batchMode,
                identityOverride))
                psi.ArgumentList.Add(arg);
            askpass?.Apply(psi);
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
