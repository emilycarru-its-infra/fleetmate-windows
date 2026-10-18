using System.Runtime.InteropServices;

namespace FleetMate.Core.Services.Repos;

/// <summary>
/// What deleting a local clone would lose that its remotes do not hold:
/// uncommitted and untracked files, commits on no remote, stashes, and
/// worktrees added from it. The Repos sidebar and <c>fleetmate repos
/// remove-clone</c> both read it before anything moves to the Recycle Bin.
/// </summary>
public sealed record CloneRemovalCheck
{
    public const int SampleLimit = 10;

    public required string Path { get; init; }
    /// <summary>Paths with staged, unstaged or untracked changes.</summary>
    public List<string> Uncommitted { get; init; } = new();
    /// <summary>Commits reachable from a local branch but from no remote-tracking ref.</summary>
    public int UnpushedCount { get; set; }
    /// <summary>The newest of those, <c>&lt;short sha&gt; &lt;subject&gt;</c>, at most <see cref="SampleLimit"/>.</summary>
    public List<string> UnpushedSample { get; init; } = new();
    /// <summary>Local branches holding commits no remote has.</summary>
    public List<string> UnpushedBranches { get; init; } = new();
    /// <summary><c>git stash list</c> lines.</summary>
    public List<string> Stashes { get; init; } = new();
    /// <summary>
    /// Linked worktrees other than the clone itself. Removing the clone leaves
    /// them broken; any inside it go to the Recycle Bin with it.
    /// </summary>
    public List<string> Worktrees { get; init; } = new();
    /// <summary>A check git could not run, reported rather than read as "nothing".</summary>
    public List<string> Problems { get; init; } = new();

    /// <summary>Nothing would be lost: the folder can go with a plain confirmation.</summary>
    public bool IsSafe => Uncommitted.Count == 0 && UnpushedCount == 0 && Stashes.Count == 0
        && Worktrees.Count == 0 && Problems.Count == 0;

    /// <summary>One line per kind of loss, in plain words, for a dialog or the CLI.</summary>
    public List<string> LossDescription
    {
        get
        {
            var lines = new List<string>();
            if (Uncommitted.Count > 0)
            {
                var shown = string.Join(", ", Uncommitted.Take(5));
                var more = Uncommitted.Count > 5 ? $" and {Uncommitted.Count - 5} more" : "";
                lines.Add($"{Count(Uncommitted.Count, "uncommitted file")}: {shown}{more}");
            }
            if (UnpushedCount > 0)
            {
                var branches = UnpushedBranches.Count == 0 ? "" : " on " + string.Join(", ", UnpushedBranches);
                lines.Add($"{Count(UnpushedCount, "commit")} not pushed to any remote{branches}");
            }
            if (Stashes.Count > 0)
                lines.Add(Count(Stashes.Count, "stash") + ": " + string.Join("; ", Stashes.Take(3)));
            if (Worktrees.Count > 0)
                lines.Add($"{Count(Worktrees.Count, "other worktree")} that would stop working: " + string.Join(", ", Worktrees));
            lines.AddRange(Problems.Select(p => $"Could not check: {p}"));
            return lines;
        }
    }

    internal static string Count(int n, string noun) =>
        n == 1 ? $"1 {noun}" : $"{n} {noun}{(noun.EndsWith("sh") ? "es" : "s")}";

    /// <summary>
    /// Runs the checks with git. Each one that fails is a problem, so a broken
    /// repository never looks safe to delete.
    /// </summary>
    public static async Task<CloneRemovalCheck> InspectAsync(string path, CancellationToken ct = default)
    {
        var copy = new GitWorkingCopy(path);
        var check = new CloneRemovalCheck { Path = path };

        try
        {
            var snapshot = await copy.StatusAsync(ct: ct);
            check.Uncommitted.AddRange(snapshot.Changes.Where(c => c.Kind != RepoChangeKind.Ignored).Select(c => c.Path));
        }
        catch (RepoException ex)
        {
            check.Problems.Add($"uncommitted changes ({ex.Message})");
        }

        // An empty repository has no commits to lose; rev-list fails on it.
        var hasBranches = (await copy.InspectAsync(new[] { "for-each-ref", "--count=1", "refs/heads" }, ct: ct)).StandardOutput.Trim().Length > 0;
        if (hasBranches)
        {
            var count = await copy.InspectAsync(new[] { "rev-list", "--count", "--branches", "--not", "--remotes" }, ct: ct);
            if (count.Succeeded) check.UnpushedCount = int.TryParse(count.StandardOutput.Trim(), out var n) ? n : 0;
            else check.Problems.Add($"unpushed commits ({count.Message})");
            if (check.UnpushedCount > 0)
            {
                var log = await copy.InspectAsync(new[] { "log", "--branches", "--not", "--remotes", "--format=%h %s", "-n", SampleLimit.ToString() }, ct: ct);
                check.UnpushedSample.AddRange(Lines(log.StandardOutput));
                var refs = await copy.InspectAsync(new[] { "for-each-ref", "--format=%(refname:short)", "refs/heads" }, ct: ct);
                foreach (var branch in Lines(refs.StandardOutput))
                {
                    var ahead = await copy.InspectAsync(new[] { "rev-list", "--count", $"refs/heads/{branch}", "--not", "--remotes" }, ct: ct);
                    if (int.TryParse(ahead.StandardOutput.Trim(), out var a) && a > 0) check.UnpushedBranches.Add(branch);
                }
            }
        }

        var stash = await copy.InspectAsync(new[] { "stash", "list" }, ct: ct);
        if (stash.Succeeded) check.Stashes.AddRange(Lines(stash.StandardOutput));
        else check.Problems.Add($"stashes ({stash.Message})");

        // git lists the main worktree first; the clone is that one, since a
        // linked worktree is never registered. The path test covers a clone
        // reached through another spelling of the same folder.
        var main = Normalize(path);
        check.Worktrees.AddRange((await copy.WorktreesAsync(ct))
            .Skip(1)
            .Where(w => !w.IsBare && !w.IsPrunable)
            .Select(w => w.Path)
            .Where(p => !string.Equals(Normalize(p), main, StringComparison.OrdinalIgnoreCase)));
        return check;
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0);

    private static string Normalize(string path)
    {
        try { return RepoSettings.Normalize(path.Replace('/', System.IO.Path.DirectorySeparatorChar)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return path; }
    }
}

/// <summary>
/// Moves a folder to the Recycle Bin through the shell, so a deleted clone can
/// be restored. Where the drive has no Recycle Bin the shell warns before
/// deleting for good instead of doing it silently.
/// </summary>
public static class RecycleBin
{
    public static void MoveToRecycleBin(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw RepoException.InvalidArgument("Moving to the Recycle Bin needs Windows.");
        var full = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');
        var op = new ShFileOpStruct
        {
            wFunc = FoDelete,
            pFrom = full + "\0\0",
            fFlags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent | FofWantNukeWarning,
        };
        var result = SHFileOperation(ref op);
        if (result != 0 || op.fAnyOperationsAborted || Directory.Exists(full))
            throw RepoException.InvalidArgument($"Could not move {path} to the Recycle Bin (error {result}).");
    }

    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofWantNukeWarning = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct lpFileOp);
}
