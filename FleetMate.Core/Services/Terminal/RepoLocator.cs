namespace FleetMate.Core.Services.Terminal;

/// <summary>Where a Repos entry lives on disk, and whether it must be cloned first.</summary>
public sealed record RepoLocation(string Name, string Path, string? CloneUrl)
{
    public bool NeedsClone => CloneUrl != null && !Directory.Exists(System.IO.Path.Combine(Path, ".git"));
}

/// <summary>
/// Resolves a Repos entry. A local path is used as it is; a clone URL maps to
/// a folder under the per-user repos root and is cloned there on first use.
/// </summary>
public static class RepoLocator
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "FleetMate", "repos");

    public static bool IsCloneUrl(string entry) =>
        entry.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        entry.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        entry.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
        (entry.Contains('@') && entry.Contains(':') && !entry.Contains(":\\"));

    public static RepoLocation Resolve(string entry, string root)
    {
        var value = entry.Trim();
        if (!IsCloneUrl(value))
        {
            var path = Environment.ExpandEnvironmentVariables(value);
            return new RepoLocation(Path.GetFileName(path.TrimEnd('\\', '/')), path, null);
        }
        var name = NameFromUrl(value);
        return new RepoLocation(name, Path.Combine(root, name), value);
    }

    /// <summary>The repository's own name: the last path segment, without .git.</summary>
    public static string NameFromUrl(string url)
    {
        var last = url.TrimEnd('/').Split('/', ':').Last();
        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) last = last[..^4];
        var safe = new string(last.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return safe.Length == 0 ? "repo" : safe;
    }
}
