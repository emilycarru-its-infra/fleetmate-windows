namespace FleetMate.Core.Services.Repos;

/// <summary>A git checkout found on disk.</summary>
public sealed record DiscoveredCheckout(string Path, string? RemoteUrl, RepoKey? Key);

/// <summary>
/// Walks scan roots for git checkouts.
///
/// A folder is a checkout when it holds <c>.git</c> (a directory, or a file
/// for a submodule). The walk stops descending at a checkout, skips hidden
/// folders, links and junctions, and the configured skip list
/// (<c>.worktrees</c>, <c>node_modules</c>, <c>bin</c>, …), and never goes
/// deeper than the configured depth below a root. Linked worktrees are not
/// reported: they belong to the checkout they were added from.
/// </summary>
public static class RepoDiscovery
{
    /// <summary>Paths of checkouts under <paramref name="roots"/>, found without spawning a process.</summary>
    public static List<string> FindCheckouts(IEnumerable<string> roots, int maxDepth, IEnumerable<string> skip)
    {
        var skipSet = new HashSet<string>(skip, StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();

        void Walk(string directory, int depth)
        {
            if (IsCheckout(directory))
            {
                found.Add(directory);
                return;
            }
            if (depth >= maxDepth) return;
            IEnumerable<DirectoryInfo> children;
            try
            {
                children = new DirectoryInfo(directory).EnumerateDirectories()
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return;
            }
            foreach (var child in children)
            {
                if (child.Name.StartsWith('.') || skipSet.Contains(child.Name)) continue;
                // Links and junctions lead to loops and duplicates; hidden and
                // system folders are not where people keep checkouts.
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || child.Attributes.HasFlag(FileAttributes.Hidden)
                    || child.Attributes.HasFlag(FileAttributes.System)) continue;
                Walk(child.FullName, depth + 1);
            }
        }

        foreach (var root in roots)
        {
            string expanded;
            try { expanded = RepoSettings.Normalize(root); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { continue; }
            if (Directory.Exists(expanded)) Walk(expanded, 0);
        }
        return found;
    }

    /// <summary>
    /// True for a main checkout or submodule; false for a linked worktree,
    /// whose <c>.git</c> file points into another repository's <c>worktrees</c> folder.
    /// </summary>
    internal static bool IsCheckout(string directory)
    {
        var dotGit = Path.Combine(directory, ".git");
        if (Directory.Exists(dotGit)) return true;
        if (!File.Exists(dotGit)) return false;
        try
        {
            var pointer = File.ReadAllText(dotGit).Replace('\\', '/');
            return !pointer.Contains("/worktrees/");
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Finds checkouts and reads each one's origin, a bounded number at a time.</summary>
    public static Task<List<DiscoveredCheckout>> DiscoverAsync(RepoSettings settings, CancellationToken ct = default)
    {
        var paths = FindCheckouts(settings.ScanRoots, settings.ScanDepth, settings.SkipDirectories);
        return Bounded.MapAsync(paths, settings.Concurrency, async path =>
        {
            var origin = await new GitWorkingCopy(path).OriginUrlAsync(ct);
            return new DiscoveredCheckout(path, origin, RepoRemoteUrl.Parse(origin));
        });
    }
}

/// <summary>Bounded concurrency for batch git work.</summary>
public static class Bounded
{
    /// <summary>
    /// Maps <paramref name="items"/> through <paramref name="transform"/> with at
    /// most <paramref name="limit"/> running at once, preserving order.
    /// </summary>
    public static async Task<List<TResult>> MapAsync<T, TResult>(IReadOnlyList<T> items, int limit, Func<T, Task<TResult>> transform)
    {
        if (items.Count == 0) return new();
        using var gate = new SemaphoreSlim(Math.Max(1, limit));
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync();
            try { return await transform(item); }
            finally { gate.Release(); }
        }).ToList();
        return (await Task.WhenAll(tasks)).ToList();
    }
}
