using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SshDashboard.Core;

/// <summary>
/// Reads PuTTY private key files (.ppk, v2 and v3) and converts unencrypted
/// RSA/Ed25519 keys to the OpenSSH private-key format so the system ssh can use
/// them. Encrypted PPKs are reported but not decrypted.
/// </summary>
public sealed class PpkFile
{
    public required string Path { get; init; }
    public required int Version { get; init; }
    public required string Algorithm { get; init; }
    public required string Encryption { get; init; }
    public required string Comment { get; init; }
    public required byte[] PublicBlob { get; init; }
    public required byte[] PrivateBlob { get; init; }

    public bool IsEncrypted => !Encryption.Equals("none", StringComparison.OrdinalIgnoreCase);
    public string Kind => Algorithm switch
    {
        "ssh-rsa" => "RSA",
        "ssh-ed25519" => "ED25519",
        "ecdsa-sha2-nistp256" => "ECDSA P-256",
        "ecdsa-sha2-nistp384" => "ECDSA P-384",
        "ecdsa-sha2-nistp521" => "ECDSA P-521",
        _ => Algorithm,
    };
    public string Access => IsEncrypted ? "PPK · passphrase" : "PPK · unencrypted";
    public string Fingerprint => KeyScanner.Fingerprint(Convert.ToBase64String(PublicBlob));

    /// <summary>Removes temporary OpenSSH keys left behind by an earlier run.</summary>
    public static int CleanupTemporaryKeys()
    {
        var directory = System.IO.Path.Combine(PlatformInfo.SupportDir, "keys");
        if (!Directory.Exists(directory)) return 0;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "ppk-*"))
        {
            try { File.Delete(file); removed++; } catch { }
        }
        return removed;
    }

    public static bool LooksLikePpk(string path) =>
        path.EndsWith(".ppk", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    public static PpkFile Load(string path)
    {
        var lines = File.ReadAllLines(path);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        for (; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) { i++; break; }
            if (line.StartsWith("Public-Lines", StringComparison.OrdinalIgnoreCase)) break;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon]] = line[(colon + 1)..].Trim();
        }

        var version = headers.Keys.FirstOrDefault(k =>
            k.StartsWith("PuTTY-User-Key-File-", StringComparison.OrdinalIgnoreCase));
        if (version is null) throw new StoreException("Not a PuTTY key file.");
        var number = int.TryParse(version[^1..], out var v) ? v : 2;

        var publicBlob = ReadBlob(lines, ref i, "Public-Lines");
        var privateBlob = ReadBlob(lines, ref i, "Private-Lines");

        return new PpkFile
        {
            Path = path,
            Version = number,
            Algorithm = headers.GetValueOrDefault(version, ""),
            Encryption = headers.GetValueOrDefault("Encryption", "none"),
            Comment = headers.GetValueOrDefault("Comment", ""),
            PublicBlob = publicBlob,
            PrivateBlob = privateBlob,
        };
    }

    private static byte[] ReadBlob(string[] lines, ref int index, string header)
    {
        while (index < lines.Length && !lines[index].StartsWith(header, StringComparison.OrdinalIgnoreCase))
            index++;
        if (index >= lines.Length) throw new StoreException($"Missing {header} in PuTTY key.");
        var count = int.Parse(lines[index].Split(':')[1].Trim());
        index++;
        var builder = new StringBuilder();
        for (var i = 0; i < count && index < lines.Length; i++, index++) builder.Append(lines[index].Trim());
        return Convert.FromBase64String(builder.ToString());
    }

    public string ToOpenSsh()
    {
        if (IsEncrypted)
            throw new StoreException(
                "This PuTTY key is passphrase-protected. Convert it once with PuTTYgen: " +
                "puttygen key.ppk -O private-openssh -o key_openssh");
        if (Algorithm is not ("ssh-rsa" or "ssh-ed25519"))
            throw new StoreException($"Conversion for {Algorithm} PuTTY keys is not supported yet.");

        var pub = new WireReader(PublicBlob);
        var keyType = Encoding.ASCII.GetString(pub.ReadString());
        var fields = new List<(string, byte[])>();
        if (keyType == "ssh-rsa")
        {
            var e = pub.ReadString();
            var n = pub.ReadString();
            var priv = new WireReader(PrivateBlob);
            var d = priv.ReadString();
            var p = priv.ReadString();
            var q = priv.ReadString();
            var iqmp = priv.ReadString();
            fields.Add(("n", n));
            fields.Add(("e", e));
            fields.Add(("d", d));
            fields.Add(("iqmp", iqmp));
            fields.Add(("p", p));
            fields.Add(("q", q));
        }
        else
        {
            var publicKey = pub.ReadString();
            var priv = new WireReader(PrivateBlob);
            var privateKey = priv.ReadString();
            // PuTTY stores only the 32-byte Ed25519 seed; OpenSSH wants seed+public.
            if (privateKey.Length == 32 && publicKey.Length == 32)
                privateKey = privateKey.Concat(publicKey).ToArray();
            fields.Add(("pub", publicKey));
            fields.Add(("priv", privateKey));
        }

        using var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes("openssh-key-v1\0"));
        WriteString(output, Encoding.ASCII.GetBytes("none"));
        WriteString(output, Encoding.ASCII.GetBytes("none"));
        WriteUInt(output, 0);
        WriteUInt(output, 1);
        WriteString(output, PublicBlob);

        using var privateBlock = new MemoryStream();
        var check = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
        WriteUInt(privateBlock, check);
        WriteUInt(privateBlock, check);
        WriteString(privateBlock, Encoding.ASCII.GetBytes(keyType));
        foreach (var (_, value) in fields) WriteString(privateBlock, value);
        WriteString(privateBlock, Encoding.UTF8.GetBytes(Comment));
        for (byte pad = 1; privateBlock.Length % 8 != 0; pad++) privateBlock.WriteByte(pad);
        WriteString(output, privateBlock.ToArray());

        var base64 = Convert.ToBase64String(output.ToArray());
        var text = new StringBuilder("-----BEGIN OPENSSH PRIVATE KEY-----\n");
        for (var i = 0; i < base64.Length; i += 70)
            text.Append(base64, i, Math.Min(70, base64.Length - i)).Append('\n');
        text.Append("-----END OPENSSH PRIVATE KEY-----\n");
        return text.ToString();
    }

    /// <summary>Converts an encrypted PPK through PuTTYgen, when it is installed.</summary>
    public string ConvertWithPuttygen(string passphrase)
    {
        var tool = FindPuttygen() ?? throw new InvalidOperationException(
            "puttygen was not found. Install PuTTY or convert the key manually.");
        var directory = System.IO.Path.Combine(PlatformInfo.SupportDir, "keys");
        Directory.CreateDirectory(directory);
        var output = System.IO.Path.Combine(directory, "ppk-" + Guid.NewGuid().ToString("N") + ".key");
        var passFile = System.IO.Path.Combine(directory, "pass-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(passFile, passphrase);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(passFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var psi = new System.Diagnostics.ProcessStartInfo(tool)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(Path);
            psi.ArgumentList.Add("-O");
            psi.ArgumentList.Add("private-openssh");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(output);
            psi.ArgumentList.Add("--old-passphrase");
            psi.ArgumentList.Add(passFile);
            using var process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start puttygen.");
            if (!process.WaitForExit(20000))
            {
                try { process.Kill(true); } catch { }
                throw new InvalidOperationException("puttygen timed out.");
            }
            var error = process.StandardError.ReadToEnd();
            if (process.ExitCode != 0 || !File.Exists(output))
                throw new InvalidOperationException("puttygen failed: " +
                    (string.IsNullOrWhiteSpace(error) ? "wrong passphrase or unsupported key" : error.Trim()));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return output;
        }
        catch
        {
            try { File.Delete(output); } catch { }
            throw;
        }
        finally
        {
            try { File.Delete(passFile); } catch { }
        }
    }

    private static string? FindPuttygen()
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[]
            {
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "PuTTY", "puttygen.exe"),
                "puttygen.exe",
            }
            : new[] { "/opt/homebrew/bin/puttygen", "/usr/local/bin/puttygen", "/usr/bin/puttygen" };
        foreach (var candidate in candidates)
        {
            if (System.IO.Path.IsPathRooted(candidate) ? File.Exists(candidate) : OnPath(candidate))
                return candidate;
        }
        return null;
    }

    private static bool OnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(System.IO.Path.PathSeparator)
            .Any(dir => File.Exists(System.IO.Path.Combine(dir, name)));
    }

    public string WriteTemporaryOpenSsh()
    {
        var directory = System.IO.Path.Combine(PlatformInfo.SupportDir, "keys");
        Directory.CreateDirectory(directory);
        foreach (var stale in Directory.EnumerateFiles(directory, "ppk-*"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddHours(-1)) File.Delete(stale);
            }
            catch { }
        }
        var target = System.IO.Path.Combine(directory, "ppk-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(target, ToOpenSsh(), new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return target;
    }

    private static void WriteUInt(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteString(Stream stream, byte[] value)
    {
        WriteUInt(stream, (uint)value.Length);
        stream.Write(value);
    }

    private sealed class WireReader
    {
        private readonly byte[] _data;
        private int _offset;

        public WireReader(byte[] data) => _data = data;

        public byte[] ReadString()
        {
            if (_offset + 4 > _data.Length) throw new StoreException("Truncated PuTTY key data.");
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_offset, 4));
            _offset += 4;
            if (_offset + length > _data.Length) throw new StoreException("Truncated PuTTY key data.");
            var result = _data.AsSpan(_offset, length).ToArray();
            _offset += length;
            return result;
        }
    }
}
