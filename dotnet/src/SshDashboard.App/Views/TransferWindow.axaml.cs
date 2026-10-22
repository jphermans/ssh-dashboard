using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SshDashboard.Core;

namespace SshDashboard.App.Views;

public partial class TransferWindow : Window
{
    private readonly HostRow _host;
    private readonly ObservableCollection<string> _sources = new();
    private readonly ObservableCollection<string> _remoteSources = new();
    private Process? _process;
    private bool _running;
    private bool _cancelled;

    public TransferWindow(HostRow host)
    {
        InitializeComponent();
        _host = host;
        Title = "Transfer files — " + host.Label;
        TargetText.Text = string.IsNullOrWhiteSpace(host.User)
            ? $"{host.Address}:{host.Port}"
            : $"{host.User}@{host.Address}:{host.Port}";
        SourcesList.ItemsSource = _sources;
        RemoteSourcesList.ItemsSource = _remoteSources;
        LocalDestination.Text = DefaultDownloadFolder();
        UploadRadio.IsCheckedChanged += (_, _) => UpdateMode();
        DownloadRadio.IsCheckedChanged += (_, _) => UpdateMode();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Closing += (_, _) => CancelProcess();
        UpdateMode();
    }

    /// <summary>Fills a representative download state for README captures.</summary>
    public void SetDownloadCaptureState()
    {
        DownloadRadio.IsChecked = true;
        _remoteSources.Clear();
        _remoteSources.Add("~/backups");
        _remoteSources.Add("~/configs");
        RemoteSource.Text = _remoteSources[0];
        RemoteSourcesBox.IsVisible = true;
        StatusText.Text = "2 remote items selected";
        Progress.IsVisible = true;
        Progress.IsIndeterminate = false;
        Progress.Value = 38;
        ProgressText.Text = "38%";
        FooterHint.Text = "Transfer in progress…";
        TransferButton.Content = "Cancel";
        LogBox.Text = "backups/  38%  420MB  12.1MB/s  00:12" + Environment.NewLine;
    }

    /// <summary>Fills a representative state for README captures.</summary>
    public void SetCaptureState()
    {
        AddSource("~/Documents/server-backup.tar.gz");
        AddSource("~/Projects/site");
        RemoteDestination.Text = "~/incoming";
        StatusText.Text = "Uploading…";
        Progress.IsVisible = true;
        Progress.IsIndeterminate = false;
        Progress.Value = 64;
        ProgressText.Text = "64%";
        FooterHint.Text = "Transfer in progress…";
        TransferButton.Content = "Cancel";
        LogBox.Text = "server-backup.tar.gz  64%  1.2GB  18.4MB/s  00:04" + Environment.NewLine;
    }

    private void UpdateMode()
    {
        var upload = UploadRadio.IsChecked == true;
        UploadPanel.IsVisible = upload;
        DownloadPanel.IsVisible = !upload;
        if (!upload && string.IsNullOrWhiteSpace(LocalDestination.Text))
            LocalDestination.Text = DefaultDownloadFolder();
        FooterHint.Text = upload ? "Copy local files or folders to the instance" :
            "Copy a remote file or folder into your Downloads folder";
    }

    private static string DefaultDownloadFolder()
    {
        var downloads = Path.Combine(PlatformInfo.Home, "Downloads");
        return Directory.Exists(downloads) ? downloads : PlatformInfo.Home;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var accepts = UploadRadio.IsChecked == true && e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropActive(accepts);
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        SetDropActive(false);
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        SetDropActive(false);
        e.Handled = true;
        if (UploadRadio.IsChecked != true || !e.DataTransfer.Contains(DataFormat.File)) return;

        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;
        foreach (var item in files)
        {
            var path = item.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path)) AddSource(path);
        }
    }

    private void SetDropActive(bool active)
    {
        if (active) DropZone.Classes.Add("drop-active");
        else DropZone.Classes.Remove("drop-active");
    }

    private async void PickFiles_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose files to upload",
            AllowMultiple = true,
        });
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path)) AddSource(path);
        }
    }

    private async void PickFolder_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose folders to upload",
            AllowMultiple = true,
        });
        foreach (var folder in folders)
        {
            var path = folder.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path)) AddSource(path);
        }
    }

    private void AddSource(string path)
    {
        if (!_sources.Contains(path)) _sources.Add(path);
        StatusText.Text = $"{_sources.Count} source(s) selected";
    }

    private void ClearSources_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _sources.Clear();
        StatusText.Text = "Ready";
    }

    private async void BrowseUploadDestination_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var result = await new RemoteBrowserWindow(_host, RemoteBrowseMode.UploadDestination)
            .ShowDialog<RemoteBrowseResult?>(this);
        if (result is not null) RemoteDestination.Text = result.Path;
    }

    private async void BrowseRemoteSource_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var result = await new RemoteBrowserWindow(_host, RemoteBrowseMode.Download)
            .ShowDialog<RemoteBrowseResult?>(this);
        if (result is null) return;

        var paths = result.Paths is { Count: > 0 } ? result.Paths : new[] { result.Path };
        _remoteSources.Clear();
        foreach (var path in paths.Distinct(StringComparer.Ordinal)) _remoteSources.Add(path);
        RemoteSource.Text = result.Path;
        RemoteSourcesBox.IsVisible = _remoteSources.Count > 0;
        StatusText.Text = _remoteSources.Count == 1
            ? "1 remote item selected"
            : $"{_remoteSources.Count} remote items selected";
    }

    private void RemoteSource_TextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        if (_remoteSources.Count == 0) return;
        // TextChanged can be raised after a programmatic selection. Only clear
        // the list when the field no longer matches the first selected path.
        if (string.Equals(RemoteSource.Text?.Trim(), _remoteSources[0].Trim(),
                StringComparison.Ordinal)) return;
        _remoteSources.Clear();
        RemoteSourcesBox.IsVisible = false;
    }

    private void ClearRemoteSources_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _remoteSources.Clear();
        RemoteSourcesBox.IsVisible = false;
        RemoteSource.Text = "";
        StatusText.Text = "Ready";
    }

    private async void PickDestination_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where to save downloaded files",
            AllowMultiple = false,
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path)) LocalDestination.Text = path;
    }

    private async void Transfer_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_running)
        {
            CancelProcess();
            return;
        }

        var upload = UploadRadio.IsChecked == true;
        if (upload && _sources.Count == 0)
        {
            StatusText.Text = "Choose at least one file or folder first.";
            return;
        }
        if (upload && string.IsNullOrWhiteSpace(RemoteDestination.Text))
        {
            StatusText.Text = "Enter a remote destination path.";
            return;
        }
        if (!upload && _remoteSources.Count == 0 && string.IsNullOrWhiteSpace(RemoteSource.Text))
        {
            StatusText.Text = "Choose at least one remote file or folder first.";
            return;
        }
        if (!upload && string.IsNullOrWhiteSpace(LocalDestination.Text))
        {
            StatusText.Text = "Choose a local destination folder first.";
            return;
        }

        LogBox.Text = "";
        var ppk = await PpkTransferKey.PrepareAsync(_host, this);
        if (ppk.Cancelled)
        {
            ppk.Dispose();
            StatusText.Text = "Cancelled";
            return;
        }
        if (ppk.Error is not null)
        {
            ppk.Dispose();
            StatusText.Text = "PuTTY key error";
            Append("PuTTY key: " + ppk.Error);
            return;
        }

        using var _ppkScope = ppk;
        var useRsync = SshLauncher.HasRsync;
        _cancelled = false;
        SetRunning(true, upload ? "Uploading…" : "Downloading…", useRsync);

        var target = SshLauncher.TransferTarget(_host);
        List<string> BuildArgs(bool allowPassword)
        {
            if (!upload)
            {
                var remotePaths = _remoteSources.Count > 0
                    ? _remoteSources.ToList()
                    : new List<string> { RemoteSource.Text! };
                return SshLauncher.BuildDownloadArguments(_host, useRsync, remotePaths,
                    LocalDestination.Text!, allowPassword, ppk.KeyPath);
            }

            var args = useRsync
                ? SshLauncher.BuildRsyncArguments(_host, batchMode: !allowPassword,
                    identityOverride: ppk.KeyPath)
                : SshLauncher.BuildScpArguments(_host, batchMode: !allowPassword,
                    identityOverride: ppk.KeyPath);
            foreach (var source in _sources) args.Add(source);
            args.Add($"{target}:{SshLauncher.EscapeRemotePath(RemoteDestination.Text!)}");
            return args;
        }

        try
        {
            var result = await RunAsync(BuildArgs(false), useRsync, null);
            if (!_cancelled && result.Code != 0 &&
                SshLauncher.IsPasswordFailure(result.Code, result.Error))
            {
                SetRunning(true, "Password required", useRsync);
                var password = await new SshPasswordWindow(_host).ShowDialog<string?>(this);
                if (!string.IsNullOrEmpty(password))
                {
                    Progress.Value = 0;
                    ProgressText.Text = useRsync ? "0%" : "copying…";
                    using var askpass = SshAskPass.Create(password);
                    result = await RunAsync(BuildArgs(true), useRsync, askpass);
                }
            }

            var cancelled = _cancelled;
            SetRunning(false, cancelled ? "Cancelled" :
                result.Code == 0 ? "Transfer complete" : $"Transfer exited with code {result.Code}");
            if (result.Code == 0 && !cancelled)
            {
                Progress.IsIndeterminate = false;
                Progress.Value = 100;
                ProgressText.Text = "100%";
                Progress.IsVisible = true;
                if (upload)
                {
                    _sources.Clear();
                    StatusText.Text = "Transfer complete · source list cleared";
                    FooterHint.Text = "Source list cleared — choose files again to copy";
                }
            }
            else if (!cancelled)
            {
                Append("If the password or key is rejected, check the account and load the key in ssh-agent.", useRsync);
            }
        }
        catch (Exception ex)
        {
            SetRunning(false, "Transfer failed");
            Append(ex.Message, useRsync);
        }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }

    private async Task<(int Code, string Error)> RunAsync(
        List<string> args, bool useRsync, SshAskPass? askpass)
    {
        var psi = new ProcessStartInfo(useRsync ? "rsync" : "scp")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        askpass?.Apply(psi);
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var error = new System.Text.StringBuilder();
        _process = process;
        process.Start();
        // Read both streams asynchronously. Waiting only for process exit can
        // return before the final stderr line is delivered, which would hide
        // an authentication failure and skip the password prompt.
        var stdoutTask = PumpAsync(process.StandardOutput, line => Append(line, useRsync));
        var stderrTask = PumpAsync(process.StandardError, line =>
        {
            if (!string.IsNullOrWhiteSpace(line)) error.AppendLine(line);
            Append(line, useRsync);
        });
        await process.WaitForExitAsync();
        await Task.WhenAll(stdoutTask, stderrTask);
        return (process.ExitCode, error.ToString());
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        while (await reader.ReadLineAsync() is { } line) onLine(line);
    }

    private void Append(string? line, bool rsync = false)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        Dispatcher.UIThread.Post(() =>
        {
            LogBox.Text += line + Environment.NewLine;
            LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
            if (rsync && Progress.IsVisible)
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d{1,3})%");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var percent))
                {
                    Progress.Value = Math.Clamp(percent, 0, 100);
                    ProgressText.Text = Progress.Value + "%";
                }
            }
        });
    }

    private void SetRunning(bool running, string status, bool determinate = false)
    {
        _running = running;
        StatusText.Text = status;
        Progress.IsVisible = running;
        Progress.IsIndeterminate = !determinate;
        if (running && determinate)
        {
            Progress.Value = 0;
            ProgressText.Text = "0%";
        }
        else if (running)
        {
            ProgressText.Text = "copying…";
        }
        TransferButton.Content = running ? "Cancel" : "Start transfer";
        UploadRadio.IsEnabled = !running;
        DownloadRadio.IsEnabled = !running;
        UploadPanel.IsEnabled = !running;
        DownloadPanel.IsEnabled = !running;
        FooterHint.Text = running ? "Transfer in progress…" : status;
    }

    private void CancelProcess()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _cancelled = true;
                StatusText.Text = "Cancelling…";
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may have exited between the check and the kill.
        }
    }

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
