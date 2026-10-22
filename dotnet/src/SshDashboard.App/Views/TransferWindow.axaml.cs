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
        if (result is not null) RemoteSource.Text = result.Path;
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
        if (!upload && string.IsNullOrWhiteSpace(RemoteSource.Text))
        {
            StatusText.Text = "Enter a remote source path.";
            return;
        }
        if (!upload && string.IsNullOrWhiteSpace(LocalDestination.Text))
        {
            StatusText.Text = "Choose a local destination folder first.";
            return;
        }

        var useRsync = SshLauncher.HasRsync;
        LogBox.Text = "";
        _cancelled = false;
        SetRunning(true, upload ? "Uploading…" : "Downloading…", useRsync);

        var target = SshLauncher.TransferTarget(_host);
        List<string> BuildArgs(bool allowPassword)
        {
            var args = useRsync
                ? SshLauncher.BuildRsyncArguments(_host, batchMode: !allowPassword)
                : SshLauncher.BuildScpArguments(_host, batchMode: !allowPassword);
            if (upload)
            {
                foreach (var source in _sources) args.Add(source);
                args.Add($"{target}:{SshLauncher.EscapeRemotePath(RemoteDestination.Text!)}");
            }
            else
            {
                args.Add($"{target}:{SshLauncher.EscapeRemotePath(RemoteSource.Text!)}");
                args.Add(LocalDestination.Text!);
            }
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
        process.OutputDataReceived += (_, data) => Append(data.Data, useRsync);
        process.ErrorDataReceived += (_, data) =>
        {
            if (!string.IsNullOrWhiteSpace(data.Data)) error.AppendLine(data.Data);
            Append(data.Data, useRsync);
        };
        _process = process;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        return (process.ExitCode, error.ToString());
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
