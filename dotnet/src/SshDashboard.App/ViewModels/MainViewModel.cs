using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SshDashboard.Core;

namespace SshDashboard.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly List<HostRow> _allHosts = new();
    private readonly List<ConfigBlock> _allConfig = new();
    private readonly List<KeyInfo> _allKeys = new();
    private string _search = "";
    private string _activeView = "hosts";
    private HostRow? _selectedHost;
    private ConfigBlock? _selectedBlock;
    private KeyInfo? _selectedKey;
    private string _status = "";
    private string _detailTitle = "Nothing selected";
    private string _detailSubtitle = "Choose a row to see its details.";
    private string _detailBody = "";
    private string _detailKind = "";
    private bool _isBusy;

    public MainViewModel(Settings? settings = null)
    {
        Settings = settings ?? Settings.Load();
        _activeView = Settings.LastView is "hosts" or "config" or "keys" or "activity"
            ? Settings.LastView : "hosts";
        Store = new SshStore();
    }

    public Settings Settings { get; }
    public SshStore Store { get; }
    public ActivityLog Log { get; } = new();

    public ObservableCollection<HostRow> FilteredHosts { get; } = new();
    public ObservableCollection<ConfigBlock> FilteredConfig { get; } = new();
    public ObservableCollection<KeyInfo> FilteredKeys { get; } = new();
    public ObservableCollection<ActivityEntry> Activity { get; } = new();

    public string Search
    {
        get => _search;
        set { if (_search == value) return; _search = value; ApplyFilter(); }
    }

    public string ActiveView
    {
        get => _activeView;
        set { if (_activeView == value) return; _activeView = value; Raise(); }
    }

    public HostRow? SelectedHost
    {
        get => _selectedHost;
        set
        {
            if (ReferenceEquals(_selectedHost, value)) return;
            _selectedHost = value;
            Raise();
            RaiseSelectionDetails();
        }
    }

    public ConfigBlock? SelectedBlock
    {
        get => _selectedBlock;
        set
        {
            if (ReferenceEquals(_selectedBlock, value)) return;
            _selectedBlock = value;
            Raise();
            RaiseSelectionDetails();
        }
    }

    public KeyInfo? SelectedKey
    {
        get => _selectedKey;
        set
        {
            if (ReferenceEquals(_selectedKey, value)) return;
            _selectedKey = value;
            Raise();
            RaiseSelectionDetails();
        }
    }

    public string Status { get => _status; private set { _status = value; Raise(); } }
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; Raise(); } }

    public string DetailTitle { get => _detailTitle; private set { _detailTitle = value; Raise(); } }
    public string DetailSubtitle { get => _detailSubtitle; private set { _detailSubtitle = value; Raise(); } }
    public string DetailBody { get => _detailBody; private set { _detailBody = value; Raise(); } }
    public string DetailKind { get => _detailKind; private set { _detailKind = value; Raise(); } }

    public bool ShowTestAction => DetailKind == "host" && SelectedHost is { IsHashed: false };
    public bool ShowEditAction => DetailKind == "config";
    public bool ShowRevealAction => DetailKind == "key";

    public int HostCount => _allHosts.Count;
    public int ConfigCount => _allConfig.Count;
    public int KeyCount => _allKeys.Count;
    public int ActivityCount => Activity.Count;
    public string BackupCountText => $"{Store.ListBackups().Count} backups";

    public int SelectedCount => _allHosts.Count(h => h.IsSelected);
    public bool HasSelection => SelectedCount > 0;
    public bool CanEditBlock => SelectedBlock?.IsEditable == true;

    public string SelectionSummary
    {
        get
        {
            if (ActiveView == "hosts")
            {
                if (SelectedCount == 0) return $"{_allHosts.Count} hosts · select rows to remove them";
                return SelectedCount == 1
                    ? $"1 of {_allHosts.Count} selected"
                    : $"{SelectedCount} of {_allHosts.Count} selected";
            }
            if (ActiveView == "config") return $"{_allConfig.Count} Host blocks";
            if (ActiveView == "keys") return $"{_allKeys.Count} key files";
            return $"{Activity.Count} recent actions";
        }
    }

    public string ThemeLabel => Settings.Theme switch
    {
        "dark" => "Dark theme",
        "light" => "Light theme",
        _ => "System theme",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Load()
    {
        IsBusy = true;
        try
        {
            var snapshot = Store.Load();
            foreach (var row in _allHosts) row.PropertyChanged -= RowChanged;
            _allHosts.Clear();
            _allHosts.AddRange(snapshot.Hosts);
            foreach (var row in _allHosts) row.PropertyChanged += RowChanged;

            _allConfig.Clear();
            _allConfig.AddRange(snapshot.Blocks.Where(b => !b.IsMatch || b.Patterns.Count > 0));
            _allKeys.Clear();
            _allKeys.AddRange(snapshot.Keys);

            Activity.Clear();
            foreach (var entry in Log.Recent(120)) Activity.Add(entry);

            ApplyFilter();
            Status = $"Updated {DateTime.Now:HH:mm:ss} · {snapshot.BackupCount} backups";
            Raise(nameof(HostCount));
            Raise(nameof(ConfigCount));
            Raise(nameof(KeyCount));
            Raise(nameof(BackupCountText));
            Raise(nameof(SelectionSummary));
            Raise(nameof(HasSelection));
            Raise(nameof(SelectedCount));
            RaiseSelectionDetails();
        }
        catch (Exception ex)
        {
            Status = "Could not read ~/.ssh: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HostRow.IsSelected))
        {
            Raise(nameof(SelectedCount));
            Raise(nameof(HasSelection));
            Raise(nameof(SelectionSummary));
        }
    }

    public void ApplyFilter()
    {
        var query = _search.Trim();
        bool Match(params string?[] values) =>
            query.Length == 0 || values.Any(v => v is not null &&
                v.Contains(query, StringComparison.OrdinalIgnoreCase));

        FilteredHosts.Clear();
        foreach (var row in _allHosts.Where(r => Match(r.Label, r.Address, r.User, r.KeyTypes, r.Source)))
            FilteredHosts.Add(row);

        FilteredConfig.Clear();
        foreach (var block in _allConfig.Where(b => Match(b.Label, b.HostName, b.User, b.IdentityFile, b.State)))
            FilteredConfig.Add(block);

        FilteredKeys.Clear();
        foreach (var key in _allKeys.Where(k => Match(k.Name, k.Kind, k.Fingerprint, k.Access)))
            FilteredKeys.Add(key);
    }

    public void SetView(string view)
    {
        ActiveView = view;
        Settings.LastView = view;
        Settings.Save();
        Raise(nameof(SelectionSummary));
    }

    private void RaiseSelectionDetails()
    {
        if (SelectedHost is not null)
        {
            DetailKind = "host";
            DetailTitle = SelectedHost.Label;
            DetailSubtitle = SelectedHost.Address + (SelectedHost.Port != 22 ? $":{SelectedHost.Port}" : "");
            var lines = new List<string>
            {
                $"Address    {SelectedHost.Address}",
                $"User       {(string.IsNullOrEmpty(SelectedHost.User) ? "—" : SelectedHost.User)}",
                $"Port       {SelectedHost.Port}",
                $"Keys       {(string.IsNullOrEmpty(SelectedHost.KeyTypes) ? "—" : SelectedHost.KeyTypes)}",
                $"Identity   {(string.IsNullOrEmpty(SelectedHost.IdentityFile) ? "—" : SelectedHost.IdentityFile)}",
                $"Source     {SelectedHost.Source}",
            };
            if (SelectedHost.ConfigStarts.Count > 0)
                lines.Add($"Config     {SelectedHost.ConfigStarts.Count} matching Host block(s)");
            if (SelectedHost.IsHashed)
            {
                lines.Insert(0, "Hashed known_hosts entry");
                lines.Add("The hostname cannot be resolved from a hash.");
                lines.Add("Delete removes the whole line.");
            }
            DetailBody = string.Join("\n", lines);
        }
        else if (SelectedBlock is not null)
        {
            DetailKind = "config";
            DetailTitle = SelectedBlock.Label;
            DetailSubtitle = SelectedBlock.IsEditable ? "Editable Host block" : SelectedBlock.State + " block";
            DetailBody = string.Join("\n", new[]
            {
                $"HostName   {(string.IsNullOrEmpty(SelectedBlock.HostName) ? "—" : SelectedBlock.HostName)}",
                $"User       {(string.IsNullOrEmpty(SelectedBlock.User) ? "—" : SelectedBlock.User)}",
                $"Port       {(string.IsNullOrEmpty(SelectedBlock.PortText) ? "—" : SelectedBlock.PortText)}",
                $"Identity   {(string.IsNullOrEmpty(SelectedBlock.IdentityFile) ? "—" : SelectedBlock.IdentityFile)}",
                $"Lines      {SelectedBlock.LineCount} (line {SelectedBlock.StartLine + 1})",
                $"State      {SelectedBlock.State}",
            });
        }
        else if (SelectedKey is not null)
        {
            DetailKind = "key";
            DetailTitle = SelectedKey.Name;
            DetailSubtitle = SelectedKey.Kind;
            DetailBody = string.Join("\n", new[]
            {
                $"Type        {SelectedKey.Kind}",
                $"Fingerprint {(string.IsNullOrEmpty(SelectedKey.Fingerprint) ? "—" : SelectedKey.Fingerprint)}",
                $"Size        {SelectedKey.DisplaySize}",
                $"Access      {SelectedKey.Access}",
                $"Path        {SelectedKey.Path}",
            });
        }
        else
        {
            DetailKind = "";
            DetailTitle = "Nothing selected";
            DetailSubtitle = "Choose a row to see its details.";
            DetailBody = "";
        }
        Raise(nameof(ShowTestAction));
        Raise(nameof(ShowEditAction));
        Raise(nameof(ShowRevealAction));
        Raise(nameof(CanEditBlock));
    }

    public void CycleTheme()
    {
        Settings.Theme = Settings.Theme switch
        {
            "system" => "dark",
            "dark" => "light",
            _ => "system",
        };
        Settings.Save();
        ThemeManager.Apply(Settings.Theme);
        Raise(nameof(ThemeLabel));
    }

    public string ExportCurrent()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
        return ActiveView switch
        {
            "hosts" => CsvExport.Hosts(FilteredHosts),
            "config" => CsvExport.Config(FilteredConfig),
            "keys" => CsvExport.Keys(FilteredKeys),
            _ => "time,action,detail\n" + string.Join("\n",
                Activity.Select(a => $"{a.TimeText},{CsvExport.Escape(a.Action)},{CsvExport.Escape(a.Detail)}")),
        };
    }

    public void ClearSelection()
    {
        foreach (var row in _allHosts) row.IsSelected = false;
        SelectedHost = null;
        Raise(nameof(SelectedCount));
        Raise(nameof(HasSelection));
        Raise(nameof(SelectionSummary));
    }

    public void SelectAll()
    {
        if (ActiveView != "hosts") return;
        foreach (var row in FilteredHosts) row.IsSelected = true;
        Raise(nameof(SelectedCount));
        Raise(nameof(HasSelection));
        Raise(nameof(SelectionSummary));
    }

    public void Raise(string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
