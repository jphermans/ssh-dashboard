using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SshDashboard.Core;

public static class PlatformInfo
{
    public static bool IsMac => OperatingSystem.IsMacOS();
    public static bool IsWindows => OperatingSystem.IsWindows();

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string SshDir => Path.Combine(Home, ".ssh");

    public static string SupportDir
    {
        get
        {
            if (IsMac)
                return Path.Combine(Home, "Library", "Application Support", "SshDashboard");
            if (IsWindows)
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SshDashboard");
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            return Path.Combine(string.IsNullOrWhiteSpace(xdg) ? Path.Combine(Home, ".config") : xdg, "sshdashboard");
        }
    }

    public static string SettingsPath => Path.Combine(SupportDir, "settings.json");
    public static string ActivityPath => Path.Combine(SupportDir, "activity.jsonl");
    public static string ErrorLogPath => Path.Combine(SupportDir, "error.log");

    public static string SystemTheme()
    {
        try
        {
            if (IsMac)
            {
                var psi = new ProcessStartInfo("/usr/bin/defaults", "read -g AppleInterfaceStyle")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using var process = Process.Start(psi);
                if (process is null) return "light";
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(2000);
                return output.Equals("Dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light";
            }
            if (IsWindows)
            {
                var psi = new ProcessStartInfo("reg")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("query");
                psi.ArgumentList.Add(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                psi.ArgumentList.Add("/v");
                psi.ArgumentList.Add("AppsUseLightTheme");
                using var process = Process.Start(psi);
                if (process is null) return "light";
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(2000);
                return output.Contains("0x0", StringComparison.OrdinalIgnoreCase) ? "dark" : "light";
            }
        }
        catch
        {
            return "light";
        }
        return "light";
    }

    public static string ResolveTheme(string preference) => preference switch
    {
        "dark" => "dark",
        "light" => "light",
        _ => SystemTheme(),
    };

    public static async Task<(bool Reachable, string Error)> ProbeAsync(
        string host, int port, int timeoutMs = 3000)
    {
        if (IsMac && File.Exists("/usr/bin/nc"))
        {
            try
            {
                var psi = new ProcessStartInfo("/usr/bin/nc")
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("-z");
                psi.ArgumentList.Add("-G");
                psi.ArgumentList.Add(Math.Max(1, timeoutMs / 1000).ToString());
                psi.ArgumentList.Add(host);
                psi.ArgumentList.Add(port.ToString());
                using var process = Process.Start(psi);
                if (process is null) return (false, "could not start nc");
                using var cts = new CancellationTokenSource(timeoutMs + 2000);
                try { await process.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException)
                {
                    try { process.Kill(true); } catch { }
                    return (false, "connection timed out");
                }
                if (process.ExitCode == 0) return (true, "");
                var error = (await process.StandardError.ReadToEndAsync()).Trim();
                return (false, string.IsNullOrEmpty(error) ? "connection refused or timed out" : error);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(host, port, cts.Token);
            return (true, "");
        }
        catch (OperationCanceledException)
        {
            return (false, "connection timed out");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static void Reveal(string path)
    {
        try
        {
            var targetExists = File.Exists(path) || Directory.Exists(path);
            var folder = targetExists ? path : Path.GetDirectoryName(path) ?? path;
            if (IsMac)
            {
                var args = File.Exists(path) ? new[] { "-R", path } : new[] { folder };
                Process.Start("open", args);
            }
            else if (IsWindows)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{folder}\"")
                { UseShellExecute = true });
            }
            else
            {
                Process.Start("xdg-open", folder);
            }
        }
        catch
        {
            // best effort
        }
    }
}

public static class KeyScanner
{
    private static readonly string[] Skip =
        { "known_hosts", "known_hosts.old", "config", "authorized_keys", "environment", "rc" };

    public static List<KeyInfo> Scan(string sshDir)
    {
        var keys = new List<KeyInfo>();
        if (!Directory.Exists(sshDir)) return keys;
        foreach (var path in Directory.EnumerateFiles(sshDir))
        {
            var name = Path.GetFileName(path);
            if (Skip.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (name.EndsWith(".ppk", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var ppk = PpkFile.Load(path);
                    keys.Add(new KeyInfo(path, name, $"PuTTY PPK · {ppk.Kind}", ppk.Fingerprint,
                        new FileInfo(path).Length, true, ppk.Access));
                }
                catch { }
                continue;
            }
            if (name.StartsWith('.') || name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;

            string first;
            try
            {
                using var reader = new StreamReader(path);
                first = reader.ReadLine() ?? "";
            }
            catch
            {
                continue;
            }

            var isPublic = name.EndsWith(".pub", StringComparison.OrdinalIgnoreCase);
            var isPrivate = first.Contains("PRIVATE KEY", StringComparison.Ordinal);
            if (!isPublic && !isPrivate) continue;

            var kind = DetectKind(first, isPrivate);
            var fingerprint = "";
            if (isPublic)
            {
                try
                {
                    var parts = File.ReadAllText(path).Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2) fingerprint = Fingerprint(parts[1]);
                }
                catch { }
            }

            var size = new FileInfo(path).Length;
            var permissions = "";
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    var mode = File.GetUnixFileMode(path);
                    permissions = Convert.ToString((int)mode & 0x1FF, 8).PadLeft(3, '0');
                }
                catch { }
            }

            keys.Add(new KeyInfo(path, name, kind, fingerprint, size, isPrivate, permissions));
        }
        return keys.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string DetectKind(string first, bool isPrivate)
    {
        if (isPrivate)
        {
            if (first.Contains("OPENSSH PRIVATE KEY")) return "OpenSSH private";
            if (first.Contains("RSA PRIVATE KEY")) return "RSA private";
            if (first.Contains("EC PRIVATE KEY")) return "ECDSA private";
            if (first.Contains("DSA PRIVATE KEY")) return "DSA private";
            if (first.Contains("PRIVATE KEY")) return "PKCS#8 private";
        }
        var text = first.Trim();
        if (text.StartsWith("ssh-ed25519")) return "ED25519";
        if (text.StartsWith("ssh-rsa")) return "RSA";
        if (text.StartsWith("ecdsa-sha2")) return "ECDSA";
        if (text.StartsWith("sk-ssh")) return "FIDO";
        return "public key";
    }

    public static string Fingerprint(string base64Key)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64Key);
            var hash = System.Security.Cryptography.SHA256.HashData(bytes);
            return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
        }
        catch
        {
            return "";
        }
    }
}

public sealed class Settings
{
    public string Theme { get; set; } = "system";
    public string LastView { get; set; } = "hosts";
    public bool ConfirmDelete { get; set; } = true;
    public bool RemoveConfigDefault { get; set; } = true;

    public static Settings Load()
    {
        try
        {
            if (!File.Exists(PlatformInfo.SettingsPath)) return new Settings();
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(PlatformInfo.SettingsPath));
            if (settings is null) return new Settings();
            if (settings.Theme is not ("system" or "dark" or "light")) settings.Theme = "system";
            return settings;
        }
        catch
        {
            return new Settings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(PlatformInfo.SupportDir);
            File.WriteAllText(PlatformInfo.SettingsPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // preferences are best effort
        }
    }
}

public sealed class ActivityLog
{
    private readonly string _path;

    public ActivityLog(string? path = null) => _path = path ?? PlatformInfo.ActivityPath;

    public void Append(string action, string detail)
    {
        try
        {
            Directory.CreateDirectory(PlatformInfo.SupportDir);
            var entry = new ActivityEntry { Action = action, Detail = detail };
            var json = JsonSerializer.Serialize(new { when = entry.When, action, detail });
            File.AppendAllText(_path, json + "\n", Encoding.UTF8);
        }
        catch { }
    }

    public List<ActivityEntry> Recent(int max = 250)
    {
        var entries = new List<ActivityEntry>();
        try
        {
            if (!File.Exists(_path)) return entries;
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    entries.Add(new ActivityEntry
                    {
                        When = root.TryGetProperty("when", out var when) && when.TryGetDateTime(out var dt) ? dt : DateTime.Now,
                        Action = root.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "",
                        Detail = root.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "",
                    });
                }
                catch { }
            }
        }
        catch { }
        return entries.TakeLast(max).Reverse().ToList();
    }
}
