using SshDashboard.App.Views;
using SshDashboard.Core;

namespace SshDashboard.App.Tests;

public class SshLauncherTests
{
    private static HostRow Host(bool inConfig = false, string label = "", string address = "192.0.2.50",
        string user = "", int port = 22) => new()
    {
        InConfig = inConfig,
        Label = label,
        Address = address,
        User = user,
        Port = port,
    };

    [Fact]
    public void ConfigAliasIsPassedAsOneArgument()
    {
        var host = Host(inConfig: true, label: "lab-server", user: "root", port: 2222);

        Assert.Equal(new[] { "lab-server" }, SshLauncher.BuildArguments(host));
        Assert.Equal("lab-server", SshLauncher.TransferTarget(host));
    }

    [Fact]
    public void NonAliasTargetUsesPortUserAndBracketedIpv6()
    {
        var host = Host(address: "2001:db8::10", user: "root", port: 2222);

        Assert.Equal("root@[2001:db8::10]", SshLauncher.TransferTarget(host));

        var args = SshLauncher.BuildSshCommandArguments(host, "pwd");
        Assert.Contains("-p", args);
        Assert.Contains("2222", args);
        Assert.Contains("root@[2001:db8::10]", args);
        Assert.Equal("pwd", args[^1]);
    }

    [Fact]
    public void PasswordRetryDisablesBatchModeAndLimitsPrompts()
    {
        var host = Host();

        var args = SshLauncher.BuildSshCommandArguments(host, "pwd", batchMode: false);
        Assert.Contains("BatchMode=no", args);
        Assert.Contains("NumberOfPasswordPrompts=1", args);
        Assert.DoesNotContain("BatchMode=yes", args);
    }

    [Fact]
    public void ExplicitIdentityIsUsedForTransfers()
    {
        var identity = Path.Combine(Path.GetTempPath(), "sshdash-test-key-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(identity, "test");
        try
        {
            var host = Host(user: "deploy");
            var scp = SshLauncher.BuildScpArguments(host, identityOverride: identity);
            Assert.Equal(identity, scp[scp.IndexOf("-i") + 1]);

            var rsync = SshLauncher.BuildRsyncArguments(host, identityOverride: identity);
            var shell = rsync[rsync.IndexOf("-e") + 1];
            Assert.Contains("-i", shell);
            Assert.Contains(identity, shell);

            var alias = Host(inConfig: true, label: "lab");
            var aliasScp = SshLauncher.BuildScpArguments(alias, identityOverride: identity);
            Assert.Equal(identity, aliasScp[aliasScp.IndexOf("-i") + 1]);
        }
        finally
        {
            File.Delete(identity);
        }
    }

    [Fact]
    public void DownloadArgumentsKeepEveryRemoteSource()
    {
        var host = Host(user: "root", address: "192.0.2.50");
        var target = SshLauncher.TransferTarget(host);
        var args = SshLauncher.BuildDownloadArguments(host, useRsync: false,
            new[] { "~/backups", "/var/log/app log" }, "/tmp/downloads",
            allowPassword: true);

        Assert.Contains($"{target}:~/backups", args);
        Assert.Contains($"{target}:/var/log/app\\ log", args);
        Assert.Equal("/tmp/downloads", args[^1]);
        Assert.Contains("BatchMode=no", args);
    }

    [Theory]
    [InlineData("a b$c", @"a\ b\$c")]
    [InlineData("folder/name", "folder/name")]
    [InlineData("~/incoming", "~/incoming")]
    public void RemotePathEscapingPreservesSafeCharacters(string input, string expected) =>
        Assert.Equal(expected, SshLauncher.EscapeRemotePath(input));

    [Fact]
    public void PasswordFailuresAreNotConfusedWithMissingFiles()
    {
        Assert.True(SshLauncher.IsPasswordFailure(255,
            "root@example.test: Permission denied (publickey,password)."));
        Assert.True(SshLauncher.IsPasswordFailure(1, "Permission denied, please try again."));
        Assert.False(SshLauncher.IsPasswordFailure(1, "No such file or directory"));
        Assert.False(SshLauncher.IsPasswordFailure(0, "Permission denied"));
    }
}

public sealed class SshAskPassTests
{
    [Fact]
    public void HelperIsPrivateAndRemovedOnDispose()
    {
        var askpass = SshAskPass.Create("not-a-real-password");
        var script = askpass.Environment["SSH_ASKPASS"];
        var password = askpass.Environment["SSH_DASHBOARD_PASSWORD_FILE"];
        var directory = Path.GetDirectoryName(script)!;

        Assert.True(File.Exists(script));
        Assert.True(File.Exists(password));
        Assert.Equal("force", askpass.Environment["SSH_ASKPASS_REQUIRE"]);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(script));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(password));
        }

        askpass.Dispose();
        Assert.False(Directory.Exists(directory));
    }
}
