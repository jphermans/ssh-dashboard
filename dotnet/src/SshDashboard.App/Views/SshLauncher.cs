using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using SshDashboard.Core;

namespace SshDashboard.App.Views;

/// <summary>
/// Opens an interactive SSH session in the platform terminal. Using the config
/// alias (when the host comes from ssh_config) keeps ProxyJump, IdentityFile and
/// other options working exactly as they do on the command line.
/// </summary>
public static class SshLauncher
{
    public static string BuildTarget(HostRow host)
    {
        if (host.InConfig && !string.IsNullOrWhiteSpace(host.Label) &&
            host.Label.IndexOfAny(new[] { '*', '?', ' ', '\t' }) < 0)
            return host.Label;
        var target = string.IsNullOrWhiteSpace(host.User) ? host.Address : $"{host.User}@{host.Address}";
        return host.Port != 22 ? target : target;
    }

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

    public static string CommandLine(HostRow host) =>
        "ssh " + string.Join(" ", BuildArguments(host).Select(ShellQuote));

    public static async Task OpenAsync(HostRow host, Window owner, Action<string> toast)
    {
        var args = BuildArguments(host);
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var sessions = Path.Combine(PlatformInfo.SupportDir, "sessions");
                Directory.CreateDirectory(sessions);
                var safe = new string(host.Label.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
                var script = Path.Combine(sessions, $"ssh-{safe}-{DateTime.Now:HHmmss}.command");
                var body = "#!/bin/zsh\n" +
                           "printf '\\e]0;SSH " + EscapeForShellDoubleQuotes(host.Label) + "\\a'\n" +
                           "exec /usr/bin/ssh " + string.Join(" ", args.Select(ShellQuote)) + "\n";
                await File.WriteAllTextAsync(script, body);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(script,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Process.Start(new ProcessStartInfo("open")
                {
                    ArgumentList = { "-a", "Terminal", script },
                    UseShellExecute = false,
                });
                toast("Opened SSH session in Terminal");
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                var quoted = string.Join(" ", args.Select(WindowsQuote));
                if (TryStart("wt.exe", args))
                {
                    toast("Opened SSH session in Windows Terminal");
                    return;
                }
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c start \"SSH {host.Label}\" ssh {quoted}")
                { UseShellExecute = false, CreateNoWindow = true });
                toast("Opened SSH session");
                return;
            }

            var linuxArgs = new[] { "-e", "/usr/bin/ssh" }.Concat(args).ToArray();
            foreach (var terminal in new[]
                     {
                         "x-terminal-emulator", "gnome-terminal", "konsole", "xfce4-terminal", "xterm",
                     })
            {
                try
                {
                    Process.Start(new ProcessStartInfo(terminal, linuxArgs) { UseShellExecute = false });
                    toast($"Opened SSH session in {terminal}");
                    return;
                }
                catch { }
            }
            throw new InvalidOperationException("No terminal emulator was found.");
        }
        catch (Exception ex)
        {
            await Dialogs.ConfirmAsync(owner, "Could not open a terminal",
                "The SSH session could not be started",
                ex.Message,
                "You can run it manually:  " + CommandLine(host),
                "Close", danger: false);
        }
    }

    private static bool TryStart(string file, IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";

    private static string WindowsQuote(string value) =>
        value.Any(char.IsWhiteSpace) ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;

    private static string EscapeForShellDoubleQuotes(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`");
}
