namespace FleetMate.Core.Services;

/// <summary>Normalizes administrator-entered service hosts into HTTPS base URIs.</summary>
public static class ServiceUri
{
    public static string Normalize(string value)
    {
        var trimmed = value.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(trimmed)) return trimmed;
        return Uri.TryCreate(trimmed, UriKind.Absolute, out _)
            ? trimmed
            : $"https://{trimmed}";
    }
}
