using System;
using System.Text;
using System.Runtime.InteropServices;

namespace TerminalStudio.Services;

public sealed class TerminalSession : IDisposable
{
    private IntPtr _hPC = IntPtr.Zero;
    private IntPtr _hInputReadPipe = IntPtr.Zero;
    private IntPtr _hInputWritePipe = IntPtr.Zero;
    private IntPtr _hOutputReadPipe = IntPtr.Zero;
    private IntPtr _hOutputWritePipe = IntPtr.Zero;

    private NativeMethods.PROCESS_INFORMATION _processInfo;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _readOutputTask;
    private bool _disposed;

    public event Action<byte[]>? OutputReceived;

    public void Start(string commandLine = "powershell.exe", string? workingDirectory = null, short cols = 80, short rows = 25)
    {
        if (_hPC != IntPtr.Zero) return;

        var sa = new NativeMethods.SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<NativeMethods.SECURITY_ATTRIBUTES>(),
            bInheritHandle = true,
            lpSecurityDescriptor = IntPtr.Zero
        };

        if (!NativeMethods.CreatePipe(out _hInputReadPipe, out _hInputWritePipe, ref sa, 0))
            throw new InvalidOperationException("Failed to create input pipe.");

        if (!NativeMethods.CreatePipe(out _hOutputReadPipe, out _hOutputWritePipe, ref sa, 0))
            throw new InvalidOperationException("Failed to create output pipe.");

        var size = new NativeMethods.COORD(cols, rows);
        int hr = NativeMethods.CreatePseudoConsole(size, _hInputReadPipe, _hOutputWritePipe, 0, out _hPC);
        if (hr != 0)
            throw new InvalidOperationException($"Failed to create pseudo console. HRESULT: {hr}");

        NativeMethods.CloseHandle(_hInputReadPipe);
        _hInputReadPipe = IntPtr.Zero;
        NativeMethods.CloseHandle(_hOutputWritePipe);
        _hOutputWritePipe = IntPtr.Zero;

        StartProcess(commandLine, workingDirectory);

        _cancellationTokenSource = new CancellationTokenSource();
        _readOutputTask = Task.Run(() => ReadOutputAsync(_cancellationTokenSource.Token));
    }

    public void WriteInput(string text)
    {
        if (string.IsNullOrEmpty(text) || _hInputWritePipe == IntPtr.Zero || _disposed) return;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        NativeMethods.WriteFile(_hInputWritePipe, bytes, (uint)bytes.Length, out _, IntPtr.Zero);
    }

    public void Resize(short cols, short rows)
    {
        if (_hPC == IntPtr.Zero || _disposed) return;
        var size = new NativeMethods.COORD(cols, rows);
        NativeMethods.ResizePseudoConsole(_hPC, size);
    }

    private void StartProcess(string commandLine, string? workingDirectory)
    {
        string finalCommandLine = commandLine;

        if (commandLine.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) || commandLine.Equals("powershell", StringComparison.OrdinalIgnoreCase))
        {
            string psScript = "function global:prompt { $p = $executionContext.SessionState.Path.CurrentFileSystemLocation.ProviderPath; $e = [char]27; Write-Host -NoNewline \"$e]9;9;`\"$p`\"$e\\\"; \"PS $p> \" }";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
            finalCommandLine = $"powershell.exe -NoExit -EncodedCommand {encoded}";
        }
        else if (commandLine.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) || commandLine.Equals("pwsh", StringComparison.OrdinalIgnoreCase))
        {
            string psScript = "function global:prompt { $p = $executionContext.SessionState.Path.CurrentFileSystemLocation.ProviderPath; $e = [char]27; Write-Host -NoNewline \"$e]9;9;`\"$p`\"$e\\\"; \"PS $p> \" }";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
            finalCommandLine = $"pwsh.exe -NoExit -EncodedCommand {encoded}";
        }

        Environment.SetEnvironmentVariable("PROMPT", "$E]9;9;\"$P\"$E\\$P$G");
        Environment.SetEnvironmentVariable("PROMPT_COMMAND", "printf \"\\033]9;9;\\\"%s\\\"\\033\\\\\" \"$PWD\"");

        string existingWslEnv = Environment.GetEnvironmentVariable("WSLENV") ?? "";
        if (!existingWslEnv.Contains("PROMPT_COMMAND"))
        {
            string newWslEnv = string.IsNullOrEmpty(existingWslEnv) ? "PROMPT_COMMAND/u" : existingWslEnv + ":PROMPT_COMMAND/u";
            Environment.SetEnvironmentVariable("WSLENV", newWslEnv);
        }

        IntPtr lpSize = IntPtr.Zero;
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
        IntPtr lpAttributeList = Marshal.AllocHGlobal(lpSize);

        try
        {
            NativeMethods.InitializeProcThreadAttributeList(lpAttributeList, 1, 0, ref lpSize);
            NativeMethods.UpdateProcThreadAttribute(
                lpAttributeList,
                0,
                (IntPtr)NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _hPC,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero);

            var startupInfoEx = new NativeMethods.STARTUPINFOEX();
            startupInfoEx.StartupInfo.cb = Marshal.SizeOf<NativeMethods.STARTUPINFOEX>();
            startupInfoEx.lpAttributeList = lpAttributeList;

            var processSa = new NativeMethods.SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<NativeMethods.SECURITY_ATTRIBUTES>(),
                bInheritHandle = false,
                lpSecurityDescriptor = IntPtr.Zero
            };

            uint creationFlags = NativeMethods.EXTENDED_STARTUPINFO_PRESENT | NativeMethods.CREATE_UNICODE_ENVIRONMENT;

            if (!NativeMethods.CreateProcessW(
                null,
                finalCommandLine,
                ref processSa,
                ref processSa,
                false,
                creationFlags,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfoEx,
                out _processInfo))
            {
                throw new InvalidOperationException($"Failed to create process. Error: {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            NativeMethods.DeleteProcThreadAttributeList(lpAttributeList);
            Marshal.FreeHGlobal(lpAttributeList);
        }
    }

    private void ReadOutputAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8192];
        while (!cancellationToken.IsCancellationRequested && _hOutputReadPipe != IntPtr.Zero)
        {
            if (NativeMethods.PeekNamedPipe(_hOutputReadPipe, null, 0, out _, out uint bytesAvailable, out _) && bytesAvailable > 0)
            {
                uint bytesToRead = Math.Min(bytesAvailable, (uint)buffer.Length);
                if (NativeMethods.ReadFile(_hOutputReadPipe, buffer, bytesToRead, out uint bytesRead, IntPtr.Zero) && bytesRead > 0)
                {
                    byte[] outputData = new byte[bytesRead];
                    Array.Copy(buffer, outputData, bytesRead);
                    OutputReceived?.Invoke(outputData);
                    continue;
                }
            }

            Thread.Sleep(10);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cancellationTokenSource?.Cancel();

        if (_hPC != IntPtr.Zero)
        {
            NativeMethods.ClosePseudoConsole(_hPC);
            _hPC = IntPtr.Zero;
        }

        if (_hInputWritePipe != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_hInputWritePipe);
            _hInputWritePipe = IntPtr.Zero;
        }

        if (_hOutputReadPipe != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_hOutputReadPipe);
            _hOutputReadPipe = IntPtr.Zero;
        }

        if (_processInfo.hProcess != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_processInfo.hProcess);
            _processInfo.hProcess = IntPtr.Zero;
        }

        if (_processInfo.hThread != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_processInfo.hThread);
            _processInfo.hThread = IntPtr.Zero;
        }
    }
}