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

    private void Start()
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
            var rows = Math.Max(12, Terminal.Rows);
            var cols = Math.Max(40, Terminal.Columns);
            psi.ArgumentList.Add($"stty rows {rows} cols {cols} 2>/dev/null; exec $SHELL -l");
            psi.Environment["TERM"] = "xterm-256color";
            psi.Environment["LANG"] = Environment.GetEnvironmentVariable("LANG") ?? "en_US.UTF-8";

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Exited += (_, _) => Dispatcher.UIThread.Post(OnExited);
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
        try
        {
            if (_process is not { HasExited: false } process) return;
            process.StandardInput.BaseStream.Write(data, 0, data.Length);
            process.StandardInput.BaseStream.Flush();
        }
        catch { }
    }

    private void OnExited()
    {
        var code = -1;
        try { code = _process?.ExitCode ?? -1; } catch { }
        SetStatus(code == 0 ? "Disconnected" : $"Disconnected (exit {code})", false);
        Terminal.WriteSystem(code == 0
            ? "Session ended."
            : $"ssh exited with code {code}.");
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
        _cts.Cancel();
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
