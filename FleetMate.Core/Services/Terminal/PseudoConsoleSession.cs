using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FleetMate.Core.Services.Terminal;

/// <summary>
/// One process running behind a Windows pseudo console (ConPTY). Output
/// arrives as UTF-8 text with the VT sequences a terminal renders; input is
/// written back as text, including the VT sequences a terminal sends for keys.
/// </summary>
public sealed class PseudoConsoleSession : IDisposable
{
    private IntPtr _console;
    private SafeFileHandle? _inputWrite;
    private SafeFileHandle? _outputRead;
    private FileStream? _input;
    private IntPtr _process;
    private IntPtr _thread;
    private IntPtr _attributeList;
    private bool _disposed;

    /// <summary>Raised on a background thread with each chunk of decoded output.</summary>
    public event Action<string>? Output;

    /// <summary>Raised on a background thread when the process ends, with its exit code.</summary>
    public event Action<int>? Exited;

    public int ProcessId { get; private set; }

    /// <summary>Start <paramref name="commandLine"/> in a <paramref name="cols"/>×<paramref name="rows"/> console.</summary>
    public void Start(string commandLine, string? workingDirectory, IReadOnlyDictionary<string, string> environment,
        short cols, short rows)
    {
        if (!CreatePipe(out var inputRead, out _inputWrite, IntPtr.Zero, 0) ||
            !CreatePipe(out _outputRead, out var outputWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed");

        var hr = CreatePseudoConsole(new Coord(cols, rows), inputRead, outputWrite, 0, out _console);
        // The console holds its own references to these ends.
        inputRead.Dispose();
        outputWrite.Dispose();
        if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole failed");

        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        _attributeList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(_attributeList, 1, 0, ref size))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");
        if (!UpdateProcThreadAttribute(_attributeList, 0, (IntPtr)ProcThreadAttributePseudoConsole, _console,
                (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");

        var startup = new StartupInfoEx { StartupInfo = { cb = Marshal.SizeOf<StartupInfoEx>() }, lpAttributeList = _attributeList };
        var block = TerminalEnvironment.ToEnvironmentBlock(environment);
        var commandBuffer = new StringBuilder(commandLine);
        if (!CreateProcessW(null, commandBuffer, IntPtr.Zero, IntPtr.Zero, false,
                ExtendedStartupInfoPresent | CreateUnicodeEnvironment, block,
                string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
                ref startup, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start: {commandLine}");

        _process = info.hProcess;
        _thread = info.hThread;
        ProcessId = info.dwProcessId;
        _input = new FileStream(_inputWrite, FileAccess.Write);

        new Thread(ReadLoop) { IsBackground = true, Name = "ConPTY output" }.Start();
        new Thread(WaitLoop) { IsBackground = true, Name = "ConPTY wait" }.Start();
    }

    public void Write(string text)
    {
        if (_input == null || _disposed) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _input.Write(bytes, 0, bytes.Length);
            _input.Flush();
        }
        catch (IOException) { /* the process has gone */ }
    }

    public void Resize(short cols, short rows)
    {
        if (_console != IntPtr.Zero && cols > 0 && rows > 0)
            ResizePseudoConsole(_console, new Coord(cols, rows));
    }

    private void ReadLoop()
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var buffer = new byte[8192];
        var chars = new char[8192 + 4];
        try
        {
            using var stream = new FileStream(_outputRead!, FileAccess.Read);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                // A decoder carries a multi-byte character split across reads.
                var count = decoder.GetChars(buffer, 0, read, chars, 0);
                if (count > 0) Output?.Invoke(new string(chars, 0, count));
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private void WaitLoop()
    {
        WaitForSingleObject(_process, Infinite);
        GetExitCodeProcess(_process, out var code);
        Exited?.Invoke((int)code);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Closing the console ends the client process and the output pipe.
        if (_console != IntPtr.Zero) { ClosePseudoConsole(_console); _console = IntPtr.Zero; }
        _input?.Dispose();
        _outputRead?.Dispose();
        if (_process != IntPtr.Zero) CloseHandle(_process);
        if (_thread != IntPtr.Zero) CloseHandle(_thread);
        if (_attributeList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
            _attributeList = IntPtr.Zero;
        }
    }

    // ── Win32 ────────────────────────────────────────────────────────────

    private const int ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint Infinite = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr console, Coord size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value,
        IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, string environment, string? currentDirectory,
        ref StartupInfoEx startup, out ProcessInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
