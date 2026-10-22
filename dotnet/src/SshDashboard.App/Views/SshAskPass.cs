using System.Diagnostics;

namespace SshDashboard.App.Views;

/// <summary>
/// Temporary askpass helper for password-authenticated SSH. The password is
/// kept in a 0600 file in a private temporary directory, never on the command
/// line or in the log.
/// </summary>
internal sealed class SshAskPass : IDisposable
{
    private readonly string _directory;
    public IReadOnlyDictionary<string, string> Environment { get; }

    private SshAskPass(string directory, IReadOnlyDictionary<string, string> environment)
    {
        _directory = directory;
        Environment = environment;
    }

    public static SshAskPass Create(string password)
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "sshdashboard-askpass-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var passwordFile = Path.Combine(directory, "password");
        File.WriteAllText(passwordFile, password);

        var script = OperatingSystem.IsWindows()
            ? Path.Combine(directory, "askpass.cmd")
            : Path.Combine(directory, "askpass.sh");
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(script, "@type \"%SSH_DASHBOARD_PASSWORD_FILE%\"\r\n");
        }
        else
        {
            File.WriteAllText(script, "#!/bin/sh\ncat \"$SSH_DASHBOARD_PASSWORD_FILE\"\n");
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(passwordFile,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var environment = new Dictionary<string, string>
        {
            ["SSH_ASKPASS"] = script,
            ["SSH_ASKPASS_REQUIRE"] = "force",
            ["SSH_DASHBOARD_PASSWORD_FILE"] = passwordFile,
            ["DISPLAY"] = ":0",
        };
        return new SshAskPass(directory, environment);
    }

    public void Apply(ProcessStartInfo psi)
    {
        foreach (var pair in Environment) psi.Environment[pair.Key] = pair.Value;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Best effort cleanup; the directory is already private.
        }
    }
}
