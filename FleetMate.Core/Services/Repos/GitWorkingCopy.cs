using System.Diagnostics;
using System.Text;

namespace FleetMate.Core.Services.Repos;

/// <summary>What one git process printed and how it exited.</summary>
public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>The error text, falling back to standard output when git wrote nothing to stderr.</summary>
    public string Message => StandardError.Trim().Length > 0 ? StandardError.Trim() : StandardOutput.Trim();
}

/// <summary>
/// Git operations on one local checkout. Authentication is left entirely to
/// the user's git setup — credential manager, <c>gh auth setup-git</c>, ssh
/// keys — and no token ever appears in a URL or argument.
///
/// This is the surface both <c>fleetmate repos</c> and the app's Repos view
/// use: status and per-file changes, staging, diffs, the file list, file
/// contents, search, and the network operations. Nothing here rewrites or
/// throws away work unless it is called with explicit paths
/// (<see cref="Discard"/>), and commit and push refuse protected branches
/// unless the caller explicitly allows them.
/// </summary>
public sealed class GitWorkingCopy
{
    /// <summary>Absolute path of the checkout's top level.</summary>
    public string Path { get; }

    public GitWorkingCopy(string path)
    {
        Path = RepoSettings.Normalize(path);
    }

    /// <summary>Whether <see cref="Path"/> is inside a git work tree.</summary>
    public async Task<bool> IsRepositoryAsync(CancellationToken ct = default) =>
        (await GitAsync(new[] { "rev-parse", "--is-inside-work-tree" }, ct: ct)).StandardOutput.StartsWith("true");

    /// <summary>
    /// <c>AGENTS.md</c> at the top level, when present. Agents read it before
    /// working in the repository.
    /// </summary>
    public string? AgentsFile
    {
        get
        {
            var candidate = System.IO.Path.Combine(Path, "AGENTS.md");
            return File.Exists(candidate) ? candidate : null;
        }
    }

    // ── Status ──────────────────────────────────────────────────────────

    public async Task<GitStatusSnapshot> StatusAsync(bool includeIgnored = false, CancellationToken ct = default)
    {
        var args = new List<string> { "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all" };
        if (includeIgnored) args.Add("--ignored");
        // Optional locks off: a status poll must never take the index lock
        // from under a commit running in a terminal.
        var output = await CheckedAsync(args, OptionalLocksOff, ct);
        return GitOutputParser.Status(output);
    }

    public async Task<List<RepoWorktree>> WorktreesAsync(CancellationToken ct = default)
    {
        var result = await GitAsync(new[] { "worktree", "list", "--porcelain" }, ct: ct);
        return result.Succeeded ? GitOutputParser.Worktrees(result.StandardOutput) : new();
    }

    public async Task<string?> CurrentBranchAsync(CancellationToken ct = default)
    {
        var result = await GitAsync(new[] { "symbolic-ref", "--quiet", "--short", "HEAD" }, ct: ct);
        var branch = result.StandardOutput.Trim();
        return result.Succeeded && branch.Length > 0 ? branch : null;
    }

    public async Task<string?> OriginUrlAsync(CancellationToken ct = default)
    {
        var result = await GitAsync(new[] { "config", "--get", "remote.origin.url" }, ct: ct);
        var url = result.StandardOutput.Trim();
        return result.Succeeded && url.Length > 0 ? url : null;
    }

    /// <summary>When HEAD was last committed, or null for an empty repository.</summary>
    public async Task<DateTimeOffset?> LastCommitDateAsync(CancellationToken ct = default)
    {
        var result = await GitAsync(new[] { "log", "-1", "--format=%ct" }, OptionalLocksOff, ct);
        return result.Succeeded && long.TryParse(result.StandardOutput.Trim(), out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime()
            : null;
    }

    // ── Network ─────────────────────────────────────────────────────────

    public Task<string> FetchAsync(CancellationToken ct = default) =>
        CheckedAsync(new[] { "fetch", "--prune", "origin" }, ct: ct);

    /// <summary>Fast-forward only: a pull never creates a merge commit behind the user's back.</summary>
    public Task<string> PullAsync(CancellationToken ct = default) =>
        CheckedAsync(new[] { "pull", "--ff-only" }, ct: ct);

    /// <summary>
    /// Pushes the current branch, setting its upstream on first push. Refuses
    /// a protected branch unless <paramref name="allowProtected"/>. Never forces.
    /// </summary>
    public async Task<string> PushAsync(IReadOnlySet<string> protectedBranches, bool allowProtected = false, CancellationToken ct = default)
    {
        var branch = await CurrentBranchAsync(ct)
            ?? throw RepoException.InvalidArgument("HEAD is detached; switch to a branch before pushing.");
        GuardBranch(branch, protectedBranches, allowProtected);
        var snapshot = await StatusAsync(ct: ct);
        return snapshot.Upstream == null
            ? await CheckedAsync(new[] { "push", "--set-upstream", "origin", branch }, ct: ct)
            : await CheckedAsync(new[] { "push" }, ct: ct);
    }

    // ── Branches ────────────────────────────────────────────────────────

    /// <summary>
    /// Switches to <paramref name="name"/>, creating it from
    /// <paramref name="startPoint"/> (default: HEAD) when it does not exist locally.
    /// Git refuses the switch when it would overwrite local changes.
    /// </summary>
    public async Task<string> SwitchBranchAsync(string name, bool? create = null, string? startPoint = null, CancellationToken ct = default)
    {
        ValidateRefName(name);
        if (startPoint != null && (startPoint.StartsWith('-') || startPoint.Contains(' ')))
            throw RepoException.InvalidArgument($"'{startPoint}' is not a valid start point.");
        var exists = (await GitAsync(new[] { "show-ref", "--verify", "--quiet", $"refs/heads/{name}" }, ct: ct)).Succeeded;
        if (create ?? !exists)
        {
            var args = new List<string> { "switch", "-c", name };
            if (startPoint != null) args.Add(startPoint);
            return await CheckedAsync(args, ct: ct);
        }
        return await CheckedAsync(new[] { "switch", name }, ct: ct);
    }

    public async Task<List<string>> LocalBranchesAsync(CancellationToken ct = default) =>
        (await CheckedAsync(new[] { "for-each-ref", "--format=%(refname:short)", "refs/heads" }, ct: ct))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // ── Staging and commits ─────────────────────────────────────────────

    public async Task StageAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0) return;
        await CheckedAsync(new[] { "add", "--" }.Concat(Confined(paths)), ct: ct);
    }

    public Task StageAllAsync(CancellationToken ct = default) =>
        CheckedAsync(new[] { "add", "--all" }, ct: ct);

    public async Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0) return;
        await CheckedAsync(new[] { "restore", "--staged", "--" }.Concat(Confined(paths)), ct: ct);
    }

    /// <summary>
    /// Throws away local changes to <paramref name="paths"/>: tracked files
    /// return to the index's content, untracked files are deleted. Destructive
    /// by design, so it only ever takes explicit paths, and the app asks first.
    /// </summary>
    public async Task DiscardAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0) return;
        var safe = Confined(paths);
        var snapshot = await StatusAsync(ct: ct);
        var untracked = snapshot.Changes.Where(c => c.Kind == RepoChangeKind.Untracked).Select(c => c.Path).ToHashSet();
        var tracked = safe.Where(p => !untracked.Contains(p)).ToList();
        var fresh = safe.Where(untracked.Contains).ToList();
        if (tracked.Count > 0) await CheckedAsync(new[] { "restore", "--worktree", "--" }.Concat(tracked), ct: ct);
        if (fresh.Count > 0) await CheckedAsync(new[] { "clean", "-f", "--" }.Concat(fresh), ct: ct);
    }

    /// <summary>
    /// Commits all changes (<paramref name="paths"/> empty) or only
    /// <paramref name="paths"/>, refusing a protected branch unless
    /// <paramref name="allowProtected"/>. Returns the new commit.
    /// </summary>
    public async Task<RepoCommit> CommitAsync(string message, IReadOnlyList<string>? paths, IReadOnlySet<string> protectedBranches,
        bool allowProtected = false, CancellationToken ct = default)
    {
        var trimmed = message.Trim();
        if (trimmed.Length == 0) throw RepoException.InvalidArgument("A commit message is required.");
        if (await CurrentBranchAsync(ct) is { } branch) GuardBranch(branch, protectedBranches, allowProtected);
        paths ??= Array.Empty<string>();
        if (paths.Count == 0) await StageAllAsync(ct);
        else await StageAsync(paths, ct);
        if ((await StatusAsync(ct: ct)).StagedCount == 0) throw RepoException.NothingToCommit();

        var args = new List<string> { "commit", "-m", trimmed };
        if (paths.Count > 0) { args.Add("--"); args.AddRange(Confined(paths)); }
        await CheckedAsync(args, ct: ct);
        return (await LogAsync(1, ct: ct)).FirstOrDefault()
            ?? throw RepoException.GitFailed("log", "no commit after commit");
    }

    // ── Diff and history ────────────────────────────────────────────────

    /// <summary>
    /// <c>git diff</c> of the worktree against the index, or of the index
    /// against HEAD when <paramref name="staged"/>. Limited to
    /// <paramref name="paths"/> when given.
    /// </summary>
    public Task<string> DiffAsync(bool staged = false, bool stat = false, IReadOnlyList<string>? paths = null, CancellationToken ct = default)
    {
        var args = new List<string> { "diff", "--no-color", "--no-ext-diff" };
        if (staged) args.Add("--cached");
        if (stat) args.Add("--stat");
        if (paths is { Count: > 0 }) { args.Add("--"); args.AddRange(Confined(paths)); }
        return CheckedAsync(args, ct: ct);
    }

    /// <summary>
    /// Diff for one file as the Repos view shows it. An untracked file has no
    /// index entry, so it is diffed against an empty file.
    /// </summary>
    public async Task<string> FileDiffAsync(string path, bool staged = false, CancellationToken ct = default)
    {
        var safe = ConfinedPath(path);
        var snapshot = await StatusAsync(ct: ct);
        if (!staged && snapshot.Changes.Any(c => c.Kind == RepoChangeKind.Untracked && c.Path == safe))
        {
            // --no-index exits 1 when the files differ, which is the expected case.
            var result = await GitAsync(new[] { "diff", "--no-color", "--no-index", "--", "/dev/null", safe }, ct: ct);
            return result.StandardOutput;
        }
        return await DiffAsync(staged, paths: new[] { safe }, ct: ct);
    }

    public async Task<List<RepoCommit>> LogAsync(int limit = 20, string? reference = null, CancellationToken ct = default)
    {
        var args = new List<string> { "log", "-n", Math.Max(1, limit).ToString(), $"--format={GitOutputParser.LogFormat}" };
        if (reference != null)
        {
            if (reference.StartsWith('-')) throw RepoException.InvalidArgument($"'{reference}' is not a valid ref.");
            args.Add(reference);
            args.Add("--");
        }
        var result = await GitAsync(args, ct: ct);
        // A repository with no commits yet has no log; that is not an error.
        if (!result.Succeeded && result.StandardError.Contains("does not have any commits")) return new();
        if (!result.Succeeded) throw RepoException.GitFailed("log", result.Message);
        return GitOutputParser.Log(result.StandardOutput);
    }

    // ── Files and search ────────────────────────────────────────────────

    /// <summary>
    /// Every file in the checkout git would consider: tracked files plus
    /// untracked ones not excluded by <c>.gitignore</c>. Paths are relative,
    /// with forward slashes.
    /// </summary>
    public async Task<List<string>> ListFilesAsync(CancellationToken ct = default)
    {
        var output = await CheckedAsync(new[] { "ls-files", "--cached", "--others", "--exclude-standard", "-z" }, ct: ct);
        // Deleted-but-tracked files still appear in --cached; keep only what exists.
        var seen = new HashSet<string>();
        return GitOutputParser.Paths(output).Where(p => seen.Add(p) && File.Exists(Absolute(p))).ToList();
    }

    /// <summary>
    /// <c>git grep</c> across tracked files (and untracked, non-ignored ones
    /// when <paramref name="includeUntracked"/>). Binary files are skipped.
    /// </summary>
    public async Task<List<RepoGrepMatch>> GrepAsync(string pattern, bool ignoreCase = false, bool fixedStrings = false,
        bool includeUntracked = true, int limit = 1000, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(pattern)) throw RepoException.InvalidArgument("A search pattern is required.");
        var args = new List<string> { "grep", "-n", "-z", "--column", "-I", "--no-color" };
        if (ignoreCase) args.Add("-i");
        if (fixedStrings) args.Add("-F");
        if (includeUntracked) args.Add("--untracked");
        args.Add("-e");
        args.Add(pattern);
        var result = await GitAsync(args, ct: ct);
        // Exit 1 means no match.
        if (result.ExitCode == 1) return new();
        if (!result.Succeeded) throw RepoException.GitFailed("grep", result.Message);
        return GitOutputParser.Grep(result.StandardOutput).Take(limit).ToList();
    }

    public byte[] ReadFile(string relative) => File.ReadAllBytes(Absolute(ConfinedPath(relative)));

    public void WriteFile(string relative, byte[] contents)
    {
        var target = Absolute(ConfinedPath(relative));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        var temp = target + ".fleetmate-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(temp, contents);
        File.Move(temp, target, overwrite: true);
    }

    // ── Path safety ─────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a repository-relative path to its git form (forward slashes),
    /// rejecting anything that would land outside the checkout (<c>..</c>,
    /// absolute paths elsewhere, links out) or inside <c>.git</c> in any letter
    /// case or Windows alias of it (<c>.git.</c>, <c>GIT~1</c>), and alternate
    /// data streams. A path that does not exist yet is checked through its
    /// deepest existing ancestor, so a new file under a linked folder cannot
    /// escape either.
    /// </summary>
    public string ConfinedPath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) throw RepoException.InvalidArgument("Expected a file path inside the repository.");
        var root = RealPath(Path);
        string candidate;
        try
        {
            candidate = System.IO.Path.IsPathRooted(relative)
                ? System.IO.Path.GetFullPath(relative)
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw RepoException.PathOutsideRepository(relative);
        }
        var resolved = RealPath(candidate);
        var prefix = root.EndsWith('\\') ? root : root + "\\";
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(resolved, root, StringComparison.OrdinalIgnoreCase))
                throw RepoException.InvalidArgument("Expected a file path inside the repository.");
            throw RepoException.PathOutsideRepository(relative);
        }
        var inside = resolved[prefix.Length..].Trim('\\');
        if (inside.Length == 0) throw RepoException.InvalidArgument("Expected a file path inside the repository.");
        foreach (var component in inside.Split('\\'))
        {
            var bare = component.TrimEnd('.', ' ');
            if (bare.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || bare.StartsWith("git~", StringComparison.OrdinalIgnoreCase)
                || component.Contains(':'))
            {
                throw RepoException.PathOutsideRepository(relative);
            }
        }
        return inside.Replace('\\', '/');
    }

    /// <summary>
    /// The path with every symbolic link and junction resolved. Components that
    /// do not exist yet are appended to the resolved form of the deepest
    /// ancestor that does.
    /// </summary>
    internal static string RealPath(string fullPath)
    {
        var root = System.IO.Path.GetPathRoot(fullPath) ?? "";
        var parts = fullPath[root.Length..].Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var next = System.IO.Path.Combine(current, parts[i]);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (!info.Exists)
            {
                // Nothing below a missing component can be a link.
                return System.IO.Path.Combine(new[] { next }.Concat(parts[(i + 1)..]).ToArray());
            }
            if (info.LinkTarget != null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                next = target != null ? RealPath(System.IO.Path.GetFullPath(target.FullName)) : next;
            }
            current = next;
        }
        return current;
    }

    internal List<string> Confined(IEnumerable<string> paths) => paths.Select(ConfinedPath).ToList();

    internal string Absolute(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', '\\'));

    // ── Running git ─────────────────────────────────────────────────────

    private static readonly IReadOnlyDictionary<string, string> OptionalLocksOff =
        new Dictionary<string, string> { ["GIT_OPTIONAL_LOCKS"] = "0" };

    /// <summary>Runs git in this checkout.</summary>
    public Task<GitResult> GitAsync(IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? extraEnvironment = null, CancellationToken ct = default) =>
        RunGitAsync(new[] { "-C", Path }.Concat(arguments), extraEnvironment, ct);

    /// <summary>
    /// Runs git with prompts disabled — with no terminal to answer them, a
    /// credential prompt would otherwise hang the call — literal pathspecs and
    /// unquoted paths. Output is read as UTF-8.
    /// </summary>
    public static async Task<GitResult> RunGitAsync(IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? extraEnvironment = null, CancellationToken ct = default)
    {
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.quotepath=off");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // Paths are file names, never pathspec magic: a file named ":/" must
        // not widen a discard or stage to the whole checkout.
        start.Environment["GIT_LITERAL_PATHSPECS"] = "1";
        if (extraEnvironment != null)
            foreach (var (key, value) in extraEnvironment) start.Environment[key] = value;

        Process process;
        try
        {
            process = Process.Start(start) ?? throw RepoException.GitFailed("start", "git could not be started");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw RepoException.GitFailed("start", "git was not found. Install Git for Windows and make sure it is on PATH.");
        }
        using (process)
        {
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }
            return new GitResult(process.ExitCode, await stdout, await stderr);
        }
    }

    internal async Task<string> CheckedAsync(IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? extraEnvironment = null, CancellationToken ct = default)
    {
        var list = arguments.ToList();
        var result = await GitAsync(list, extraEnvironment, ct);
        if (!result.Succeeded)
        {
            if (result.Message.Contains("not a git repository")) throw RepoException.NotAGitRepository(Path);
            throw RepoException.GitFailed(list.FirstOrDefault() ?? "", result.Message);
        }
        // Network commands report progress on stderr; keep it for the caller.
        return result.StandardOutput.Length == 0 ? result.StandardError : result.StandardOutput;
    }

    internal static void GuardBranch(string branch, IReadOnlySet<string> protectedBranches, bool allow)
    {
        if (!allow && protectedBranches.Contains(branch)) throw RepoException.ProtectedBranch(branch);
    }

    internal static void ValidateRefName(string name)
    {
        if (name.Length == 0 || name.StartsWith('-') || name.Contains(' ') || name.Contains("..") || name.EndsWith('/'))
            throw RepoException.InvalidArgument($"'{name}' is not a valid branch name.");
    }
}
