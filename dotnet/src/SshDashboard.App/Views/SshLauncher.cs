using SshDashboard.Core;

namespace SshDashboard.App.Views;

/// <summary>
/// Builds the ssh command and opens an in-app session window. Using the
/// ssh_config alias keeps ProxyJump, IdentityFile, ports and agent settings.
/// </summary>
public static class SshLauncher
{
    public static Avalonia.Controls.Window? LastWindow { get; private set; }

    public static List<string> BuildArguments(HostRow host)
    {
        var args = new List<string>();
        if (host.InConfig && !string.IsNullOrWhiteSpace(host.Label) &&
            host.Label.IndexOfAny(new[] { '*', '?', ' ', '\t' }) < 0)
        {
            args.Add(host.Label);
            return args;
        }
        if (host.Port != 22)
        {
            args.Add("-p");
            args.Add(host.Port.ToString());
        }
        args.Add(string.IsNullOrWhiteSpace(host.User) ? host.Address : $"{host.User}@{host.Address}");
        return args;
    }

    public static string SanitizeName(string name) => string.Concat(
        name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-'));

    public static string? ResolvePpkPath(HostRow host)
    {
        var identity = host.IdentityFile.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(identity))
        {
            if (identity.StartsWith("~/", StringComparison.Ordinal))
                identity = Path.Combine(PlatformInfo.Home, identity[2..]);
            if (!Path.IsPathRooted(identity)) identity = Path.Combine(PlatformInfo.SshDir, identity);
            if (PpkFile.LooksLikePpk(identity)) return identity;
        }
        // Keys imported for this host are copied in as <alias>.ppk.
        var neighbour = Path.Combine(PlatformInfo.SshDir, SanitizeName(host.Label) + ".ppk");
        return PpkFile.LooksLikePpk(neighbour) ? neighbour : null;
    }

    public static string CommandLine(HostRow host) =>
        "ssh " + string.Join(" ", BuildArguments(host).Select(ShellQuote));

    /// <summary>True when the ssh_config alias should be used instead of user@address.</summary>
    private static bool UsesConfigAlias(HostRow host) =>
        host.InConfig && !string.IsNullOrWhiteSpace(host.Label) &&
        host.Label.IndexOfAny(new[] { '*', '?', ' ', '\t' }) < 0;

    /// <summary>Base scp arguments for a host, without the source/destination operands.</summary>
    public static List<string> BuildScpArguments(HostRow host, bool batchMode = true)
    {
        var args = new List<string> { "-r", "-p" };
        AddBatchMode(args, batchMode);
        // OpenSSH 9+ defaults scp to SFTP, which changes how remote paths are
        // parsed. -O keeps the legacy protocol so shell escaping behaves the
        // same on macOS 12 and current macOS. Older scp has no -O and is
        // already legacy.
        if (SupportsLegacyScpFlag()) args.Add("-O");
        if (UsesConfigAlias(host)) return args;
        if (host.Port != 22)
        {
            args.Add("-P");
            args.Add(host.Port.ToString());
        }
        var identity = ResolveIdentityPath(host);
        if (identity is not null)
        {
            args.Add("-i");
            args.Add(identity);
        }
        return args;
    }

    /// <summary>Arguments for a non-interactive remote command over SSH.</summary>
    public static List<string> BuildSshCommandArguments(HostRow host, string command,
        bool batchMode = true)
    {
        var args = new List<string> { "-o", "ConnectTimeout=8" };
        AddBatchMode(args, batchMode);
        if (UsesConfigAlias(host))
        {
            args.Add(host.Label);
        }
        else
        {
            if (host.Port != 22)
            {
                args.Add("-p");
                args.Add(host.Port.ToString());
            }
            var identity = ResolveIdentityPath(host);
            if (identity is not null)
            {
                args.Add("-i");
                args.Add(identity);
            }
            var address = host.Address.Contains(':') && !host.Address.StartsWith('[')
                ? $"[{host.Address}]"
                : host.Address;
            args.Add(string.IsNullOrWhiteSpace(host.User) ? address : $"{host.User}@{address}");
        }
        args.Add(command);
        return args;
    }

    public static bool HasRsync =>
        File.Exists("/usr/bin/rsync") ||
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, "rsync")));

    public static List<string> BuildRsyncArguments(HostRow host, bool batchMode = true)
    {
        var args = new List<string> { "-a", "--progress" };
        args.Add("-e");
        args.Add(BuildRsyncShell(host, batchMode));
        return args;
    }

    private static string BuildRsyncShell(HostRow host, bool batchMode)
    {
        var shell = batchMode
            ? "ssh -o BatchMode=yes"
            : "ssh -o BatchMode=no -o NumberOfPasswordPrompts=1";
        if (!UsesConfigAlias(host))
        {
            if (host.Port != 22) shell += " -p " + host.Port;
            var identity = ResolveIdentityPath(host);
            if (identity is not null) shell += " -i " + ShellQuote(identity);
        }
        return shell;
    }

    public static bool IsPasswordFailure(int exitCode, string? stderr) =>
        exitCode != 0 && !string.IsNullOrWhiteSpace(stderr) &&
        System.Text.RegularExpressions.Regex.IsMatch(stderr,
            @"(?i)(permission denied|password|authentication failed|enter passphrase)");

    private static void AddBatchMode(List<string> args, bool batchMode)
    {
        if (batchMode)
        {
            args.Add("-o");
            args.Add("BatchMode=yes");
        }
        else
        {
            args.Add("-o");
            args.Add("BatchMode=no");
            args.Add("-o");
            args.Add("NumberOfPasswordPrompts=1");
        }
    }

    public static string TransferTarget(HostRow host)
    {
        if (UsesConfigAlias(host)) return host.Label;
        var address = host.Address.Contains(':') && !host.Address.StartsWith('[')
            ? $"[{host.Address}]"
            : host.Address;
        return string.IsNullOrWhiteSpace(host.User) ? address : $"{host.User}@{address}";
    }

    /// <summary>Escape a remote path for the single argument after the colon.</summary>
    public static string EscapeRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "~";
        var builder = new System.Text.StringBuilder();
        foreach (var c in path.Trim())
        {
            if (c == '\\' || c == ' ' || c == '\t' || c == (char)39 || c == '"' || c == '$' || c == '`' || c == '&' || c == ';' || c == '|' || c == '<' || c == '>' || c == '(' || c == ')' || c == '*' || c == '?' || c == '[' || c == ']' || c == '{' || c == '}')
                builder.Append('\\');
            builder.Append(c);
        }
        return builder.ToString();
    }

    private static bool? _legacyScpFlag;

    private static bool SupportsLegacyScpFlag()
    {
        if (_legacyScpFlag is bool cached) return cached;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ssh")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-V");
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return (_legacyScpFlag = false).Value;
            var version = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            process.WaitForExit(1000);
            var match = System.Text.RegularExpressions.Regex.Match(version, @"OpenSSH_(\d+)\.(\d+)");
            var supported = match.Success &&
                (int.Parse(match.Groups[1].Value) > 9 ||
                 (int.Parse(match.Groups[1].Value) == 9));
            return (_legacyScpFlag = supported).Value;
        }
        catch
        {
            return (_legacyScpFlag = false).Value;
        }
    }

    private static string? ResolveIdentityPath(HostRow host)
    {
        var identity = host.IdentityFile.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(identity)) return null;
        if (identity.StartsWith("~/", StringComparison.Ordinal))
            identity = Path.Combine(PlatformInfo.Home, identity[2..]);
        if (!Path.IsPathRooted(identity)) identity = Path.Combine(PlatformInfo.SshDir, identity);
        return File.Exists(identity) && !PpkFile.LooksLikePpk(identity) ? identity : null;
    }

    public static void OpenTerminal(HostRow host)
    {
        var window = new SshSessionWindow(host);
        LastWindow = window;
        var owner = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
        if (owner is not null && owner.IsVisible) window.Show(owner);
        else window.Show();
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";
}
