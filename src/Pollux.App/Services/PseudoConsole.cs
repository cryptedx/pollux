using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pollux.App.Services;

/// <summary>A shell running in a Windows pseudo console (ConPTY). Output arrives as UTF-8 text with VT sequences for xterm.js.</summary>
internal sealed unsafe partial class PseudoConsole : IDisposable
{
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private static readonly nint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    private nint _hpc;
    private nint _process;
    private nint _attributeList;
    private FileStream? _input;
    private FileStream? _output;
    private Thread? _reader;
    private RegisteredWaitHandle? _exitWait;
    private ManualResetEvent? _processEvent;
    private bool _disposed;

    public event Action<string>? Output;
    public event Action? Exited;

    public static PseudoConsole Start(string commandLine, string workingDirectory, short cols, short rows)
    {
        var pc = new PseudoConsole();
        pc.Create(commandLine, workingDirectory, cols, rows);
        return pc;
    }

    private void Create(string commandLine, string workingDirectory, short cols, short rows)
    {
        if (!CreatePipe(out var inRead, out var inWrite, 0, 0) || !CreatePipe(out var outRead, out var outWrite, 0, 0))
            throw new InvalidOperationException("CreatePipe failed: " + Marshal.GetLastPInvokeError());

        var hr = CreatePseudoConsole(Coord(cols, rows), inRead.DangerousGetHandle(), outWrite.DangerousGetHandle(), 0, out _hpc);
        if (hr != 0) throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");

        // The console owns these ends now.
        inRead.Dispose();
        outWrite.Dispose();

        nint size = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        _attributeList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(_attributeList, 1, 0, ref size)) throw new InvalidOperationException("InitializeProcThreadAttributeList failed");
        var hpc = _hpc;
        if (!UpdateProcThreadAttribute(_attributeList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hpc, sizeof(nint), 0, 0))
            throw new InvalidOperationException("UpdateProcThreadAttribute failed");

        var si = new STARTUPINFOEXW();
        si.StartupInfo.cb = sizeof(STARTUPINFOEXW);
        si.lpAttributeList = _attributeList;
        var cmd = Marshal.StringToHGlobalUni(commandLine);
        try
        {
            if (!CreateProcessW(0, cmd, 0, 0, false, EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, 0, workingDirectory, &si, out var pi))
                throw new InvalidOperationException("CreateProcess failed: " + Marshal.GetLastPInvokeError());
            _process = pi.hProcess;
            CloseHandle(pi.hThread);
        }
        finally
        {
            Marshal.FreeHGlobal(cmd);
        }

        _input = new FileStream(inWrite, FileAccess.Write, 1);
        _output = new FileStream(outRead, FileAccess.Read, 1);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "Pollux ConPTY reader" };
        _reader.Start();

        // The output pipe only closes with ClosePseudoConsole, so watch the process itself for exit.
        _processEvent = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(_process, ownsHandle: false) };
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_processEvent, (_, _) => { if (!_disposed) Exited?.Invoke(); }, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    private void ReadLoop()
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[16 * 1024];
        var chars = new char[16 * 1024 + 4];
        try
        {
            while (true)
            {
                var n = _output!.Read(bytes, 0, bytes.Length);
                if (n <= 0) break;
                var c = decoder.GetChars(bytes, 0, n, chars, 0);
                if (c > 0) Output?.Invoke(new string(chars, 0, c));
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Write(string text)
    {
        if (_input is null || _disposed) return;
        try
        {
            var data = Encoding.UTF8.GetBytes(text);
            _input.Write(data, 0, data.Length);
            _input.Flush();
        }
        catch (IOException)
        {
        }
    }

    public void Resize(short cols, short rows)
    {
        if (_hpc != 0 && cols > 0 && rows > 0) ResizePseudoConsole(_hpc, Coord(cols, rows));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _exitWait?.Unregister(null);
        _processEvent?.Dispose();
        if (_hpc != 0) { ClosePseudoConsole(_hpc); _hpc = 0; }
        if (_process != 0) { TerminateProcess(_process, 0); CloseHandle(_process); _process = 0; }
        _input?.Dispose();
        _output?.Dispose();
        if (_attributeList != 0) { DeleteProcThreadAttributeList(_attributeList); Marshal.FreeHGlobal(_attributeList); _attributeList = 0; }
    }

    private static int Coord(short x, short y) => (ushort)x | ((ushort)y << 16);

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, int size);

    [LibraryImport("kernel32.dll")]
    private static partial int CreatePseudoConsole(int size, nint input, nint output, uint flags, out nint hpc);

    [LibraryImport("kernel32.dll")]
    private static partial int ResizePseudoConsole(nint hpc, int size);

    [LibraryImport("kernel32.dll")]
    private static partial void ClosePseudoConsole(nint hpc);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(nint app, nint commandLine, nint pa, nint ta, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint env, string cwd, STARTUPINFOEXW* si, out PROCESS_INFORMATION pi);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint h);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint h, uint code);
}
