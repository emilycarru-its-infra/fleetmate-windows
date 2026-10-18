namespace FleetMate.Core.Services.Repos;

/// <summary>A git checkout found on disk.</summary>
public sealed record DiscoveredCheckout(string Path, string? RemoteUrl, RepoKey? Key);

/// <summary>
/// Walks scan roots for git checkouts.
///
/// A folder is a checkout when it holds <c>.git</c> (a directory, or a file
/// for a submodule). The walk stops descending at a checkout, skips hidden
/// folders, links and junctions, the built-in skip list (<c>node_modules</c>,
/// <c>bin</c>, <c>obj</c>, …) and the configured one, and never goes deeper than
/// the configured depth below a root. A skipped name is still reported when the
/// folder is itself a checkout, so a repository called <c>build</c> is found;
/// its contents are never walked. Linked worktrees are not reported: they belong
/// to the checkout they were added from.
///
/// VS Code multi-root workspaces (<c>*.code-workspace</c>) found on the way, in a
/// visited folder or at the top of a checkout, contribute the checkouts their
/// <c>folders</c> name, wherever those live.
/// </summary>
public static class RepoDiscovery
{
    /// <summary>
    /// Folder names never descended into, whatever the settings say: build
    /// output, package caches and worktree folders hold no checkouts of their
    /// own, and walking them is what makes a scan slow.
    /// </summary>
    public static readonly IReadOnlyList<string> BuiltInSkip = new[]
    {
        ".worktrees", "node_modules", ".build", "build", "bin", "obj", "dist", "target",
        "DerivedData", "Pods", "Carthage", "vendor", "packages", "__pycache__", "venv",
        ".vs", "TestResults", "AppPackages",
    };

    /// <summary>
    /// Where people commonly keep clones, relative to the profile folder. On a
    /// first run these are looked in and whatever they hold is proposed.
    /// </summary>
    public static readonly IReadOnlyList<string> CommonLocations = new[]
    {
        @"~\Developer", @"~\DevOps", @"~\Projects", @"~\src", @"~\code", @"~\git",
        @"~\repos", @"~\GitHub", @"~\Documents\GitHub", @"~\source\repos",
    };

    /// <summary>The common locations that exist on this machine, as stored (<c>~\…</c>).</summary>
    public static List<string> ExistingCommonLocations() =>
        CommonLocations.Where(l => Directory.Exists(RepoSettings.Expand(l))).ToList();

    /// <summary>What one walk found: checkouts in walk order, each once, and the workspace files read.</summary>
    public sealed record ScanResult(List<string> Checkouts, List<string> WorkspaceFiles);

    /// <summary>Paths of checkouts under <paramref name="roots"/>, found without spawning a process.</summary>
    public static List<string> FindCheckouts(IEnumerable<string> roots, int maxDepth, IEnumerable<string> skip) =>
        Scan(roots, maxDepth, skip).Checkouts;

    /// <summary>Walks <paramref name="roots"/> for checkouts and VS Code workspace files.</summary>
    public static ScanResult Scan(IEnumerable<string> roots, int maxDepth, IEnumerable<string> skip)
    {
        var skipSet = new HashSet<string>(skip.Concat(BuiltInSkip), StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var workspaceFiles = new List<string>();
        string home;
        try { home = RepoSettings.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)); }
        catch (Exception) { home = ""; }

        void Note(string path)
        {
            string key;
            try { key = RepoSettings.Normalize(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { key = path; }
            if (seen.Add(key)) found.Add(path);
        }

        void CollectWorkspaces(string directory)
        {
            try
            {
                workspaceFiles.AddRange(Directory.EnumerateFiles(directory, "*.code-workspace", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }

        void Walk(string directory, int depth)
        {
            if (IsCheckout(directory))
            {
                Note(directory);
                CollectWorkspaces(directory);
                return;
            }
            CollectWorkspaces(directory);
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
                if (child.Name.StartsWith('.')) continue;
                // Links and junctions lead to loops and duplicates; hidden and
                // system folders are not where people keep checkouts.
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || child.Attributes.HasFlag(FileAttributes.Hidden)
                    || child.Attributes.HasFlag(FileAttributes.System)) continue;
                var underHome = home.Length > 0 && string.Equals(directory.TrimEnd('\\', '/'), home, StringComparison.OrdinalIgnoreCase);
                if (skipSet.Contains(child.Name) || (underHome && (child.Name is "AppData" or "Library")))
                {
                    if (IsCheckout(child.FullName)) Note(child.FullName);
                    continue;
                }
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

        foreach (var file in workspaceFiles)
            foreach (var folder in WorkspaceFolders(file))
                if (EnclosingCheckout(folder) is { } checkout) Note(checkout);

        return new ScanResult(found, workspaceFiles);
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

    /// <summary>
    /// The checkout <paramref name="path"/> is in: itself, or the nearest parent
    /// that is one (a workspace often opens a subfolder of a repository).
    /// </summary>
    internal static string? EnclosingCheckout(string path)
    {
        DirectoryInfo? current;
        try { current = new DirectoryInfo(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        for (var i = 0; i < 8 && current != null; i++, current = current.Parent)
        {
            if (current.Exists && IsCheckout(current.FullName)) return current.FullName.TrimEnd('\\', '/');
        }
        return null;
    }

    /// <summary>
    /// Absolute paths of the folders a <c>.code-workspace</c> file lists. Paths
    /// are relative to the file unless absolute; the file is JSON with comments
    /// and trailing commas allowed, as VS Code writes it.
    /// </summary>
    public static List<string> WorkspaceFolders(string file)
    {
        try { return WorkspaceFolders(File.ReadAllText(file), Path.GetDirectoryName(Path.GetFullPath(file)) ?? ""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
    }

    internal static List<string> WorkspaceFolders(string json, string relativeTo)
    {
        var result = new List<string>();
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json, new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (!document.RootElement.TryGetProperty("folders", out var folders)
                || folders.ValueKind != System.Text.Json.JsonValueKind.Array) return result;
            foreach (var folder in folders.EnumerateArray())
            {
                if (folder.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !folder.TryGetProperty("path", out var pathElement)
                    || pathElement.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                var raw = pathElement.GetString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                try
                {
                    var expanded = RepoSettings.Expand(raw.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
                    result.Add(Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(relativeTo, expanded)));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
        }
        catch (System.Text.Json.JsonException) { }
        return result;
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
