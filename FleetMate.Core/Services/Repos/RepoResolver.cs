namespace FleetMate.Core.Services.Repos;

/// <summary>
/// A repository as the CLI and app see it: its identity, what the provider
/// says about it (when it is in the catalog), and its local checkout (when
/// registered).
/// </summary>
public sealed record RepoRecord(RepoKey Key, CatalogRepo? Catalog, RepoRegistryEntry? Local)
{
    public string Id => Key.Id;
    public bool IsLocal => Local != null;
    public bool IsTracked => Local?.Tracked ?? false;
    public string? DefaultBranch => Catalog?.DefaultBranch ?? Local?.DefaultBranch;

    /// <summary>
    /// Merges a catalog and registry into one record per repository. The
    /// catalog's spelling of the key wins over the checkout's.
    /// </summary>
    public static List<RepoRecord> Merge(IEnumerable<CatalogRepo> catalog, IEnumerable<RepoRegistryEntry> registry)
    {
        var byId = new Dictionary<string, (RepoKey Key, CatalogRepo? Catalog, RepoRegistryEntry? Local)>();
        foreach (var repo in catalog) byId[repo.Id] = (repo.Key, repo, null);
        foreach (var entry in registry)
        {
            byId[entry.Key.Id] = byId.TryGetValue(entry.Key.Id, out var existing)
                ? (existing.Key, existing.Catalog, entry)
                : (entry.Key, null, entry);
        }
        return byId.Values
            .Select(v => new RepoRecord(v.Key, v.Catalog, v.Local))
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>
/// Turns what a person or agent typed into one repository.
///
/// Accepted forms, all case-insensitive:
/// <list type="bullet">
/// <item>the registry id: <c>github:owner/repo</c>, <c>azdo:org/project/repo</c></item>
/// <item><c>name</c></item>
/// <item><c>project/name</c> (Azure DevOps) or <c>owner/name</c> (GitHub)</item>
/// <item><c>org/project/name</c> (Azure DevOps)</item>
/// <item>a path to a registered checkout: absolute, or starting with <c>~</c> or <c>.</c></item>
/// </list>
/// More than one match is an error that lists the candidates.
/// </summary>
public static class RepoResolver
{
    public static RepoRecord Resolve(string argument, IReadOnlyList<RepoRecord> records)
    {
        var matches = Candidates(argument, records);
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw RepoException.NotFound(argument),
            _ => throw RepoException.Ambiguous(argument, matches.Select(m => $"{m.Key.DisplayName}  ({m.Id})").ToList()),
        };
    }

    public static List<RepoRecord> Candidates(string argument, IReadOnlyList<RepoRecord> records)
    {
        var arg = argument.Trim();
        if (arg.Length == 0) return new();

        if (LooksLikePath(arg))
        {
            var target = SafeNormalize(arg);
            return records.Where(r => r.Local?.Path is { } path
                && string.Equals(SafeNormalize(path), target, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var lower = arg.ToLowerInvariant();
        if (lower.Contains(':')) return records.Where(r => r.Id == lower).ToList();

        var parts = lower.Split('/');
        return records.Where(r =>
        {
            var key = r.Key;
            var name = key.Name.ToLowerInvariant();
            return parts.Length switch
            {
                1 => name == parts[0],
                2 => name == parts[1] && key.Scope.ToLowerInvariant() == parts[0],
                3 => key.Provider == RepoProvider.AzureDevOps
                     && key.Owner.ToLowerInvariant() == parts[0]
                     && key.Project?.ToLowerInvariant() == parts[1]
                     && name == parts[2],
                _ => false,
            };
        }).ToList();
    }

    /// <summary>Whether an argument names a folder rather than a repository.</summary>
    public static bool LooksLikePath(string arg) =>
        arg.StartsWith('/') || arg.StartsWith('\\') || arg.StartsWith('~') || arg.StartsWith('.')
        || (arg.Length >= 2 && char.IsLetter(arg[0]) && arg[1] == ':');

    private static string SafeNormalize(string path)
    {
        try { return RepoSettings.Normalize(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }
}
