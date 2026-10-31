namespace FleetMate.Core.Links;

/// <summary>
/// Web links FleetMate opens in the browser on purpose (macOS parity: BrowserLink).
/// </summary>
/// <remarks>
/// A managed browser extension can hand Azure DevOps pages back to FleetMate.
/// When FleetMate itself sends one to the browser, as "Open in Browser" does,
/// that would bounce straight back, so the link carries <c>fleetmate=browser</c>,
/// which the extension leaves alone. Every other URL is returned unchanged.
/// </remarks>
public static class BrowserLink
{
    public const string OptOutName = "fleetmate";
    public const string OptOutValue = "browser";

    /// <summary>The URL to hand the browser, marked when it is an Azure DevOps page.</summary>
    public static Uri ForBrowser(Uri url)
    {
        if (!IsAzureDevOpsPage(url)) return url;
        var query = url.Query.TrimStart('?');
        var marker = $"{OptOutName}={OptOutValue}";
        if (query.Split('&').Any(p => p.Equals(marker, StringComparison.Ordinal))) return url;

        var builder = new UriBuilder(url) { Query = query.Length == 0 ? marker : $"{query}&{marker}" };
        // UriBuilder writes the default port out; keep the URL as it was given.
        if (url.IsDefaultPort) builder.Port = -1;
        return builder.Uri;
    }

    /// <summary>
    /// <see cref="ForBrowser(Uri)"/> for a string; text that is not an absolute
    /// URL is returned as it is.
    /// </summary>
    public static string ForBrowser(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? ForBrowser(parsed).AbsoluteUri : url;

    /// <summary>
    /// Recognised by path shape, as <see cref="FleetMateLink.ParseWeb"/> does,
    /// so any Azure DevOps server name works.
    /// </summary>
    public static bool IsAzureDevOpsPage(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps) return false;
        if (url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Contains("_git") || parts.Contains("_workitems")
            || parts.Any(p => p.StartsWith("_build", StringComparison.Ordinal));
    }
}
