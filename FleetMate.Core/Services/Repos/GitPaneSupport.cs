using System.Globalization;
using System.Text;
using FleetMate.Core.Shared;

namespace FleetMate.Core.Services.Repos;

// The data side of the Repos workspace's git pane, ported from FleetMate for
// Mac, which adapted it from MunkiStudio (Apache-2.0): the status, commit, ref
// and branch models, the log and branch parsing, the commit-graph lane builder
// and the single-hunk patch. Git runs through GitWorkingCopy, and FleetMate's
// guard holds: commit (including amend) and push refuse protected branches.

public enum GitEntryKind { Modified, Added, Deleted, Untracked, Ignored, Conflicted, Renamed, Copied }

/// <summary>
/// One row of the git pane's change list. A file with both staged and
/// unstaged edits appears twice, once per side.
/// </summary>
public sealed record GitStatusEntry(string RelativePath, GitEntryKind Kind, bool Staged, string? OriginalPath = null)
{
    /// <summary>Unique across both sides of the same path.</summary>
    public string Id => (Staged ? "staged:" : "work:") + RelativePath;

    /// <summary>The one-letter code the list shows.</summary>
    public string Letter => Kind switch
    {
        GitEntryKind.Modified => "M",
        GitEntryKind.Added => "A",
        GitEntryKind.Deleted => "D",
        GitEntryKind.Untracked => "U",
        GitEntryKind.Renamed => "R",
        GitEntryKind.Copied => "C",
        GitEntryKind.Conflicted => "!",
        _ => "?",
    };

    /// <summary>Entries for a status snapshot, staged side first for each path.</summary>
    public static List<GitStatusEntry> Entries(GitStatusSnapshot snapshot)
    {
        var result = new List<GitStatusEntry>();
        foreach (var change in snapshot.Changes)
        {
            switch (change.Kind)
            {
                case RepoChangeKind.Ignored:
                    continue;
                case RepoChangeKind.Untracked:
                    result.Add(new GitStatusEntry(change.Path, GitEntryKind.Untracked, false));
                    break;
                case RepoChangeKind.Unmerged:
                    result.Add(new GitStatusEntry(change.Path, GitEntryKind.Conflicted, false));
                    break;
                default:
                    if (change.IndexStatus != "." && KindOf(change.IndexStatus) is { } staged)
                        result.Add(new GitStatusEntry(change.Path, staged, true, change.OriginalPath));
                    if (change.WorktreeStatus != "." && KindOf(change.WorktreeStatus) is { } work)
                        result.Add(new GitStatusEntry(change.Path, work, false));
                    break;
            }
        }
        return result;
    }

    private static GitEntryKind? KindOf(string letter) => letter switch
    {
        "M" or "T" => GitEntryKind.Modified,
        "A" => GitEntryKind.Added,
        "D" => GitEntryKind.Deleted,
        "R" => GitEntryKind.Renamed,
        "C" => GitEntryKind.Copied,
        "U" => GitEntryKind.Conflicted,
        _ => null,
    };
}

public enum GitRefKind { Head, LocalBranch, RemoteBranch, Tag }

/// <summary>A branch, tag or HEAD decoration on a commit.</summary>
public sealed record RepoGitRef(string Name, GitRefKind Kind, bool IsHead = false)
{
    /// <summary>Parses a <c>%D</c> decoration written with <c>--decorate=full</c>.</summary>
    public static List<RepoGitRef> Parse(string raw)
    {
        var refs = new List<RepoGitRef>();
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return refs;
        foreach (var token in trimmed.Split(", "))
        {
            var value = token.Trim();
            var isHead = false;
            if (value == "HEAD")
            {
                refs.Add(new RepoGitRef("HEAD", GitRefKind.Head, true));
                continue;
            }
            if (value.StartsWith("HEAD -> "))
            {
                isHead = true;
                value = value["HEAD -> ".Length..];
            }
            if (value.StartsWith("tag: "))
            {
                var name = value["tag: ".Length..];
                refs.Add(new RepoGitRef(name.StartsWith("refs/tags/") ? name["refs/tags/".Length..] : name, GitRefKind.Tag));
            }
            else if (value.StartsWith("refs/heads/"))
                refs.Add(new RepoGitRef(value["refs/heads/".Length..], GitRefKind.LocalBranch, isHead));
            else if (value.StartsWith("refs/remotes/"))
                refs.Add(new RepoGitRef(value["refs/remotes/".Length..], GitRefKind.RemoteBranch, isHead));
            else if (value.StartsWith("refs/tags/"))
                refs.Add(new RepoGitRef(value["refs/tags/".Length..], GitRefKind.Tag));
            else if (value.Length > 0)
                refs.Add(new RepoGitRef(value, value.Contains('/') ? GitRefKind.RemoteBranch : GitRefKind.LocalBranch, isHead));
        }
        return refs;
    }
}

/// <summary>A commit in the History panel, with the parents the lane graph needs.</summary>
public sealed record GitCommit(string Sha, string Subject, string Author, DateTimeOffset Date,
    IReadOnlyList<string> Parents, IReadOnlyList<RepoGitRef> Refs)
{
    public string ShortSha => Sha.Length > 7 ? Sha[..7] : Sha;

    /// <summary>The <c>--pretty=format:</c> argument <see cref="ParseLog"/> reads.</summary>
    public const string LogFormat = "--pretty=format:%H%x1f%an%x1f%aI%x1f%P%x1f%D%x1f%s%x1e";

    public static List<GitCommit> ParseLog(string output)
    {
        var commits = new List<GitCommit>();
        foreach (var raw in output.Split('\u001e'))
        {
            var record = raw.TrimStart('\n', '\r');
            if (record.Length == 0) continue;
            var parts = record.Split('\u001f');
            if (parts.Length != 6
                || !DateTimeOffset.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            commits.Add(new GitCommit(parts[0], parts[5], parts[1], date,
                parts[3].Split(' ', StringSplitOptions.RemoveEmptyEntries), RepoGitRef.Parse(parts[4])));
        }
        return commits;
    }
}

public sealed record GitBranch(string Name, bool IsCurrent, string? UpstreamName = null)
{
    /// <summary><c>git branch --format=%(refname:short)|%(upstream:short)|%(HEAD)</c>.</summary>
    public const string ListFormat = "--format=%(refname:short)|%(upstream:short)|%(HEAD)";

    public static List<GitBranch> Parse(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('|'))
            .Where(p => p.Length > 0 && p[0].Length > 0)
            .Select(p => new GitBranch(p[0], p.Length > 2 && p[2] == "*", p.Length > 1 && p[1].Length > 0 ? p[1] : null))
            .ToList();
}

// ── Commit graph ────────────────────────────────────────────────────────

/// <summary>
/// One drawn segment of a commit-graph row. Upper-half segments span the
/// row's top edge to its middle; the rest span middle to bottom.
/// </summary>
public readonly record struct GraphSegment(int FromColumn, int ToColumn, bool UpperHalf, int ColorIndex);

/// <summary>Per-commit graph geometry, parallel to the commit list.</summary>
public sealed record GraphRow(int DotColumn, int DotColorIndex, IReadOnlyList<GraphSegment> Segments);

public static class CommitGraphBuilder
{
    /// <summary>
    /// Lane geometry for <paramref name="commits"/> (newest first). Lanes keep
    /// their column for life, so passing lines stay vertical.
    /// </summary>
    public static (List<GraphRow> Rows, int LaneCount) Build(IReadOnlyList<GitCommit> commits)
    {
        var lanes = new List<string?>();
        var laneColor = new List<int>();
        var rows = new List<GraphRow>();
        var nextColor = 0;
        var laneCount = 1;

        int FreeColumn()
        {
            var i = lanes.IndexOf(null);
            if (i >= 0) return i;
            lanes.Add(null);
            laneColor.Add(0);
            return lanes.Count - 1;
        }

        foreach (var commit in commits)
        {
            var entry = lanes.ToList();
            var occupied = Enumerable.Range(0, entry.Count).Where(i => entry[i] == commit.Sha).ToList();
            int col;
            if (occupied.Count > 0)
            {
                col = occupied[0];
            }
            else
            {
                col = FreeColumn();
                lanes[col] = commit.Sha;
                laneColor[col] = nextColor++;
            }
            var dotColor = laneColor[col];

            var segments = new List<GraphSegment>();
            for (var i = 0; i < entry.Count; i++)
            {
                if (entry[i] is not { } sha) continue;
                segments.Add(new GraphSegment(i, sha == commit.Sha ? col : i, true, laneColor[i]));
            }

            foreach (var i in occupied) lanes[i] = null;
            var parents = commit.Parents;
            lanes[col] = parents.Count > 0 ? parents[0] : null;
            var fromDot = new HashSet<int> { col };
            foreach (var parent in parents.Skip(1))
            {
                var existing = lanes.IndexOf(parent);
                if (existing >= 0)
                {
                    fromDot.Add(existing);
                }
                else
                {
                    var nc = FreeColumn();
                    lanes[nc] = parent;
                    laneColor[nc] = nextColor++;
                    fromDot.Add(nc);
                }
            }

            for (var i = 0; i < lanes.Count; i++)
            {
                if (lanes[i] == null) continue;
                if (i == col)
                {
                    if (parents.Count > 0) segments.Add(new GraphSegment(col, col, false, dotColor));
                }
                else if (fromDot.Contains(i))
                {
                    segments.Add(new GraphSegment(col, i, false, laneColor[i]));
                }
                else
                {
                    segments.Add(new GraphSegment(i, i, false, laneColor[i]));
                }
            }

            rows.Add(new GraphRow(col, dotColor, segments));
            laneCount = Math.Max(laneCount, lanes.Count);
        }
        return (rows, laneCount);
    }
}

// ── Hunk patches ────────────────────────────────────────────────────────

public static class DiffHunkPatch
{
    /// <summary>
    /// A patch holding only <paramref name="hunk"/>, for <c>git apply</c>
    /// (optionally <c>--cached</c> and/or <c>--reverse</c>) to stage, unstage
    /// or discard one chunk.
    /// </summary>
    public static string PatchForHunk(this DiffFile file, DiffHunk hunk)
    {
        var builder = new StringBuilder(string.Join("\n", file.HeaderLines));
        if (builder.Length > 0 && builder[^1] != '\n') builder.Append('\n');
        builder.Append(hunk.Header).Append('\n');
        foreach (var line in hunk.Lines)
        {
            builder.Append(line.Kind switch
            {
                DiffLineKind.Addition => "+",
                DiffLineKind.Deletion => "-",
                DiffLineKind.NoNewline => "\\",
                _ => " ",
            }).Append(line.Content).Append('\n');
        }
        return builder.ToString();
    }
}

// ── Git operations ──────────────────────────────────────────────────────

public static class GitPaneOperations
{
    public static async Task<List<GitStatusEntry>> StatusEntriesAsync(this GitWorkingCopy copy, CancellationToken ct = default) =>
        GitStatusEntry.Entries(await copy.StatusAsync(ct: ct));

    public static async Task<List<GitBranch>> BranchListAsync(this GitWorkingCopy copy, CancellationToken ct = default) =>
        GitBranch.Parse(await copy.CheckedAsync(new[] { "branch", GitBranch.ListFormat }, ct: ct));

    /// <summary>Every branch's history, newest first, in topological order so the lane graph draws cleanly.</summary>
    public static async Task<List<GitCommit>> HistoryAsync(this GitWorkingCopy copy, int limit = 200, CancellationToken ct = default)
    {
        var result = await copy.GitAsync(new[]
            { "log", "--all", "--topo-order", "--decorate=full", $"--max-count={Math.Max(1, limit)}", GitCommit.LogFormat }, ct: ct);
        if (!result.Succeeded && result.StandardError.Contains("does not have any commits")) return new();
        if (!result.Succeeded) throw RepoException.GitFailed("log", result.Message);
        return GitCommit.ParseLog(result.StandardOutput);
    }

    /// <summary><c>git show --stat --patch</c> for the History detail pane.</summary>
    public static Task<string> ShowAsync(this GitWorkingCopy copy, string sha, CancellationToken ct = default)
    {
        ValidateRevision(sha);
        return copy.CheckedAsync(new[] { "show", "--stat", "--patch", "--no-color", "--no-ext-diff", sha }, ct: ct);
    }

    /// <summary><c>git format-patch</c> of one commit, for Copy as Patch.</summary>
    public static Task<string> FormatPatchAsync(this GitWorkingCopy copy, string sha, CancellationToken ct = default)
    {
        ValidateRevision(sha);
        return copy.CheckedAsync(new[] { "format-patch", "-1", "--stdout", sha }, ct: ct);
    }

    /// <summary>
    /// Staged and unstaged changes to one path against HEAD; an untracked
    /// file is shown as wholly added.
    /// </summary>
    public static async Task<string> CombinedDiffAsync(this GitWorkingCopy copy, string relativePath, CancellationToken ct = default)
    {
        var safe = copy.ConfinedPath(relativePath);
        var result = await copy.GitAsync(new[] { "diff", "--no-color", "--no-ext-diff", "HEAD", "--", safe }, ct: ct);
        if (result.Succeeded && result.StandardOutput.Length > 0) return result.StandardOutput;
        return await copy.FileDiffAsync(safe, ct: ct);
    }

    /// <summary>
    /// Applies a patch to the working tree, or the index when
    /// <paramref name="cached"/>; <paramref name="reverse"/> undoes it. The
    /// basis for staging, unstaging and discarding single chunks.
    /// </summary>
    public static async Task ApplyPatchAsync(this GitWorkingCopy copy, string patch, bool cached, bool reverse, CancellationToken ct = default)
    {
        var file = Path.Combine(Path.GetTempPath(), $"fleetmate-hunk-{Guid.NewGuid():N}.patch");
        await File.WriteAllTextAsync(file, patch, new UTF8Encoding(false), ct);
        try
        {
            var args = new List<string> { "apply", "--whitespace=nowarn" };
            if (cached) args.Add("--cached");
            if (reverse) args.Add("--reverse");
            args.Add(file);
            await copy.CheckedAsync(args, ct: ct);
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Commits what is staged with a subject and optional body.
    /// <paramref name="amend"/> rewrites HEAD; <paramref name="runHooks"/> false
    /// passes <c>--no-verify</c>. Refuses a protected branch unless
    /// <paramref name="allowProtected"/>. Returns the new HEAD.
    /// </summary>
    public static async Task<RepoCommit> CommitAsync(this GitWorkingCopy copy, string subject, string? body, bool amend, bool runHooks,
        IReadOnlySet<string> protectedBranches, bool allowProtected = false, CancellationToken ct = default)
    {
        var trimmed = subject.Trim();
        if (trimmed.Length == 0) throw RepoException.InvalidArgument("A commit subject is required.");
        if (await copy.CurrentBranchAsync(ct) is { } branch) GitWorkingCopy.GuardBranch(branch, protectedBranches, allowProtected);
        if (!amend && (await copy.StatusAsync(ct: ct)).StagedCount == 0) throw RepoException.NothingToCommit();
        var args = new List<string> { "commit", "-m", trimmed };
        if (body?.Trim() is { Length: > 0 } text) { args.Add("-m"); args.Add(text); }
        if (amend) args.Add("--amend");
        if (!runHooks) args.Add("--no-verify");
        await copy.CheckedAsync(args, ct: ct);
        return (await copy.LogAsync(1, ct: ct)).FirstOrDefault() ?? throw RepoException.GitFailed("log", "no commit after commit");
    }

    /// <summary>
    /// Commits exactly what is staged, leaving unstaged changes alone. A
    /// staged rename or deletion commits whole, since nothing is re-staged by path.
    /// </summary>
    public static Task<RepoCommit> CommitStagedAsync(this GitWorkingCopy copy, string message,
        IReadOnlySet<string> protectedBranches, bool allowProtected = false, CancellationToken ct = default) =>
        copy.CommitAsync(message, null, amend: false, runHooks: true, protectedBranches, allowProtected, ct);

    public static async Task TagAsync(this GitWorkingCopy copy, string name, string sha, string? message, CancellationToken ct = default)
    {
        ValidateRevision(sha);
        if (name.Length == 0 || name.StartsWith('-') || name.Contains(' ') || name.Contains(".."))
            throw RepoException.InvalidArgument($"'{name}' is not a valid tag name.");
        if (!string.IsNullOrWhiteSpace(message))
            await copy.CheckedAsync(new[] { "tag", "-a", name, "-m", message, sha }, ct: ct);
        else
            await copy.CheckedAsync(new[] { "tag", name, sha }, ct: ct);
    }

    /// <summary>Creates a branch at <paramref name="sha"/> without switching to it.</summary>
    public static async Task CreateBranchAsync(this GitWorkingCopy copy, string name, string sha, CancellationToken ct = default)
    {
        ValidateRevision(sha);
        GitWorkingCopy.ValidateRefName(name);
        await copy.CheckedAsync(new[] { "branch", name, sha }, ct: ct);
    }

    /// <summary>Detaches HEAD at a commit. Git refuses when it would overwrite local changes.</summary>
    public static async Task CheckoutCommitAsync(this GitWorkingCopy copy, string sha, CancellationToken ct = default)
    {
        ValidateRevision(sha);
        await copy.CheckedAsync(new[] { "switch", "--detach", sha }, ct: ct);
    }

    public static async Task CherryPickAsync(this GitWorkingCopy copy, string sha, CancellationToken ct = default)
    {
        ValidateRevision(sha);
        await copy.CheckedAsync(new[] { "cherry-pick", sha }, ct: ct);
    }

    /// <summary>Adds a commit that undoes <paramref name="sha"/>; history is not rewritten.</summary>
    public static async Task RevertAsync(this GitWorkingCopy copy, string sha, CancellationToken ct = default)
    {
        ValidateRevision(sha);
        await copy.CheckedAsync(new[] { "revert", "--no-edit", sha }, ct: ct);
    }

    internal static void ValidateRevision(string sha)
    {
        if (sha.Length == 0 || sha.StartsWith('-') || !sha.All(Uri.IsHexDigit))
            throw RepoException.InvalidArgument($"'{sha}' is not a commit hash.");
    }
}
