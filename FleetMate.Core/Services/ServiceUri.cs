namespace FleetMate.Core.Services;

/// <summary>Normalizes administrator-entered service hosts into HTTPS base URIs.</summary>
public static class ServiceUri
{
    /// An unset host is a configuration state, not a programming error: callers
    /// decide whether to skip the service or fail with a useful message, so an
    /// empty string comes back rather than an exception.
    public static string Normalize(string? value)
    {
        var trimmed = value?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(trimmed)) return string.Empty;
        return Uri.TryCreate(trimmed, UriKind.Absolute, out _)
            ? trimmed
            : $"https://{trimmed}";
    }
}
