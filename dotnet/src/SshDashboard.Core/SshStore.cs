using System.Text;

namespace SshDashboard.Core;

public sealed record DeleteResult(int KnownHostLines, int ConfigBlocks, IReadOnlyList<string> Backups);

public sealed class SshStore
{
    private readonly ActivityLog _log;

    public SshStore(string? sshDir = null, ActivityLog? log = null)
    {
        SshDir = sshDir ?? PlatformInfo.SshDir;
        _log = log ?? new ActivityLog();
    }

    public string SshDir { get; }
    public string KnownHostsPath => Path.Combine(SshDir, "known_hosts");
    public string ConfigPath => Path.Combine(SshDir, "config");
    public string BackupsDir => Path.Combine(SshDir, ".sshdashboard", "backups");

    /// <summary>Backups created by the most recent mutating action.</summary>
    public IReadOnlyList<string> LastActionBackups { get; private set; } = Array.Empty<string>();

    public Snapshot Load()
    {
        PruneBackups();
        var snapshot = new Snapshot();
        snapshot.KnownHosts.AddRange(KnownHostsParser.ParseFile(KnownHostsPath));
        snapshot.Blocks.AddRange(ConfigParser.ParseFile(ConfigPath, followIncludes: true).Blocks);
        snapshot.Keys.AddRange(KeyScanner.Scan(SshDir));
        snapshot.Hosts.AddRange(MergeHosts(snapshot));
        snapshot.BackupCount = ListBackups().Count;
        return snapshot;
    }

    private static List<HostRow> MergeHosts(Snapshot snapshot)
    {
        var rows = new Dictionary<string, HostRow>(StringComparer.OrdinalIgnoreCase);

        string Key(string address, int port) => $"{address}\u001f{port}";

        foreach (var line in snapshot.KnownHosts)
        {
            if (line.IsMarker) continue;
            if (line.IsHashed)
            {
                var token = line.Tokens.FirstOrDefault();
                if (token is null) continue;
                var hashedKey = "hashed:" + token;
                if (!rows.TryGetValue(hashedKey, out var hashedRow))
                {
                    hashedRow = new HostRow
                    {
                        Label = "hashed · " + (token.Length > 16 ? token[..16] + "…" : token),
                        Address = "hashed host",
                        Port = 22,
                        IsHashed = true,
                        InKnownHosts = true,
                        Source = "known_hosts",
                    };
                    rows[hashedKey] = hashedRow;
                }
                hashedRow.KnownTokens.Add(token);
                hashedRow.KeyTypes = line.KeyType;
                continue;
            }
            foreach (var token in line.Tokens)
            {
                var address = token;
                var port = 22;
                if (token.StartsWith('[') && token.Contains("]:"))
                {
                    var split = token.IndexOf("]:", StringComparison.Ordinal);
                    address = token[1..split];
                    int.TryParse(token[(split + 2)..], out port);
                }
                var key = Key(address, port);
                if (!rows.TryGetValue(key, out var row))
                {
                    row = new HostRow { Label = address, Address = address, Port = port };
                    rows[key] = row;
                }
                row.InKnownHosts = true;
                row.Source = "known_hosts";
                if (!row.KnownTokens.Contains(token)) row.KnownTokens.Add(token);
                if (!string.IsNullOrEmpty(line.KeyType))
                {
                    var types = row.KeyTypes.Split(", ", StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                    types.Add(line.KeyType);
                    row.KeyTypes = string.Join(", ", types);
                }
            }
        }

        foreach (var block in snapshot.Blocks)
        {
            var host = block.HostName;
            if (string.IsNullOrWhiteSpace(host) || block.Patterns.Count == 0) continue;
            var label = block.Patterns[0];
            if (label.IndexOfAny(new[] { '*', '?' }) >= 0) continue;
            var port = int.TryParse(block.PortText, out var parsed) ? parsed : 22;
            var key = Key(host, port);
            if (!rows.TryGetValue(key, out var row))
            {
                row = new HostRow { Label = label, Address = host, Port = port };
                rows[key] = row;
            }
            else if (row.Label == row.Address)
            {
                row.Label = label;
            }
            row.InConfig = true;
            row.Source = row.InKnownHosts ? "both" : "config";
            if (string.IsNullOrWhiteSpace(row.User)) row.User = block.User;
            if (string.IsNullOrWhiteSpace(row.IdentityFile)) row.IdentityFile = block.IdentityFile;
            if (!row.ConfigStarts.Contains(block.StartLine)) row.ConfigStarts.Add(block.StartLine);
        }

        return rows.Values
            .OrderBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Address, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public DeleteResult DeleteHosts(IReadOnlyList<HostRow> rows, bool removeConfig)
    {
        if (rows.Count == 0) return new DeleteResult(0, 0, Array.Empty<string>());

        var tokens = rows.SelectMany(r => r.KnownTokens).ToHashSet(StringComparer.Ordinal);
        var backups = new List<string>();
        var removedLines = 0;

        if (tokens.Count > 0 && File.Exists(KnownHostsPath))
        {
            backups.Add(Backup(KnownHostsPath));
            removedLines = KnownHostsParser.RemoveLines(KnownHostsPath, tokens);
        }

        var blockStarts = removeConfig ? rows.SelectMany(r => r.ConfigStarts).ToHashSet() : new HashSet<int>();
        var removedBlocks = 0;
        if (blockStarts.Count > 0 && File.Exists(ConfigPath))
        {
            backups.Add(Backup(ConfigPath));
            removedBlocks = RemoveConfigBlocks(blockStarts);
        }

        _log.Append("delete-hosts",
            $"{removedLines} known_hosts line(s), {removedBlocks} config block(s)");
        LastActionBackups = backups;
        return new DeleteResult(removedLines, removedBlocks, backups);
    }

    public DeleteResult DeleteConfigBlocks(IEnumerable<int> starts)
    {
        var set = starts.ToHashSet();
        if (set.Count == 0 || !File.Exists(ConfigPath)) return new DeleteResult(0, 0, Array.Empty<string>());
        var backup = Backup(ConfigPath);
        var removed = RemoveConfigBlocks(set);
        _log.Append("delete-config", $"{removed} block(s)");
        LastActionBackups = new[] { backup };
        return new DeleteResult(0, removed, new[] { backup });
    }

    private int RemoveConfigBlocks(ISet<int> starts)
    {
        var text = TextIO.ReadText(ConfigPath);
        var parsed = ConfigParser.Parse(text);
        var remove = new HashSet<int>();
        foreach (var block in parsed.Blocks.Where(b => starts.Contains(b.StartLine)))
            for (var i = block.StartLine; i <= block.EndLine; i++) remove.Add(i);

        var lines = TextIO.SplitLines(text);
        var kept = new List<string>();
        for (var i = 0; i < lines.Count; i++)
            if (!remove.Contains(i)) kept.Add(lines[i]);
        WriteAtomic(ConfigPath, TextIO.JoinLines(kept, TextIO.Newline(text)));
        return parsed.Blocks.Count(b => starts.Contains(b.StartLine));
    }

    public void UpdateBlock(ConfigBlock block, IReadOnlyList<string> patterns,
        IReadOnlyDictionary<string, string> values)
    {
        if (!block.IsEditable) throw new StoreException("This block cannot be edited.");
        if (patterns.Count == 0) throw new StoreException("A Host block needs at least one pattern.");
        if (!File.Exists(ConfigPath)) throw new StoreException("ssh_config was not found.");

        var text = TextIO.ReadText(ConfigPath);
        var newline = TextIO.Newline(text);
        var lines = TextIO.SplitLines(text);
        if (block.EndLine >= lines.Count) throw new StoreException("The file changed; refresh and try again.");

        var output = new List<string>();
        output.AddRange(lines.Take(block.StartLine));
        output.Add("Host " + string.Join(" ", patterns));

        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var edits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
            if (!string.IsNullOrWhiteSpace(pair.Value)) edits[pair.Key] = pair.Value.Trim();

        for (var i = block.StartLine + 1; i <= block.EndLine; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            var indent = line[..(line.Length - line.TrimStart().Length)];
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                output.Add(line);
                continue;
            }
            var split = trimmed.IndexOfAny(new[] { '=', ' ', '\t' });
            var key = split < 0 ? trimmed : trimmed[..split];
            if (edits.TryGetValue(key, out var value))
            {
                output.Add($"{indent}{CanonicalKey(key)} {value}");
                written.Add(key);
            }
            else
            {
                output.Add(line);
            }
        }

        foreach (var pair in edits)
        {
            if (written.Contains(pair.Key)) continue;
            output.Add($"{CanonicalKey(pair.Key)} {pair.Value}");
        }

        output.AddRange(lines.Skip(block.EndLine + 1));
        LastActionBackups = new[] { Backup(ConfigPath) };
        WriteAtomic(ConfigPath, TextIO.JoinLines(output, newline) +
            (text.EndsWith(newline, StringComparison.Ordinal) ? newline : ""));
        _log.Append("edit-config", "Host " + string.Join(" ", patterns));
    }

    private static string CanonicalKey(string key) => key.ToLowerInvariant() switch
    {
        "hostname" => "HostName",
        "user" => "User",
        "port" => "Port",
        "identityfile" => "IdentityFile",
        _ => key,
    };

    public string Backup(string path)
    {
        Directory.CreateDirectory(BackupsDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var target = Path.Combine(BackupsDir, $"{stamp}-{Path.GetFileName(path)}");
        File.Copy(path, target, true);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(target, File.GetUnixFileMode(path)); } catch { }
        }
        return target;
    }

    public List<string> ListBackups()
    {
        if (!Directory.Exists(BackupsDir)) return new List<string>();
        return Directory.EnumerateFiles(BackupsDir)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    /// <summary>Restores every file touched by the last action, newest first.</summary>
    public int UndoLast()
    {
        var backups = LastActionBackups;
        LastActionBackups = Array.Empty<string>();
        var restored = 0;
        foreach (var backup in backups.Reverse())
        {
            try
            {
                RestoreBackup(backup);
                restored++;
            }
            catch { }
        }
        if (restored > 0) _log.Append("undo", $"{restored} file(s)");
        return restored;
    }

    /// <summary>Keeps the backup folder bounded: 30 days and at most 200 files.</summary>
    public void PruneBackups()
    {
        try
        {
            if (!Directory.Exists(BackupsDir)) return;
            var files = new DirectoryInfo(BackupsDir).GetFiles()
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            var cutoff = DateTime.UtcNow.AddDays(-30);
            for (var i = 0; i < files.Count; i++)
            {
                if (i < 200 && files[i].LastWriteTimeUtc >= cutoff) continue;
                try { files[i].Delete(); } catch { }
            }
        }
        catch { }
    }

    public void RestoreBackup(string backupPath)
    {
        var name = Path.GetFileName(backupPath);
        var dash = name.IndexOf('-', 16);
        var original = dash >= 0 && dash + 1 < name.Length ? name[(dash + 1)..] : "";
        if (original is not ("known_hosts" or "config"))
            throw new StoreException("Unrecognised backup name.");
        var target = Path.Combine(SshDir, original);
        var text = File.ReadAllText(backupPath);
        WriteAtomic(target, text);
        _log.Append("restore", name);
    }

    private static void WriteAtomic(string path, string text)
    {
        var mode = OperatingSystem.IsWindows() ? (UnixFileMode?)null : File.GetUnixFileMode(path);
        var temp = path + ".sshdash-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (mode.HasValue) File.SetUnixFileMode(temp, mode.Value);
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp))
                try { File.Delete(temp); } catch { }
        }
    }
}

public static class CsvExport
{
    public static string Escape(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    public static string Hosts(IEnumerable<HostRow> rows)
    {
        var builder = new StringBuilder("host,address,user,port,keys,source\n");
        foreach (var row in rows)
            builder.AppendLine(string.Join(",", new[]
            {
                Escape(row.Label), Escape(row.Address), Escape(row.User),
                row.Port.ToString(), Escape(row.KeyTypes), Escape(row.Source),
            }));
        return builder.ToString();
    }

    public static string Config(IEnumerable<ConfigBlock> blocks)
    {
        var builder = new StringBuilder("block,hostname,user,port,identityfile,lines,state\n");
        foreach (var block in blocks)
            builder.AppendLine(string.Join(",", new[]
            {
                Escape(block.Label), Escape(block.HostName), Escape(block.User),
                Escape(block.PortText), Escape(block.IdentityFile),
                block.LineCount.ToString(), Escape(block.State),
            }));
        return builder.ToString();
    }

    public static string Keys(IEnumerable<KeyInfo> keys)
    {
        var builder = new StringBuilder("name,type,fingerprint,size,access,path\n");
        foreach (var key in keys)
            builder.AppendLine(string.Join(",", new[]
            {
                Escape(key.Name), Escape(key.Kind), Escape(key.Fingerprint),
                Escape(key.DisplaySize), Escape(key.Access), Escape(key.Path),
            }));
        return builder.ToString();
    }
}
