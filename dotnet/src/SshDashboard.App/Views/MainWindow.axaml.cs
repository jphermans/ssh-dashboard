using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SshDashboard.App.ViewModels;
using SshDashboard.Core;

namespace SshDashboard.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _themeTimer;
    private bool _syncingNav;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        TryLoadIcon();
        VersionText.Text = "Version " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "2.0.0");

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.HasSelection)
                or nameof(MainViewModel.CanEditBlock)
                or nameof(MainViewModel.SelectedBlock)
                or nameof(MainViewModel.SelectedKey)
                or nameof(MainViewModel.SelectedHost)
                or nameof(MainViewModel.ActiveView))
                UpdateChrome();
        };

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _toastTimer.Tick += (_, _) => { Toast.IsVisible = false; _toastTimer.Stop(); };

        _themeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _themeTimer.Tick += (_, _) =>
        {
            if (_vm.Settings.Theme == "system" &&
                PlatformInfo.ResolveTheme("system") != ThemeManager.Current)
                ThemeManager.Apply("system");
        };

        _vm.Load();
        ShowView(_vm.ActiveView, syncNav: true);
        _themeTimer.Start();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    public void ShowViewForCapture(string key) => ShowView(key, syncNav: true);

    public void ProbeFirstForCapture()
    {
        if (_vm.FilteredHosts.FirstOrDefault() is not { } host) return;
        _vm.SelectedHost = host;
        Test_Click(null, new RoutedEventArgs());
    }

    public void OpenFirstSessionForCapture()
    {
        if (_vm.FilteredHosts.FirstOrDefault() is { } host) SshLauncher.OpenTerminal(host);
    }

    private void TryLoadIcon()
    {
        try
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(
                new Uri("avares://SshDashboard/Assets/appicon.png"));
            Icon = new WindowIcon(new Bitmap(stream));
        }
        catch { }
    }

    // ---------------------------------------------------------------- chrome

    private void ShowView(string key, bool syncNav = false)
    {
        HostsView.IsVisible = key == "hosts";
        ConfigView.IsVisible = key == "config";
        KeysView.IsVisible = key == "keys";
        ActivityView.IsVisible = key == "activity";

        (ViewTitle.Text, ViewSubtitle.Text) = key switch
        {
            "config" => ("Configuration", "Host blocks in ~/.ssh/config. Double-click a row to edit it."),
            "keys" => ("Keys", "Private and public key files found in ~/.ssh."),
            "activity" => ("Activity", "A local journal of every change this app made."),
            _ => ("Hosts", "Every server listed in known_hosts or ssh_config."),
        };

        _vm.ActiveView = key;
        if (key != "activity") _vm.SetView(key);
        if (syncNav)
        {
            _syncingNav = true;
            Nav.SelectedIndex = key switch
            {
                "config" => 1,
                "keys" => 2,
                "activity" => 3,
                _ => 0,
            };
            _syncingNav = false;
        }
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        var hosts = _vm.ActiveView == "hosts";
        var config = _vm.ActiveView == "config";
        var keys = _vm.ActiveView == "keys";

        DeleteSelectedButton.IsVisible = hosts && _vm.HasSelection;
        OpenSessionButton.IsVisible = hosts && _vm.SelectedHost is not null;
        EditBlockButton.IsVisible = config && _vm.CanEditBlock;
        DeleteBlockButton.IsVisible = config && _vm.SelectedBlock?.IsEditable == true;
        RevealKeyButton.IsVisible = keys && _vm.SelectedKey is not null;
        if (hosts) _vm.Raise(nameof(MainViewModel.SelectionSummary));
    }

    private void Nav_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingNav || _vm is null || Nav.SelectedItem is not ListBoxItem item) return;
        if (item.Tag is string key) ShowView(key);
    }

    private void Search_Changed(object? sender, TextChangedEventArgs e) =>
        _vm.Search = SearchBox.Text ?? "";

    // ---------------------------------------------------------------- hosts

    private void Hosts_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        _vm.SelectedHost = HostsGrid.SelectedItem as HostRow;

    private void Config_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        _vm.SelectedBlock = ConfigGrid.SelectedItem as ConfigBlock;

    private void Keys_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        _vm.SelectedKey = KeysGrid.SelectedItem as KeyInfo;

    private void Hosts_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (_vm.SelectedHost is { } host) SshLauncher.OpenTerminal(host);
    }

    private async void Config_DoubleTapped(object? sender, RoutedEventArgs e) =>
        await EditSelectedBlockAsync();

    private void Keys_DoubleTapped(object? sender, RoutedEventArgs e) => RevealSelectedKey();

    // ---------------------------------------------------------------- actions

    private void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        _vm.Load();
        UpdateChrome();
        ShowToast("Rescanned ~/.ssh");
    }

    private void OpenSshFolder_Click(object? sender, RoutedEventArgs e) =>
        PlatformInfo.Reveal(_vm.Store.SshDir);

    private void OpenBackupsFolder_Click(object? sender, RoutedEventArgs e) =>
        PlatformInfo.Reveal(_vm.Store.BackupsDir);

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm.ActiveView == "config")
        {
            await DeleteSelectedBlockAsync();
            return;
        }

        var rows = _vm.FilteredHosts.Where(r => r.IsSelected).ToList();
        if (rows.Count == 0)
        {
            ShowToast("Select one or more hosts first");
            return;
        }

        if (_vm.Settings.ConfirmDelete)
        {
            var detail = string.Join("\n", rows.Take(8).Select(r => $"• {r.Label}  ({r.Address}:{r.Port})"));
            if (rows.Count > 8) detail += $"\n…and {rows.Count - 8} more";
            var blocks = rows.Sum(r => r.ConfigStarts.Count);
            if (blocks > 0 && _vm.Settings.RemoveConfigDefault)
                detail += $"\n\n{blocks} matching ssh_config block(s) will be removed too.";
            var ok = await Dialogs.ConfirmAsync(this, "Remove hosts",
                rows.Count == 1 ? "Remove this host?" : $"Remove {rows.Count} hosts?",
                "Known_hosts lines are removed completely. A timestamped backup is written first.",
                detail, "Remove", danger: true);
            if (!ok) return;
        }

        try
        {
            var result = _vm.Store.DeleteHosts(rows, _vm.Settings.RemoveConfigDefault);
            _vm.Load();
            UpdateChrome();
            var parts = new List<string>();
            if (result.KnownHostLines > 0) parts.Add($"{result.KnownHostLines} line(s)");
            if (result.ConfigBlocks > 0) parts.Add($"{result.ConfigBlocks} block(s)");
            ShowToast(parts.Count == 0
                ? "Nothing matched — files were left untouched"
                : "Removed " + string.Join(" and ", parts) + " · backup saved");
        }
        catch (Exception ex)
        {
            await Dialogs.ConfirmAsync(this, "Could not remove", "Nothing was changed",
                ex.Message, "", "Close", danger: false);
        }
    }

    private async Task DeleteSelectedBlockAsync()
    {
        if (_vm.SelectedBlock is not { IsEditable: true } block)
        {
            ShowToast("Select an editable Host block first");
            return;
        }
        if (_vm.Settings.ConfirmDelete)
        {
            var ok = await Dialogs.ConfirmAsync(this, "Remove block",
                $"Remove “{block.Label}”?",
                "The whole Host block (lines " + (block.StartLine + 1) + "–" + (block.EndLine + 1) +
                ") is removed. A timestamped backup is written first.",
                "", "Remove", danger: true);
            if (!ok) return;
        }
        try
        {
            var result = _vm.Store.DeleteConfigBlocks(new[] { block.StartLine });
            _vm.Load();
            UpdateChrome();
            ShowToast(result.ConfigBlocks > 0 ? "Block removed · backup saved" : "Nothing was removed");
        }
        catch (Exception ex)
        {
            await Dialogs.ConfirmAsync(this, "Could not remove", "Nothing was changed",
                ex.Message, "", "Close", danger: false);
        }
    }

    private async void EditBlock_Click(object? sender, RoutedEventArgs e) =>
        await EditSelectedBlockAsync();

    private void DeleteBlock_Click(object? sender, RoutedEventArgs e) =>
        _ = DeleteSelectedBlockAsync();

    private async Task EditSelectedBlockAsync()
    {
        if (_vm.SelectedBlock is not { } block) return;
        if (!block.IsEditable)
        {
            ShowToast("Only plain Host blocks can be edited");
            return;
        }
        var result = await Dialogs.EditBlockAsync(this, block);
        if (result is null) return;
        try
        {
            _vm.Store.UpdateBlock(block, result.Patterns, result.Values);
            _vm.Load();
            ShowToast("Block updated · backup saved");
        }
        catch (Exception ex)
        {
            await Dialogs.ConfirmAsync(this, "Could not save", "The file was left unchanged",
                ex.Message, "", "Close", danger: false);
        }
    }

    private async void Undo_Click(object? sender, RoutedEventArgs e)
    {
        var backups = _vm.Store.ListBackups();
        if (backups.Count == 0)
        {
            ShowToast("No backups yet");
            return;
        }
        var latest = backups[0];
        var ok = await Dialogs.ConfirmAsync(this, "Restore backup",
            "Restore the latest backup?",
            "This copies the backup over the current file. The current file is not backed up again.",
            Path.GetFileName(latest), "Restore", danger: true);
        if (!ok) return;
        try
        {
            _vm.Store.RestoreBackup(latest);
            _vm.Load();
            ShowToast("Restored " + Path.GetFileName(latest));
        }
        catch (Exception ex)
        {
            await Dialogs.ConfirmAsync(this, "Could not restore", "Nothing was changed",
                ex.Message, "", "Close", danger: false);
        }
    }

    private async void Export_Click(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export to CSV",
            SuggestedFileName = $"ssh-dashboard-{_vm.ActiveView}-{DateTime.Now:yyyyMMdd}.csv",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } },
            },
        });
        if (file is null) return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(_vm.ExportCurrent());
            ShowToast("Exported CSV");
        }
        catch (Exception ex)
        {
            await Dialogs.ConfirmAsync(this, "Export failed", "Could not write the file",
                ex.Message, "", "Close", danger: false);
        }
    }

    private IBrush BrushOr(string key, IBrush fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush : fallback;

    private async void Test_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm.SelectedHost is not { } host) return;
        try
        {
            TestResult.Text = "Testing…";
            TestResult.Foreground = BrushOr("TextMuted", Brushes.Gray);
            var (reachable, error) = await PlatformInfo.ProbeAsync(host.Address, host.Port);
            TestResult.Text = reachable
                ? $"Port {host.Port} reachable"
                : $"Unreachable · {error}";
            TestResult.Foreground = BrushOr(reachable ? "SuccessBrush" : "DangerBrush", Brushes.Gray);
        }
        catch (Exception ex)
        {
            TestResult.Text = "Test failed: " + ex.Message;
            TestResult.Foreground = BrushOr("DangerBrush", Brushes.IndianRed);
        }
    }

    private void Ssh_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm.SelectedHost is { } host)
        {
            SshLauncher.OpenTerminal(host);
            ShowToast("Opening SSH session");
        }
        else ShowToast("Select a host first");
    }

    private void RevealKey_Click(object? sender, RoutedEventArgs e) => RevealSelectedKey();

    private void RevealSelectedKey()
    {
        if (_vm.SelectedKey is { } key) PlatformInfo.Reveal(key.Path);
        else if (_vm.ActiveView == "keys") PlatformInfo.Reveal(_vm.Store.SshDir);
    }

    private async void Settings_Click(object? sender, RoutedEventArgs e)
    {
        var result = await Dialogs.SettingsAsync(this, _vm.Settings,
            preview => ThemeManager.Apply(preview));
        if (result is null)
        {
            ThemeManager.Apply(_vm.Settings.Theme);
            _vm.Raise(nameof(MainViewModel.ThemeLabel));
            return;
        }
        _vm.Settings.Theme = result.Theme;
        _vm.Settings.ConfirmDelete = result.ConfirmDelete;
        _vm.Settings.RemoveConfigDefault = result.RemoveConfigDefault;
        _vm.Settings.Save();
        ThemeManager.Apply(_vm.Settings.Theme);
        _vm.Raise(nameof(MainViewModel.ThemeLabel));
        ShowToast("Settings saved");
    }

    private void Theme_Click(object? sender, RoutedEventArgs e)
    {
        _vm.CycleTheme();
        ShowToast(_vm.ThemeLabel);
    }

    private void ThemeSystem_Click(object? sender, RoutedEventArgs e) => SetTheme("system");
    private void ThemeDark_Click(object? sender, RoutedEventArgs e) => SetTheme("dark");
    private void ThemeLight_Click(object? sender, RoutedEventArgs e) => SetTheme("light");

    private void SetTheme(string preference)
    {
        _vm.Settings.Theme = preference;
        _vm.Settings.Save();
        ThemeManager.Apply(preference);
        _vm.Raise(nameof(MainViewModel.ThemeLabel));
        ShowToast(_vm.ThemeLabel);
    }

    private void SelectAll_Click(object? sender, RoutedEventArgs e) => _vm.SelectAll();
    private void ClearSelection_Click(object? sender, RoutedEventArgs e) => _vm.ClearSelection();

    private void ViewHosts_Click(object? sender, RoutedEventArgs e) => ShowView("hosts", true);
    private void ViewConfig_Click(object? sender, RoutedEventArgs e) => ShowView("config", true);
    private void ViewKeys_Click(object? sender, RoutedEventArgs e) => ShowView("keys", true);
    private void ViewActivity_Click(object? sender, RoutedEventArgs e) => ShowView("activity", true);

    private void Quit_Click(object? sender, RoutedEventArgs e) => Close();

    private async void About_Click(object? sender, RoutedEventArgs e) =>
        await Dialogs.AboutAsync(this,
            typeof(App).Assembly.GetName().Version?.ToString(3) ?? "2.0.0",
            _vm.Store.SshDir);

    private void Help_Click(object? sender, RoutedEventArgs e) => new HelpWindow().Show(this);

    private async void Shortcuts_Click(object? sender, RoutedEventArgs e) =>
        await Dialogs.ConfirmAsync(this, "Keyboard shortcuts", "Keyboard shortcuts",
            "F5 rescans. Delete removes the selection. Cmd/Ctrl+A selects all hosts, " +
            "Cmd/Ctrl+E exports, Cmd/Ctrl+, opens settings, Cmd/Ctrl+Z restores the latest backup. " +
            "Cmd/Ctrl+1…4 switch views. Cmd/Ctrl+Shift+T opens an SSH session.",
            "", "Close", danger: false);

    // ---------------------------------------------------------------- helpers

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.IsVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var mod = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        var command = (e.KeyModifiers & mod) == mod;

        if (e.Key == Key.F1) { Help_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.F5) { Refresh_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            Delete_Click(sender, e);
            e.Handled = true;
        }
        else if (command && e.Key == Key.A) { _vm.SelectAll(); e.Handled = true; }
        else if (command && e.Key == Key.E) { Export_Click(sender, e); e.Handled = true; }
        else if (command && e.Key == Key.OemComma) { Settings_Click(sender, e); e.Handled = true; }
        else if (command && e.Key == Key.Z) { Undo_Click(sender, e); e.Handled = true; }
        else if (command && e.Key == Key.D1) { ShowView("hosts", true); e.Handled = true; }
        else if (command && e.Key == Key.D2) { ShowView("config", true); e.Handled = true; }
        else if (command && e.Key == Key.D3) { ShowView("keys", true); e.Handled = true; }
        else if (command && e.Key == Key.D4) { ShowView("activity", true); e.Handled = true; }
        else if (command && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.T)
        {
            Ssh_Click(sender, e);
            e.Handled = true;
        }
    }
}
