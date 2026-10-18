using SshDashboard.Core;
using Xunit;

namespace SshDashboard.Core.Tests;

public class ParserTests : IDisposable
{
    private readonly string _root;
    private readonly string _ssh;
    private readonly SshStore _store;

    public ParserTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sshdash-tests-" + Guid.NewGuid().ToString("N"));
        _ssh = Path.Combine(_root, ".ssh");
        Directory.CreateDirectory(_ssh);
        _store = new SshStore(_ssh, new ActivityLog(Path.Combine(_root, "activity.jsonl")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(_ssh, name), content);

    [Fact]
    public void SharedKnownHostsLineIsOneEntry()
    {
        Write("known_hosts", "alpha,beta ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAAA\n");
        var lines = KnownHostsParser.ParseFile(Path.Combine(_ssh, "known_hosts"));
        Assert.Single(lines);
        Assert.Equal(new[] { "alpha", "beta" }, lines[0].Tokens);
    }

    [Fact]
    public void DeleteRemovesWholeSharedLineAndWritesBackup()
    {
        var path = Path.Combine(_ssh, "known_hosts");
        Write("known_hosts", "alpha,beta ssh-ed25519 AAAAB\nkeep ssh-rsa AAAAC\n");
        var tokens = new HashSet<string> { "beta" };
        var removed = KnownHostsParser.RemoveLines(path, tokens);
        Assert.Equal(1, removed);
        Assert.DoesNotContain("alpha", File.ReadAllText(path));
        Assert.Contains("keep", File.ReadAllText(path));
    }

    [Fact]
    public void PortAndHashedHostsParse()
    {
        var lines = KnownHostsParser.Parse("[example.com]:2222 ssh-ed25519 AAAA\n|1|abc=|def= ssh-rsa BBBB\n");
        Assert.Equal(2222, lines[0].Port);
        Assert.Equal("example.com", lines[0].Address);
        Assert.True(lines[1].IsHashed);
        Assert.Equal("hashed host", lines[1].DisplayHost);
    }

    [Fact]
    public void MergeJoinsKnownHostsAndConfig()
    {
        Write("known_hosts", "192.0.2.10 ssh-ed25519 AAAA\n192.0.2.10 ssh-rsa BBBB\n");
        Write("config", "Host nas\n    HostName 192.0.2.10\n    User root\n");
        var snapshot = _store.Load();
        var row = Assert.Single(snapshot.Hosts);
        Assert.Equal("nas", row.Label);
        Assert.Equal("192.0.2.10", row.Address);
        Assert.Equal("root", row.User);
        Assert.Equal(22, row.Port);
        Assert.True(row.InKnownHosts && row.InConfig);
        Assert.Single(row.KnownTokens);
    }

    [Fact]
    public void DeleteHostRemovesLineAndBlockWithBackup()
    {
        Write("known_hosts", "192.0.2.10 ssh-ed25519 AAAA\n192.0.2.11 ssh-ed25519 CCCC\n");
        Write("config", "Host nas\n    HostName 192.0.2.10\n    User root\n\nHost other\n    HostName 192.0.2.11\n");
        var snapshot = _store.Load();
        var row = snapshot.Hosts.Single(h => h.Address == "192.0.2.10");

        var result = _store.DeleteHosts(new[] { row }, removeConfig: true);

        Assert.Equal(1, result.KnownHostLines);
        Assert.Equal(1, result.ConfigBlocks);
        Assert.Equal(2, result.Backups.Count);
        Assert.All(result.Backups, b => Assert.True(File.Exists(b)));
        Assert.Empty(Directory.GetFiles(_ssh, "*.tmp"));
        var known = File.ReadAllText(Path.Combine(_ssh, "known_hosts"));
        Assert.DoesNotContain("192.0.2.10", known);
        Assert.Contains("192.0.2.11", known);
        var config = File.ReadAllText(Path.Combine(_ssh, "config"));
        Assert.DoesNotContain("Host nas", config);
        Assert.Contains("Host other", config);
    }

    [Fact]
    public void UpdateBlockPreservesCommentsAndAddsOptions()
    {
        Write("config", "Host nas\n    # keep me\n    HostName 192.0.2.10\n    ForwardAgent yes\n");
        var block = ConfigParser.ParseFile(Path.Combine(_ssh, "config")).Blocks.Single();
        _store.UpdateBlock(block, new[] { "nas", "nas-alt" }, new Dictionary<string, string>
        {
            ["hostname"] = "192.0.2.20",
            ["user"] = "admin",
            ["port"] = "2200",
        });

        var text = File.ReadAllText(Path.Combine(_ssh, "config"));
        Assert.Contains("Host nas nas-alt", text);
        Assert.Contains("# keep me", text);
        Assert.Contains("ForwardAgent yes", text);
        Assert.Contains("HostName 192.0.2.20", text);
        Assert.Contains("User admin", text);
        Assert.Contains("Port 2200", text);
        Assert.Single(_store.ListBackups());
    }

    [Fact]
    public void BackupRestoresPreviousContent()
    {
        Write("known_hosts", "alpha ssh-ed25519 AAAA\n");
        var backup = _store.Backup(Path.Combine(_ssh, "known_hosts"));
        File.WriteAllText(Path.Combine(_ssh, "known_hosts"), "changed\n");
        _store.RestoreBackup(backup);
        Assert.Contains("alpha", File.ReadAllText(Path.Combine(_ssh, "known_hosts")));
    }

    [Fact]
    public void CsvEscapesCommasAndQuotes()
    {
        var csv = CsvExport.Hosts(new[]
        {
            new HostRow { Label = "a,b", Address = "192.0.2.1", User = "ro\"ot", Port = 22 },
        });
        Assert.Contains("\"a,b\"", csv);
        Assert.Contains("\"ro\"\"ot\"", csv);
    }
}
