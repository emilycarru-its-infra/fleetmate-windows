using System.Text;

namespace FleetMate.Core.Services.Repos;

/// <summary>One entry of a checkout's file tree: a folder with children, or a file.</summary>
public sealed record RepoFileNode(string Name, string Path, IReadOnlyList<RepoFileNode>? Children = null)
{
    public bool IsFolder => Children != null;

    // Records compare lists by reference; a tree compares by content.
    public bool Equals(RepoFileNode? other) =>
        other is not null && Name == other.Name && Path == other.Path
        && (Children == null ? other.Children == null : other.Children != null && Children.SequenceEqual(other.Children));

    public override int GetHashCode() => HashCode.Combine(Name, Path, Children?.Count);
}

/// <summary>One visible line of the outline.</summary>
public sealed record RepoFileRow(RepoFileNode Node, int Depth, bool IsExpanded);

/// <summary>
/// Turns git's flat path list into the outline the Repos view shows, and
/// filters it. Pure, so it is tested without a checkout.
/// </summary>
public static class RepoFileTree
{
    /// <summary>Folders first, then files, each in natural, case-insensitive order (file2 before file10).</summary>
    public static List<RepoFileNode> Build(IEnumerable<string> paths)
    {
        var root = new Folder();
        foreach (var path in paths)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            var folder = root;
            foreach (var part in parts[..^1])
            {
                if (!folder.Folders.TryGetValue(part, out var next))
                {
                    next = new Folder();
                    folder.Folders[part] = next;
                }
                folder = next;
            }
            folder.Files.Add(parts[^1]);
        }
        return root.Nodes("");
    }

    /// <summary>
    /// The subtree whose file paths contain <paramref name="query"/>
    /// (case-insensitive). A folder stays when its own name matches — with all
    /// it holds — or when anything below it matches. An empty query returns
    /// <paramref name="nodes"/> unchanged.
    /// </summary>
    public static IReadOnlyList<RepoFileNode> Filter(IReadOnlyList<RepoFileNode> nodes, string? query)
    {
        var needle = query?.Trim() ?? "";
        if (needle.Length == 0) return nodes;
        return nodes.Select(n => Filtered(n, needle)).OfType<RepoFileNode>().ToList();
    }

    private static RepoFileNode? Filtered(RepoFileNode node, string needle)
    {
        if (node.Children == null) return node.Path.Contains(needle, StringComparison.OrdinalIgnoreCase) ? node : null;
        if (node.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)) return node;
        var kept = node.Children.Select(c => Filtered(c, needle)).OfType<RepoFileNode>().ToList();
        return kept.Count == 0 ? null : node with { Children = kept };
    }

    /// <summary>Every folder path in <paramref name="nodes"/>, for expanding a filtered tree fully.</summary>
    public static HashSet<string> FolderPaths(IEnumerable<RepoFileNode> nodes)
    {
        var result = new HashSet<string>();
        void Walk(IEnumerable<RepoFileNode> list)
        {
            foreach (var node in list)
            {
                if (node.Children == null) continue;
                result.Add(node.Path);
                Walk(node.Children);
            }
        }
        Walk(nodes);
        return result;
    }

    /// <summary>
    /// The rows an outline shows: each node with its depth, descending only
    /// into folders in <paramref name="expanded"/>. Flattened so a virtualized
    /// list renders a large checkout without building a control per file.
    /// </summary>
    public static List<RepoFileRow> VisibleRows(IEnumerable<RepoFileNode> nodes, IReadOnlySet<string> expanded)
    {
        var rows = new List<RepoFileRow>();
        void Walk(IEnumerable<RepoFileNode> list, int depth)
        {
            foreach (var node in list)
            {
                var open = node.IsFolder && expanded.Contains(node.Path);
                rows.Add(new RepoFileRow(node, depth, open));
                if (open) Walk(node.Children!, depth + 1);
            }
        }
        Walk(nodes, 0);
        return rows;
    }

    /// <summary>Number of files in <paramref name="nodes"/>, counted through every folder.</summary>
    public static int FileCount(IEnumerable<RepoFileNode> nodes) =>
        nodes.Sum(n => n.Children == null ? 1 : FileCount(n.Children));

    private sealed class Folder
    {
        public Dictionary<string, Folder> Folders { get; } = new();
        public HashSet<string> Files { get; } = new();

        public List<RepoFileNode> Nodes(string prefix)
        {
            var folders = Folders.Keys.OrderBy(k => k, NaturalComparer.Instance).Select(name =>
            {
                var path = prefix.Length == 0 ? name : prefix + "/" + name;
                return new RepoFileNode(name, path, Folders[name].Nodes(path));
            });
            var files = Files.OrderBy(k => k, NaturalComparer.Instance)
                .Select(name => new RepoFileNode(name, prefix.Length == 0 ? name : prefix + "/" + name));
            return folders.Concat(files).ToList();
        }
    }
}

/// <summary>
/// Case-insensitive ordering that compares runs of digits by value, the way
/// Explorer sorts: <c>file2</c> before <c>file10</c>.
/// </summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x == null) return -1;
        if (y == null) return 1;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var si = i; while (i < x.Length && char.IsDigit(x[i])) i++;
                var sj = j; while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                var c = string.CompareOrdinal(a, b);
                if (c != 0) return c;
                continue;
            }
            var cx = char.ToLowerInvariant(x[i]);
            var cy = char.ToLowerInvariant(y[j]);
            if (cx != cy) return cx.CompareTo(cy);
            i++;
            j++;
        }
        var length = (x.Length - i).CompareTo(y.Length - j);
        return length != 0 ? length : string.CompareOrdinal(x, y);
    }
}

/// <summary>
/// Repositories grouped the way Settings lists them: by project on Azure
/// DevOps, by owner on GitHub, by host otherwise.
/// </summary>
public sealed record RepoRecordGroup(RepoProvider Provider, string Scope, IReadOnlyList<RepoRecord> Records)
{
    public string Id => $"{Provider.Code()}:{Scope.ToLowerInvariant()}";

    public string Title => Provider switch
    {
        RepoProvider.AzureDevOps => $"Azure DevOps · {Scope}",
        RepoProvider.GitHub => $"GitHub · {Scope}",
        _ => Scope,
    };

    /// <summary>
    /// Groups <paramref name="records"/> after keeping those whose name, id or
    /// local path contains <paramref name="query"/>. Providers appear Azure
    /// DevOps, GitHub, other; scopes and repositories alphabetically within each.
    /// </summary>
    public static List<RepoRecordGroup> Groups(IEnumerable<RepoRecord> records, string? query = null)
    {
        var needle = query?.Trim() ?? "";
        var kept = needle.Length == 0 ? records : records.Where(r =>
            r.Key.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || r.Id.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || (r.Local?.Path.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
        return kept
            .GroupBy(r => (r.Key.Provider, Scope: r.Key.Scope.ToLowerInvariant()))
            .Select(g => new RepoRecordGroup(g.Key.Provider, g.First().Key.Scope,
                g.OrderBy(r => r.Key.Name, NaturalComparer.Instance).ToList()))
            .OrderBy(g => (int)g.Provider)
            .ThenBy(g => g.Scope, NaturalComparer.Instance)
            .ToList();
    }
}

/// <summary>Whether a file's bytes can be edited as text.</summary>
public static class RepoTextFile
{
    /// <summary>Files larger than this open read-only, not in the editor.</summary>
    public const int EditableLimit = 4 * 1024 * 1024;

    private static readonly UTF8Encoding Strict = new(false, true);

    /// <summary>
    /// The file as UTF-8 text, or null when it looks binary: a NUL byte in its
    /// first 8 KB, as git itself decides, or bytes that are not UTF-8. A UTF-8
    /// byte-order mark is dropped.
    /// </summary>
    public static string? Decode(byte[] data)
    {
        if (data.AsSpan(0, Math.Min(8192, data.Length)).IndexOf((byte)0) >= 0) return null;
        try
        {
            var start = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
            return Strict.GetString(data, start, data.Length - start);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>Whether the bytes start with a UTF-8 byte-order mark, so a save can keep it.</summary>
    public static bool HasByteOrderMark(byte[] data) => data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF;
}
