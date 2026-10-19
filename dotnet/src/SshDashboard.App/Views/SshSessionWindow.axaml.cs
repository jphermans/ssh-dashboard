using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SshDashboard.Core;

namespace SshDashboard.App.Views;

public partial class SshSessionWindow : Window
{
    private readonly HostRow _host;
    private readonly CancellationTokenSource _cts = new();
    private Process? _process;
    private PtySession? _pty;
    private readonly Decoder _ptyDecoder = Encoding.UTF8.GetDecoder();
    private string? _tempKey;

    public SshSessionWindow(HostRow host)
    {
        InitializeComponent();
        _host = host;
        Title = "SSH — " + host.Label;
        TitleText.Text = host.Label;
        SubtitleText.Text = string.IsNullOrWhiteSpace(host.User)
            ? $"{host.Address}:{host.Port}"
            : $"{host.User}@{host.Address}:{host.Port}";
        Terminal.Input += SendInput;
        Opened += (_, _) =>
        {
            Terminal.Focus();
            Start();
        };
        Closing += (_, _) => Stop();
    }

    private async void Start()
    {
        try
        {
            var psi = new ProcessStartInfo("ssh")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-tt");
            foreach (var arg in SshLauncher.BuildArguments(_host)) psi.ArgumentList.Add(arg);
            var ppkPath = SshLauncher.ResolvePpkPath(_host);
            if (ppkPath is not null)
            {
                try
                {
                    var ppk = PpkFile.Load(ppkPath);
                    if (ppk.IsEncrypted)
                    {
                        var passphrase = await new PassphraseWindow(Path.GetFileName(ppkPath))
                            .ShowDialog<string?>(this);
                        if (string.IsNullOrEmpty(passphrase))
                        {
                            SetStatus("Cancelled", false);
                            Terminal.WriteSystem("Passphrase required — session cancelled.");
                            return;
                        }
                        _tempKey = ppk.ConvertWithPuttygen(passphrase);
                    }
                    else
                    {
                        _tempKey = ppk.WriteTemporaryOpenSsh();
                    }
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add(_tempKey);
                    Terminal.WriteSystem($"Using PuTTY key {Path.GetFileName(ppkPath)} ({ppk.Kind}).");
                }
                catch (Exception ex)
                {
                    CleanupKey();
                    SetStatus("PuTTY key error", false);
                    Terminal.WriteSystem("PuTTY key: " + ex.Message);
                    return;
                }
            }
            var rows = Math.Max(12, Terminal.Rows);
            var cols = Math.Max(40, Terminal.Columns);
            psi.ArgumentList.Add($"stty rows {rows} cols {cols} 2>/dev/null; exec $SHELL -l");
            psi.Environment["TERM"] = "xterm-256color";
            psi.Environment["LANG"] = Environment.GetEnvironmentVariable("LANG") ?? "en_US.UTF-8";

            if (PtySession.Supported)
            {
                var pty = new PtySession("/usr/bin/ssh", psi.ArgumentList.ToList(),
                    Terminal.Columns, Terminal.Rows);
                _pty = pty;
                pty.Output += bytes =>
                {
                    var chars = new char[bytes.Length];
                    var count = _ptyDecoder.GetChars(bytes, 0, bytes.Length, chars, 0);
                    if (count > 0)
                    {
                        var text = new string(chars, 0, count);
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (StatusText.Text == "Connecting…") SetStatus("Connected", true);
                            Terminal.Feed(text);
                        });
                    }
                };
                pty.Exited += code => Dispatcher.UIThread.Post(() => OnExited(code));
                Terminal.Resized += (cols, rows) => pty.Resize(cols, rows);
                Terminal.WriteSystem($"Connecting to {SubtitleText.Text} …");
                return;
            }

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Exited += (_, _) => Dispatcher.UIThread.Post(() =>
                OnExited(_process?.ExitCode ?? -1));
            _process.Start();

            Terminal.WriteSystem($"Connecting to {SubtitleText.Text} …");
            _ = PumpAsync(_process.StandardOutput.BaseStream);
            _ = PumpAsync(_process.StandardError.BaseStream);
        }
        catch (Exception ex)
        {
            SetStatus("Could not start ssh", false);
            Terminal.WriteSystem("Could not start ssh: " + ex.Message);
        }
    }

    private async Task PumpAsync(Stream stream)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[4096];
        var chars = new char[4096];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(), _cts.Token);
                if (read <= 0) break;
                var count = decoder.GetChars(bytes, 0, read, chars, 0);
                if (count <= 0) continue;
                var text = new string(chars, 0, count);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (StatusText.Text == "Connecting…") SetStatus("Connected", true);
                    Terminal.Feed(text);
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Terminal.WriteSystem("Connection error: " + ex.Message));
        }
    }

    private void SendInput(byte[] data)
    {
        if (_pty is not null)
        {
            _pty.Write(data);
            return;
        }
        try
        {
            if (_process is not { HasExited: false } process) return;
            process.StandardInput.BaseStream.Write(data, 0, data.Length);
            process.StandardInput.BaseStream.Flush();
        }
        catch { }
    }

    private void CleanupKey()
    {
        if (_tempKey is null) return;
        try { File.Delete(_tempKey); } catch { }
        _tempKey = null;
    }

    private void OnExited(int code)
    {
        SetStatus(code == 0 ? "Disconnected" : $"Disconnected (exit {code})", false);
        Terminal.WriteSystem(code == 0
            ? "Session ended."
            : $"ssh exited with code {code}.");
        CleanupKey();
        if (code != 0) return; // keep the window so the error stays readable
        DispatcherTimer.RunOnce(() =>
        {
            if (IsVisible) Close();
        }, TimeSpan.FromMilliseconds(1400));
    }

    private void SetStatus(string text, bool connected)
    {
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(Color.Parse(connected ? "#3FCF8E" : "#F7768E"));
    }

    private void Disconnect_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void Stop()
    {
        CleanupKey();
        _cts.Cancel();
        _pty?.Dispose();
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(true);
            }
        }
        catch { }
    }
}
