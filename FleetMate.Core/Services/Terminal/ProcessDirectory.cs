using System.Runtime.InteropServices;

namespace FleetMate.Core.Services.Terminal;

/// <summary>
/// Reads another process's current directory from its process parameters.
/// This is the fallback for shells that never report their directory with
/// OSC 7 or OSC 9;9 (PowerShell and cmd, unless their prompt is set up to).
/// It needs only PROCESS_QUERY_INFORMATION and PROCESS_VM_READ on a process
/// this user started, and works for 64-bit children of a 64-bit host.
/// </summary>
public static class ProcessDirectory
{
    public static string? TryGet(int processId)
    {
        if (!Environment.Is64BitProcess) return null;
        var handle = OpenProcess(QueryInformation | VmRead, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var info = new ProcessBasicInformation();
            if (NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
                return null;
            if (Is32BitProcess(handle)) return null;

            // PEB.ProcessParameters at 0x20; RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath at 0x38.
            var parameters = ReadPointer(handle, info.PebBaseAddress + 0x20);
            if (parameters == IntPtr.Zero) return null;
            var buffer = new byte[16];
            if (!ReadProcessMemory(handle, parameters + 0x38, buffer, buffer.Length, out _)) return null;
            var length = BitConverter.ToUInt16(buffer, 0);
            var address = (IntPtr)BitConverter.ToInt64(buffer, 8);
            if (length == 0 || address == IntPtr.Zero) return null;

            var text = new byte[length];
            if (!ReadProcessMemory(handle, address, text, length, out _)) return null;
            return System.Text.Encoding.Unicode.GetString(text);
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static IntPtr ReadPointer(IntPtr process, IntPtr address)
    {
        var buffer = new byte[8];
        return ReadProcessMemory(process, address, buffer, 8, out _) ? (IntPtr)BitConverter.ToInt64(buffer, 0) : IntPtr.Zero;
    }

    private static bool Is32BitProcess(IntPtr process) =>
        IsWow64Process(process, out var wow64) && wow64;

    private const int QueryInformation = 0x0400;
    private const int VmRead = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr process, out bool wow64);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int size, out int returned);
}
