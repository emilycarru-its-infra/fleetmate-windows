namespace FleetMate.Core.Services.Teams;

/// <summary>
/// Links in Teams search results come from message content, which anyone in a channel can
/// write. A link is opened or printed only when it is https, carries no user info, and its
/// host is Teams, SharePoint or one of the configured extra hosts; anything else is shown as
/// plain text or withheld.
/// </summary>
public static class TeamsLinkPolicy
{
    /// <summary>Allowed by default. A leading <c>*.</c> allows subdomains only.</summary>
    public static readonly IReadOnlyList<string> DefaultHosts =
        new[] { "teams.microsoft.com", "teams.cloud.microsoft", "*.sharepoint.com" };

    /// <summary>The defaults plus configured extras (comma or space separated).</summary>
    public static IReadOnlyList<string> Hosts(string? extra) =>
        DefaultHosts.Concat((extra ?? string.Empty)
                .Split(new[] { ',', ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(h => h.Trim().ToLowerInvariant()))
            .ToList();

    /// <summary>The URL when it is allowed; null otherwise.</summary>
    public static Uri? SafeUri(string? value, IReadOnlyList<string> allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return null;
        var host = uri.IdnHost.ToLowerInvariant();
        if (host.Length == 0) return null;
        return allowedHosts.Any(p => Matches(host, p)) ? uri : null;
    }

    internal static bool Matches(string host, string pattern)
    {
        pattern = pattern.ToLowerInvariant();
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
            return host.EndsWith(pattern[1..], StringComparison.Ordinal);
        if (pattern is "teams.microsoft.com" or "teams.cloud.microsoft")
            return host == pattern;
        return host == pattern || host.EndsWith("." + pattern, StringComparison.Ordinal);
    }
}
