using System.Text;

namespace SshDashboard.Core;

public static class TextIO
{
    public static string ReadText(string path) =>
        File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;

    public static string Newline(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    public static List<string> SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        return lines.Count == 1 && lines[0].Length == 0 ? new List<string>() : lines;
    }

    public static string JoinLines(IEnumerable<string> lines, string newline) =>
        string.Join(newline, lines);
}

public sealed class KnownHostLine
{
    public int Index { get; init; }
    public string Raw { get; init; } = "";
    public string HostField { get; init; } = "";
    public IReadOnlyList<string> Tokens { get; init; } = Array.Empty<string>();
    public string KeyType { get; init; } = "";
    public string KeyData { get; init; } = "";
    public string Comment { get; init; } = "";
    public bool IsMarker { get; init; }
    public bool IsHashed { get; init; }

    public string DisplayHost =>
        IsHashed ? "hashed host" : (Tokens.Count > 0 ? Tokens[0] : "unknown");

    public string Label =>
        IsMarker ? $"@{KeyType}" : Tokens.Count > 1
            ? $"{Tokens[0]} +{Tokens.Count - 1}"
            : DisplayHost;

    public int? Port
    {
        get
        {
            var host = Tokens.FirstOrDefault();
            if (host is null) return null;
            if (host.StartsWith('[') && host.Contains("]:"))
            {
                var after = host[(host.IndexOf("]:", StringComparison.Ordinal) + 2)..];
                return int.TryParse(after, out var port) ? port : null;
            }
            return null;
        }
    }

    public string Address
    {
        get
        {
            var host = Tokens.FirstOrDefault();
            if (host is null) return "unknown";
            if (host.StartsWith('[') && host.Contains("]:"))
                return host[1..host.IndexOf("]:", StringComparison.Ordinal)];
            return host;
        }
    }
}

public sealed record ConfigOption(string Key, string Value, int LineIndex);

public sealed class ConfigBlock
{
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string Header { get; init; } = "";
    public IReadOnlyList<string> Patterns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ConfigOption> Options { get; init; } = Array.Empty<ConfigOption>();
    public bool IsMatch { get; init; }
    public bool IsGlobal { get; init; }

    public string Label => Patterns.Count == 0 ? Header : string.Join(" ", Patterns);
    public bool IsEditable => !IsMatch && !IsGlobal && Patterns.Count > 0;
    public string State => IsMatch ? "Match" : IsGlobal ? "Default" : "Editable";

    public string? Option(string key) =>
        Options.FirstOrDefault(o => o.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;

    public string HostName => Option("hostname") ?? "";
    public string User => Option("user") ?? "";
    public string PortText => Option("port") ?? "";
    public string IdentityFile => Option("identityfile") ?? "";
    public int LineCount => EndLine - StartLine + 1;
}

public sealed record KeyInfo(
    string Path,
    string Name,
    string Kind,
    string Fingerprint,
    long Size,
    bool IsPrivate,
    string Permissions)
{
    public string DisplaySize => Size < 1024 ? $"{Size} B" : $"{Size / 1024.0:0.#} KB";
    public string Access => IsPrivate ? (Permissions == "600" ? "Private · 600" : $"Private · {Permissions}") : "Public";
}

public sealed class HostRow : System.ComponentModel.INotifyPropertyChanged
{
    private bool _selected;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string Label { get; set; } = "";
    public string Address { get; set; } = "";
    public string User { get; set; } = "";
    public int Port { get; set; } = 22;
    public string KeyTypes { get; set; } = "";
    public string Source { get; set; } = "";
    public bool InKnownHosts { get; set; }
    public bool InConfig { get; set; }
    public List<string> KnownTokens { get; } = new();
    public List<int> ConfigStarts { get; } = new();
    public string PortText => Port.ToString();
    public string Detail =>
        $"{(InKnownHosts ? "known_hosts" : "")}{(InConfig ? (InKnownHosts ? " · ssh_config" : "ssh_config") : "")}";
}

public sealed class ActivityEntry
{
    public DateTime When { get; init; } = DateTime.Now;
    public string Action { get; init; } = "";
    public string Detail { get; init; } = "";
    public string TimeText => When.ToString("MMM d, HH:mm");
}

public sealed class Snapshot
{
    public List<KnownHostLine> KnownHosts { get; } = new();
    public List<ConfigBlock> Blocks { get; } = new();
    public List<KeyInfo> Keys { get; } = new();
    public List<HostRow> Hosts { get; } = new();
    public int BackupCount { get; set; }
}

public sealed class StoreException : Exception
{
    public StoreException(string message) : base(message) { }
    public StoreException(string message, Exception inner) : base(message, inner) { }
}
