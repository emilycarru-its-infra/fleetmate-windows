namespace FleetMate.Core.Services.Repos;

/// <summary>
/// Parses git remote URLs into a <see cref="RepoKey"/>, so a local checkout can
/// be matched to a catalog entry however its origin was written.
///
/// Azure DevOps is recognised by the shape of the path rather than by host,
/// because the host is configuration, never code:
/// <list type="bullet">
/// <item>https: <c>https://[user@]&lt;host&gt;/&lt;org&gt;/&lt;project&gt;/_git/&lt;repo&gt;</c></item>
/// <item>legacy https: <c>https://&lt;org&gt;.visualstudio.com/[DefaultCollection/]&lt;project&gt;/_git/&lt;repo&gt;</c></item>
/// <item>ssh: <c>git@&lt;ssh-host&gt;:v3/&lt;org&gt;/&lt;project&gt;/&lt;repo&gt;</c></item>
/// <item>the <c>/&lt;org&gt;/_git/&lt;repo&gt;</c> short form, used when a repository has
/// its project's name, resolves the project to the repository name.</item>
/// </list>
/// GitHub: <c>https://github.com/&lt;owner&gt;/&lt;repo&gt;[.git]</c>,
/// <c>git@github.com:&lt;owner&gt;/&lt;repo&gt;.git</c> and <c>ssh://git@github.com/&lt;owner&gt;/&lt;repo&gt;.git</c>.
///
/// Anything else becomes <see cref="RepoProvider.Other"/>, keyed by host and
/// path; a remote on disk (a bare repository) by its folder. Case, a
/// <c>.git</c> suffix, credentials, ports and percent-encoding never change the key.
/// </summary>
public static class RepoRemoteUrl
{
    public static RepoKey? Parse(string? raw)
    {
        var trimmed = raw?.Trim() ?? "";
        if (trimmed.Length == 0) return null;

        if (LocalPath(trimmed) is { } local)
        {
            var parts = local.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            var name = StripGit(parts[^1]);
            var parent = string.Join("/", parts[..^1]);
            // A Unix-style path keeps its leading slash; a drive path starts with its letter.
            var owner = local.StartsWith('/') ? "/" + parent : parent;
            return new RepoKey(RepoProvider.Other, owner, null, name);
        }

        if (Split(trimmed) is not var (host, path, isScp)) return null;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Decode).Where(s => s.Length > 0).ToList();
        if (segments.Count == 0) return null;
        segments[^1] = StripGit(segments[^1]);
        if (segments[^1].Length == 0) return null;
        var lowerHost = host.ToLowerInvariant();

        if (lowerHost is "github.com" or "www.github.com")
        {
            return segments.Count == 2 ? new RepoKey(RepoProvider.GitHub, segments[0], null, segments[1]) : null;
        }

        // Azure DevOps ssh: v3/<org>/<project>/<repo>
        if (segments.Count == 4 && segments[0].Equals("v3", StringComparison.OrdinalIgnoreCase)
            && (isScp || lowerHost.StartsWith("ssh.") || lowerHost.StartsWith("vs-ssh.")))
        {
            return new RepoKey(RepoProvider.AzureDevOps, segments[1], segments[2], segments[3]);
        }

        // Azure DevOps https, any host: the path carries a `_git` segment.
        var gitIndex = segments.FindIndex(s => s.Equals("_git", StringComparison.OrdinalIgnoreCase));
        if (gitIndex >= 0 && gitIndex + 1 == segments.Count - 1)
        {
            var repo = segments[gitIndex + 1];
            var before = segments.Take(gitIndex).ToList();
            if (lowerHost.EndsWith(".visualstudio.com"))
            {
                // Legacy: the organization is the subdomain.
                if (before.Count > 0 && before[0].Equals("DefaultCollection", StringComparison.OrdinalIgnoreCase)) before.RemoveAt(0);
                var org = host[..^".visualstudio.com".Length];
                if (before.Count == 0) return new RepoKey(RepoProvider.AzureDevOps, org, repo, repo);
                if (before.Count == 1) return new RepoKey(RepoProvider.AzureDevOps, org, before[0], repo);
            }
            else
            {
                if (before.Count == 1) return new RepoKey(RepoProvider.AzureDevOps, before[0], repo, repo);
                if (before.Count == 2) return new RepoKey(RepoProvider.AzureDevOps, before[0], before[1], repo);
            }
        }

        return new RepoKey(RepoProvider.Other, lowerHost, null, string.Join("/", segments));
    }

    /// <summary>The registry id for a remote URL, or null if it cannot be parsed.</summary>
    public static string? Id(string? raw) => Parse(raw)?.Id;

    /// <summary>
    /// The path of a remote on disk, with forward slashes, or null for a
    /// network remote: <c>/srv/git/x.git</c>, <c>file:///C:/git/x.git</c>,
    /// <c>C:\git\x.git</c> or a UNC path.
    /// </summary>
    private static string? LocalPath(string value)
    {
        string? path = null;
        if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            path = Uri.UnescapeDataString(value["file://".Length..]);
            // file:///C:/x → /C:/x
            if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':') path = path[1..];
        }
        else if (value.StartsWith('/') || value.StartsWith(@"\\"))
        {
            path = value;
        }
        else if (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/'))
        {
            path = value;
        }
        if (path == null) return null;
        path = path.Replace('\\', '/').TrimEnd('/');
        return path;
    }

    /// <summary>
    /// Splits a URL or scp-style address into host and path, dropping any
    /// user, password and port.
    /// </summary>
    private static (string Host, string Path, bool IsScp)? Split(string raw)
    {
        if (raw.Contains("://"))
        {
            if (!Uri.TryCreate(raw.Replace(" ", "%20"), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;
            return (uri.Host, uri.AbsolutePath, false);
        }
        // scp-like: [user@]host:path
        var colon = raw.IndexOf(':');
        if (colon <= 0) return null;
        var host = raw[..colon];
        var at = host.LastIndexOf('@');
        if (at >= 0) host = host[(at + 1)..];
        if (host.Length == 0 || host.Contains('/') || host.Contains(' ')) return null;
        return (host, raw[(colon + 1)..], true);
    }

    private static string StripGit(string segment) =>
        segment.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segment[..^4] : segment;

    private static string Decode(string segment)
    {
        try { return Uri.UnescapeDataString(segment); }
        catch (UriFormatException) { return segment; }
    }
}
