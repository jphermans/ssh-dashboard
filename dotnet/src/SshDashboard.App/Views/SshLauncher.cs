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

    public static string? ResolvePpkPath(HostRow host)
    {
        var identity = host.IdentityFile.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(identity)) return null;
        if (identity.StartsWith("~/", StringComparison.Ordinal))
            identity = Path.Combine(PlatformInfo.Home, identity[2..]);
        if (!Path.IsPathRooted(identity)) identity = Path.Combine(PlatformInfo.SshDir, identity);
        return PpkFile.LooksLikePpk(identity) ? identity : null;
    }

    public static string CommandLine(HostRow host) =>
        "ssh " + string.Join(" ", BuildArguments(host).Select(ShellQuote));

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
