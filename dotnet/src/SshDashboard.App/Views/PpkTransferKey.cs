using Avalonia.Controls;
using SshDashboard.Core;

namespace SshDashboard.App.Views;

/// <summary>
/// Converts a PuTTY key to a temporary OpenSSH key for a transfer. The
/// temporary key is deleted as soon as the operation finishes.
/// </summary>
internal sealed class PreparedPpkKey : IDisposable
{
    public string? KeyPath { get; }
    public string? Error { get; }
    public bool Cancelled { get; }

    public PreparedPpkKey(string? keyPath = null, string? error = null, bool cancelled = false)
    {
        KeyPath = keyPath;
        Error = error;
        Cancelled = cancelled;
    }

    public void Dispose()
    {
        if (string.IsNullOrWhiteSpace(KeyPath)) return;
        try { File.Delete(KeyPath); } catch { }
    }
}

internal static class PpkTransferKey
{
    public static async Task<PreparedPpkKey> PrepareAsync(HostRow host, Window owner)
    {
        var ppkPath = SshLauncher.ResolvePpkPath(host);
        if (ppkPath is null) return new PreparedPpkKey();

        try
        {
            var ppk = PpkFile.Load(ppkPath);
            string key;
            if (ppk.IsEncrypted)
            {
                var passphrase = await new PassphraseWindow(Path.GetFileName(ppkPath))
                    .ShowDialog<string?>(owner);
                if (string.IsNullOrEmpty(passphrase))
                    return new PreparedPpkKey(cancelled: true);
                key = ppk.ConvertWithPuttygen(passphrase);
            }
            else
            {
                key = ppk.WriteTemporaryOpenSsh();
            }
            return new PreparedPpkKey(key);
        }
        catch (Exception ex)
        {
            return new PreparedPpkKey(error: ex.Message);
        }
    }
}
