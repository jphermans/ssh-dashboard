using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SshDashboard.App.Views;

/// <summary>
/// A real pseudo-terminal for the ssh child. Unlike a pair of pipes, the pty
/// forwards window-size changes to ssh (SIGWINCH), so full-screen programs
/// resize with the window. macOS and Linux only; Windows keeps the pipe path.
/// </summary>
public sealed class PtySession : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort Row;
        public ushort Col;
        public ushort XPixel;
        public ushort YPixel;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int openpty(out int master, out int slave, IntPtr name, IntPtr termios, ref WinSize size);

    [DllImport("libc", SetLastError = true)]
    private static extern int fork();

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, int arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup2(int oldFd, int newFd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int execve(string path, IntPtr[] argv, IntPtr[] envp);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    [DllImport("libc")]
    private static extern void _exit(int code);

    private readonly FileStream _stream;
    private readonly object _writeGate = new();
    private readonly CancellationTokenSource _cts = new();
    private int _pid;
    private int _masterFd;
    private int _slaveFd;
    private string? _deviceName;
    private bool _disposed;

    private static ulong SetControllingTty => OperatingSystem.IsMacOS() ? 0x20007461UL : 0x540EUL;

    public event Action<byte[]>? Output;
    public event Action<int>? Exited;

    public static bool Supported => !OperatingSystem.IsWindows();

    public PtySession(string file, IReadOnlyList<string> arguments, int columns, int rows,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        if (!Supported) throw new PlatformNotSupportedException("A pty needs macOS or Linux.");
        var size = new WinSize { Col = (ushort)Math.Max(20, columns), Row = (ushort)Math.Max(5, rows) };
        var nameBuffer = Marshal.AllocHGlobal(128);
        int master, slave;
        try
        {
            for (var i = 0; i < 128; i++) Marshal.WriteByte(nameBuffer, i, 0);
            if (openpty(out master, out slave, nameBuffer, IntPtr.Zero, ref size) != 0)
                throw new IOException("openpty failed: " + Marshal.GetLastPInvokeErrorMessage());
            _deviceName = Marshal.PtrToStringAnsi(nameBuffer);
        }
        finally { Marshal.FreeHGlobal(nameBuffer); }

        var argv = new IntPtr[arguments.Count + 2];
        var envp = new IntPtr[0];
        var allocated = new List<IntPtr>();
        try
        {
            argv[0] = Alloc(file, allocated);
            for (var i = 0; i < arguments.Count; i++) argv[i + 1] = Alloc(arguments[i], allocated);
            argv[^1] = IntPtr.Zero;

            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                if (entry.Key is string key && entry.Value is string value) env[key] = value;
            env["TERM"] = "xterm-256color";
            env.TryAdd("LANG", "en_US.UTF-8");
            if (environment is not null)
                foreach (var pair in environment) env[pair.Key] = pair.Value;
            envp = new IntPtr[env.Count + 1];
            var index = 0;
            foreach (var pair in env)
                envp[index++] = Alloc(pair.Key + "=" + pair.Value, allocated);
            envp[^1] = IntPtr.Zero;

            var pid = fork();
            if (pid < 0) throw new IOException("fork failed: " + Marshal.GetLastPInvokeErrorMessage());
            if (pid == 0)
            {
                // Child: only async-signal-safe libc calls before exec.
                setsid();
                ioctl(slave, SetControllingTty, 0);
                dup2(slave, 0);
                dup2(slave, 1);
                dup2(slave, 2);
                if (master > 2) close(master);
                if (slave > 2) close(slave);
                execve(file, argv, envp);
                _exit(127);
            }

            _pid = pid;
            _masterFd = master;
            _slaveFd = slave;
            _stream = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)master, true),
                FileAccess.ReadWrite, 1, isAsync: false);
        }
        catch
        {
            close(master);
            close(slave);
            throw;
        }
        finally
        {
            foreach (var pointer in allocated) Marshal.FreeCoTaskMem(pointer);
        }

        _ = ReadLoopAsync();
    }

    private static IntPtr Alloc(string value, List<IntPtr> allocated)
    {
        var pointer = Marshal.StringToCoTaskMemUTF8(value);
        allocated.Add(pointer);
        return pointer;
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = await _stream.ReadAsync(buffer.AsMemory(), _cts.Token);
                if (read <= 0) break;
                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                Output?.Invoke(chunk);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            var status = 0;
            if (_pid > 0) waitpid(_pid, out status, 0);
            var code = (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
            Exited?.Invoke(code);
        }
    }

    public void Write(byte[] data)
    {
        if (_disposed) return;
        lock (_writeGate)
        {
            try { _stream.Write(data, 0, data.Length); _stream.Flush(); }
            catch { }
        }
    }

    public void Resize(int columns, int rows)
    {
        if (_disposed) return;
        var size = new WinSize { Col = (ushort)Math.Max(20, columns), Row = (ushort)Math.Max(5, rows) };
        if (!string.IsNullOrWhiteSpace(_deviceName))
        {
            try
            {
                var psi = new ProcessStartInfo("/bin/stty")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add(_deviceName);
                psi.ArgumentList.Add("rows");
                psi.ArgumentList.Add(size.Row.ToString());
                psi.ArgumentList.Add("cols");
                psi.ArgumentList.Add(size.Col.ToString());
                using var process = Process.Start(psi);
                process?.WaitForExit(1000);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        if (_pid > 0) try { kill(_pid, 15); } catch { }
        if (_slaveFd > 2) try { close(_slaveFd); } catch { }
        try { _stream.Dispose(); } catch { }
    }
}
