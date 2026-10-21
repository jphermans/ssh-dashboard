using SshDashboard.Core;
using Xunit;

namespace SshDashboard.Core.Tests;

public class RegressionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sshdash-reg-" + Guid.NewGuid().ToString("N"));

    public RegressionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private SshStore Store() => new(_dir);

    [Fact]
    public void AddHostBlockAppendsWithBackup()
    {
        var config = Path.Combine(_dir, "config");
        File.WriteAllText(config, "# keep me\nHost existing\n  HostName 192.0.2.1\n");

        var store = Store();
        store.AddHostBlock(new[] { "lab", "lab.example.com" }, new Dictionary<string, string>
        {
            ["hostname"] = "192.0.2.50",
            ["user"] = "jean",
            ["port"] = "2222",
            ["identityfile"] = "~/.ssh/id_ed25519",
        });

        var text = File.ReadAllText(config);
        Assert.Contains("# keep me", text);
        Assert.Contains("Host lab lab.example.com", text);
        Assert.Contains("  HostName 192.0.2.50", text);
        Assert.Contains("  Port 2222", text);
        Assert.Single(store.ListBackups());
        var added = store.Load().Blocks.Single(b => b.Patterns.Contains("lab"));
        Assert.True(added.IsEditable);
        Assert.Equal("jean", added.User);
    }

    [Fact]
    public void AddHostBlockRefusesGlobalWildcard()
    {
        var store = Store();
        Assert.Throws<StoreException>(() => store.AddHostBlock(new[] { "*" },
            new Dictionary<string, string>()));
    }

    [Fact]
    public void HashedKnownHostEntryIsListedAndRemovable()
    {
        var line = "|1|c2FsdA==|aGFzaA== ssh-ed25519 AAAAC3Nza";
        File.WriteAllText(Path.Combine(_dir, "known_hosts"), line + "\n");
        var store = Store();
        var snapshot = store.Load();

        var row = Assert.Single(snapshot.Hosts);
        Assert.True(row.IsHashed);
        Assert.Equal("known_hosts", row.Source);
        Assert.Equal(line.Split(' ')[0], Assert.Single(row.KnownTokens));

        store.DeleteHosts(new[] { row }, removeConfig: false);
        Assert.Equal("", File.ReadAllText(Path.Combine(_dir, "known_hosts")));
    }

    [Fact]
    public void IncludedConfigIsParsedReadOnly()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "config.d"));
        File.WriteAllText(Path.Combine(_dir, "config"),
            "Include config.d/*.conf\n\nHost main\n  HostName 192.0.2.1\n");
        File.WriteAllText(Path.Combine(_dir, "config.d", "lab.conf"),
            "Host lab\n  HostName 192.0.2.50\n  User jean\n");

        var snapshot = Store().Load();
        var included = snapshot.Blocks.Single(b => b.Label == "lab");
        Assert.True(included.IsIncluded);
        Assert.False(included.IsEditable);
        var row = snapshot.Hosts.Single(h => h.Address == "192.0.2.50");
        Assert.Equal("lab", row.Label);
        Assert.Equal("jean", row.User);
        Assert.Empty(row.ConfigStarts);
    }

    [Fact]
    public void UndoRestoresEveryFileTouchedByOneAction()
    {
        var known = Path.Combine(_dir, "known_hosts");
        var config = Path.Combine(_dir, "config");
        File.WriteAllText(known, "192.0.2.10 ssh-ed25519 AAAAC3Nza\n");
        File.WriteAllText(config, "Host lab\n  HostName 192.0.2.10\n");

        var store = Store();
        var row = store.Load().Hosts.Single(h => h.Address == "192.0.2.10");
        var result = store.DeleteHosts(new[] { row }, removeConfig: true);
        Assert.Equal(2, result.Backups.Count);
        Assert.DoesNotContain("192.0.2.10", File.ReadAllText(known));
        Assert.DoesNotContain("Host lab", File.ReadAllText(config));

        Assert.Equal(2, store.UndoLast());
        Assert.Contains("192.0.2.10", File.ReadAllText(known));
        Assert.Contains("Host lab", File.ReadAllText(config));
    }

    [Fact]
    public void EncryptedPpkConvertsThroughPuttygenWhenInstalled()
    {
        var tool = new[] { "/opt/homebrew/bin/puttygen", "/usr/local/bin/puttygen", "/usr/bin/puttygen" }
            .FirstOrDefault(File.Exists);
        if (tool is null) return; // optional tool on CI

        var plain = Path.Combine(_dir, "plain.ppk");
        var encrypted = Path.Combine(_dir, "encrypted.ppk");
        var empty = Path.Combine(_dir, "empty.txt");
        var secret = Path.Combine(_dir, "secret.txt");
        File.WriteAllText(empty, "");
        File.WriteAllText(secret, "correct horse");

        static void Run(string tool, params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(tool)
            { RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(psi)!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }

        Run(tool, "-t", "ed25519", "-o", plain, "--new-passphrase", empty);
        Run(tool, plain, "-P", "--old-passphrase", empty,
            "--new-passphrase", secret, "-o", encrypted);

        var ppk = PpkFile.Load(encrypted);
        Assert.True(ppk.IsEncrypted);
        var converted = ppk.ConvertWithPuttygen("correct horse");
        try
        {
            Assert.True(File.Exists(converted));
            Assert.Contains("OPENSSH PRIVATE KEY", File.ReadAllText(converted));
        }
        finally { try { File.Delete(converted); } catch { } }
    }

    [Fact]
    public void RemovingALinePreservesInvalidBytesElsewhere()
    {
        var path = Path.Combine(_dir, "known_hosts");
        var bytes = new List<byte>();
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("# caf"));
        bytes.Add(0xE9); // not valid UTF-8
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(" comment\n192.0.2.20 ssh-ed25519 AAAAC3Nza\n"));
        File.WriteAllBytes(path, bytes.ToArray());

        var removed = KnownHostsParser.RemoveLines(path,
            new HashSet<string> { "192.0.2.20" });

        Assert.Equal(1, removed);
        var after = File.ReadAllBytes(path);
        Assert.Contains((byte)0xE9, after);
        Assert.DoesNotContain("192.0.2.20", System.Text.Encoding.ASCII.GetString(after));
    }
}
