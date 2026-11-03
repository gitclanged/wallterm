using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WallTerm;

internal delegate void PtyOutputHandler(ReadOnlySpan<char> text);

/// <summary>
/// A child process attached to a Win32 pseudo console. Output is read on a dedicated thread that blocks in
/// ReadFile on the pipe (the kernel wakes it only when bytes arrive), and process exit is observed through a
/// registered wait on the process handle — no polling or timers anywhere.
/// </summary>
internal sealed class ConPty : IDisposable
{
    private IntPtr _hpc;
    private IntPtr _hProcess;
    private readonly FileStream _input;
    private readonly SafeFileHandle _outputRead;
    private readonly RegisteredWaitHandle _exitWait;
    private readonly PtyOutputHandler _onOutput;
    private readonly object _writeLock = new();
    private int _disposed;

    public int ProcessId { get; }

    public ConPty(string commandLine, string? workingDirectory, int cols, int rows, PtyOutputHandler onOutput, Action<ConPty, int> onExit)
    {
        _onOutput = onOutput;

        if (!Native.CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0) ||
            !Native.CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
            throw new InvalidOperationException($"CreatePipe failed ({Marshal.GetLastWin32Error()}).");

        int hr = Native.CreatePseudoConsole(new Native.COORD(cols, rows), inputRead, outputWrite, 0, out _hpc);
        if (hr != 0)
        {
            inputRead.Dispose(); inputWrite.Dispose(); outputRead.Dispose(); outputWrite.Dispose();
            throw new InvalidOperationException($"CreatePseudoConsole failed (0x{hr:X8}).");
        }

        // The pseudo console duplicated its ends; closing ours lets the reader see EOF when the console goes away.
        inputRead.Dispose();
        outputWrite.Dispose();

        var size = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attrList = Marshal.AllocHGlobal(size);
        try
        {
            if (!Native.InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
                throw new InvalidOperationException($"InitializeProcThreadAttributeList failed ({Marshal.GetLastWin32Error()}).");
            try
            {
                if (!Native.UpdateProcThreadAttribute(attrList, 0, Native.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hpc, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new InvalidOperationException($"UpdateProcThreadAttribute failed ({Marshal.GetLastWin32Error()}).");

                var si = new Native.STARTUPINFOEX();
                si.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
                // Null std handles: the child must bind to the pseudo console, never to handles this process inherited.
                si.StartupInfo.dwFlags = Native.STARTF_USESTDHANDLES;
                si.lpAttributeList = attrList;

                if (!Native.CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                        Native.EXTENDED_STARTUPINFO_PRESENT | Native.CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero,
                        workingDirectory, ref si, out var pi))
                {
                    int err = Marshal.GetLastWin32Error();
                    Native.ClosePseudoConsole(_hpc);
                    _hpc = IntPtr.Zero;
                    inputWrite.Dispose(); outputRead.Dispose();
                    throw new InvalidOperationException($"Could not start '{commandLine}' (Win32 error {err}).");
                }

                Native.CloseHandle(pi.hThread);
                _hProcess = pi.hProcess;
                ProcessId = pi.dwProcessId;
            }
            finally
            {
                Native.DeleteProcThreadAttributeList(attrList);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(attrList);
        }

        _input = new FileStream(inputWrite, FileAccess.Write, 1, false);
        _outputRead = outputRead;

        var reader = new Thread(ReadLoop) { IsBackground = true, Name = "WallTerm ConPTY reader" };
        reader.Start();

        _exitWait = ThreadPool.RegisterWaitForSingleObject(new ProcessWaitHandle(_hProcess), (_, _) =>
        {
            uint code = 0;
            if (_hProcess != IntPtr.Zero) Native.GetExitCodeProcess(_hProcess, out code);
            onExit(this, unchecked((int)code));
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    private void ReadLoop()
    {
        var decoder = new UTF8Encoding(false).GetDecoder();
        var bytes = new byte[64 * 1024];
        var chars = new char[bytes.Length + 4];
        using var stream = new FileStream(_outputRead, FileAccess.Read, 1, false);
        while (true)
        {
            int n;
            try { n = stream.Read(bytes, 0, bytes.Length); }
            catch (IOException) { break; }
            catch (ObjectDisposedException) { break; }
            if (n <= 0) break;
            int count = decoder.GetChars(bytes, 0, n, chars, 0, flush: false);
            if (count > 0) _onOutput(chars.AsSpan(0, count));
        }
    }

    public void Write(string text)
    {
        if (_disposed != 0 || text.Length == 0) return;
        var data = Encoding.UTF8.GetBytes(text);
        lock (_writeLock)
        {
            try
            {
                _input.Write(data, 0, data.Length);
                _input.Flush();
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    public void Resize(int cols, int rows)
    {
        if (_disposed == 0 && _hpc != IntPtr.Zero)
            Native.ResizePseudoConsole(_hpc, new Native.COORD(cols, rows));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _exitWait.Unregister(null);
        lock (_writeLock) _input.Dispose();
        // Closing the pseudo console terminates the attached client and breaks the output pipe, ending ReadLoop.
        if (_hpc != IntPtr.Zero) { Native.ClosePseudoConsole(_hpc); _hpc = IntPtr.Zero; }
        if (_hProcess != IntPtr.Zero) { Native.CloseHandle(_hProcess); _hProcess = IntPtr.Zero; }
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(IntPtr handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: false);
    }
}
