using System.Runtime.InteropServices;

namespace FleetMate.Core.Services.Terminal;

/// <summary>
/// Whether a terminal session is running an agent CLI rather than a bare
/// shell. Windows has no foreground process group to ask, so the session's
/// process and its descendants are read from a process snapshot: an agent is
/// running when the session's own program is one, or a shell has started one
/// that has not exited.
/// </summary>
public static class AgentProcessTree
{
    /// <summary>One process in a snapshot.</summary>
    public readonly record struct Entry(int ProcessId, int ParentProcessId, string Name);

    /// <summary>Programs that count as an agent CLI. An npm-installed CLI runs as node.</summary>
    public static readonly IReadOnlyList<string> AgentPrograms = new[] { "claude", "codex", "node" };

    /// <summary>Whether <paramref name="name"/> (with or without .exe) is an agent CLI.</summary>
    public static bool IsAgentProgram(string name)
    {
        var bare = Path.GetFileNameWithoutExtension(name.Trim()).ToLowerInvariant();
        return AgentPrograms.Any(p => bare.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether <paramref name="rootProcessId"/> or any of its descendants in
    /// <paramref name="snapshot"/> is an agent CLI.
    /// </summary>
    public static bool HasAgent(IEnumerable<Entry> snapshot, int rootProcessId)
    {
        if (rootProcessId <= 0) return false;
        var entries = snapshot.ToList();
        var byParent = entries.Where(e => e.ProcessId != e.ParentProcessId).ToLookup(e => e.ParentProcessId);
        var seen = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(rootProcessId);
        var root = entries.FirstOrDefault(e => e.ProcessId == rootProcessId);
        if (root.Name != null && IsAgentProgram(root.Name)) return true;
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id)) continue;
            foreach (var child in byParent[id])
            {
                if (IsAgentProgram(child.Name)) return true;
                queue.Enqueue(child.ProcessId);
            }
        }
        return false;
    }

    /// <summary>Whether the session whose process is <paramref name="rootProcessId"/> is running an agent CLI now.</summary>
    public static bool IsAgentRunning(int rootProcessId)
    {
        if (rootProcessId <= 0) return false;
        try { return HasAgent(Snapshot(), rootProcessId); }
        catch (Exception) { return false; }
    }

    /// <summary>Every process on the machine, from a Toolhelp snapshot.</summary>
    public static List<Entry> Snapshot()
    {
        var result = new List<Entry>();
        var handle = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (handle == InvalidHandle) return result;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(handle, ref entry)) return result;
            do
            {
                result.Add(new Entry((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile));
            } while (Process32Next(handle, ref entry));
        }
        finally
        {
            CloseHandle(handle);
        }
        return result;
    }

    private const uint SnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW", SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW", SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
