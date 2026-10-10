using System.Globalization;

namespace FleetMate.Core.Services.Repos;

/// <summary>
/// Pure parsers for git's machine-readable output. Kept free of process
/// spawning so they are tested against hand-written fixtures.
/// </summary>
public static class GitOutputParser
{
    /// <summary>
    /// Parses <c>git status --porcelain=v2 --branch -z</c>. Records are
    /// NUL-separated; a rename or copy (<c>2 …</c>) is followed by one more
    /// NUL-terminated field holding the original path.
    /// </summary>
    public static GitStatusSnapshot Status(string output)
    {
        var snapshot = new GitStatusSnapshot();
        var fields = new Queue<string>(output.Split('\0', StringSplitOptions.RemoveEmptyEntries));

        while (fields.TryDequeue(out var record))
        {
            if (record.StartsWith("# "))
            {
                Header(record, snapshot);
                continue;
            }
            switch (record[0])
            {
                case '1':
                {
                    // 1 XY sub mH mI mW hH hI path
                    var parts = record.Split(' ', 9);
                    if (parts.Length != 9) continue;
                    snapshot.Changes.Add(new RepoFileChange
                    {
                        Path = parts[8], Kind = RepoChangeKind.Changed,
                        IndexStatus = Letter(parts[1], 0, "."), WorktreeStatus = Letter(parts[1], 1, "."),
                    });
                    break;
                }
                case '2':
                {
                    // 2 XY sub mH mI mW hH hI Xscore path \0 origPath
                    var parts = record.Split(' ', 10);
                    if (parts.Length != 10) continue;
                    fields.TryDequeue(out var original);
                    snapshot.Changes.Add(new RepoFileChange
                    {
                        Path = parts[9], OriginalPath = original, Kind = RepoChangeKind.Renamed,
                        IndexStatus = Letter(parts[1], 0, "."), WorktreeStatus = Letter(parts[1], 1, "."),
                    });
                    break;
                }
                case 'u':
                {
                    // u XY sub m1 m2 m3 mW h1 h2 h3 path
                    var parts = record.Split(' ', 11);
                    if (parts.Length != 11) continue;
                    snapshot.Changes.Add(new RepoFileChange
                    {
                        Path = parts[10], Kind = RepoChangeKind.Unmerged,
                        IndexStatus = Letter(parts[1], 0, "U"), WorktreeStatus = Letter(parts[1], 1, "U"),
                    });
                    break;
                }
                case '?' when record.Length > 2:
                    snapshot.Changes.Add(new RepoFileChange
                        { Path = record[2..], Kind = RepoChangeKind.Untracked, IndexStatus = "?", WorktreeStatus = "?" });
                    break;
                case '!' when record.Length > 2:
                    snapshot.Changes.Add(new RepoFileChange
                        { Path = record[2..], Kind = RepoChangeKind.Ignored, IndexStatus = "!", WorktreeStatus = "!" });
                    break;
            }
        }
        return snapshot;
    }

    private static string Letter(string xy, int index, string fallback) =>
        xy.Length > index ? xy[index].ToString() : fallback;

    private static void Header(string record, GitStatusSnapshot snapshot)
    {
        var body = record[2..];
        var space = body.IndexOf(' ');
        if (space < 0) return;
        var key = body[..space];
        var value = body[(space + 1)..];
        switch (key)
        {
            case "branch.oid":
                snapshot.HeadOid = value == "(initial)" ? null : value;
                break;
            case "branch.head":
                snapshot.Branch = value == "(detached)" ? null : value;
                break;
            case "branch.upstream":
                snapshot.Upstream = value;
                break;
            case "branch.ab":
                foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.StartsWith('+')) snapshot.Ahead = int.TryParse(token[1..], out var a) ? a : 0;
                    if (token.StartsWith('-')) snapshot.Behind = int.TryParse(token[1..], out var b) ? b : 0;
                }
                break;
        }
    }

    /// <summary>Parses <c>git worktree list --porcelain</c>.</summary>
    public static List<RepoWorktree> Worktrees(string output)
    {
        var result = new List<RepoWorktree>();
        foreach (var block in output.Replace("\r\n", "\n").Split("\n\n"))
        {
            string? path = null, head = null, branch = null;
            bool detached = false, bare = false, locked = false, prunable = false;
            foreach (var line in block.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var space = line.IndexOf(' ');
                var key = space < 0 ? line : line[..space];
                var value = space < 0 ? null : line[(space + 1)..];
                switch (key)
                {
                    case "worktree": path = value; break;
                    case "HEAD": head = value; break;
                    case "branch": branch = value == null ? null : RepoKey.ShortBranch(value); break;
                    case "detached": detached = true; break;
                    case "bare": bare = true; break;
                    case "locked": locked = true; break;
                    case "prunable": prunable = true; break;
                }
            }
            if (path != null) result.Add(new RepoWorktree(path, head, branch, detached, bare, locked, prunable));
        }
        return result;
    }

    /// <summary>The <c>--format</c> <see cref="Log"/> expects: fields split by US (0x1f), records by RS (0x1e).</summary>
    public const string LogFormat = "%H%x1f%h%x1f%an%x1f%ae%x1f%aI%x1f%s%x1e";

    public static List<RepoCommit> Log(string output) =>
        output.Split('\u001e', StringSplitOptions.RemoveEmptyEntries)
            .Select(record => record.Trim().Split('\u001f'))
            .Where(fields => fields.Length == 6)
            .Select(f => new RepoCommit(f[0], f[1], f[2], f[3], ParseDate(f[4]), f[5]))
            .ToList();

    /// <summary>Parses <c>git grep -n -z --column</c>: <c>path\0line\0column\0text</c> per line.</summary>
    public static List<RepoGrepMatch> Grep(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\0', 4))
            .Where(p => p.Length == 4 && int.TryParse(p[1], out _) && int.TryParse(p[2], out _))
            .Select(p => new RepoGrepMatch(p[0], int.Parse(p[1]), int.Parse(p[2]), p[3]))
            .ToList();

    /// <summary>NUL-separated path list (<c>git ls-files -z</c>).</summary>
    public static List<string> Paths(string output) =>
        output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();

    internal static DateTimeOffset? ParseDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
