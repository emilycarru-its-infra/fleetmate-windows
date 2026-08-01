using System.Text.Json;

namespace FleetMate.Core.Services.Tickets;

/// <summary>
/// Parsing helpers for TeamDynamix JWT bearer tokens (from the SSO loginSSO flow).
/// Framework-agnostic so the GUI's WebView2 SSO window and any silent path share it,
/// and so the claim extraction is unit-tested.
/// </summary>
public static class TdxJwt
{
    /// <summary>
    /// Extract (userName, userEmail) from a JWT's payload (the base64url-encoded
    /// middle segment). Returns (null, null) if the token is malformed.
    /// </summary>
    public static (string? UserName, string? UserEmail) ExtractUserInfo(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return (null, null);
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return (null, null);

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            var remainder = payload.Length % 4;
            if (remainder > 0) payload += new string('=', 4 - remainder);

            var bytes = Convert.FromBase64String(payload);
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;

            string? name = FirstStringClaim(root, "given_name", "name", "unique_name");
            string? email = FirstStringClaim(root, "email", "upn", "unique_name");
            return (name, email);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? FirstStringClaim(JsonElement root, params string[] claims)
    {
        foreach (var claim in claims)
        {
            if (root.TryGetProperty(claim, out var val) && val.ValueKind == JsonValueKind.String)
            {
                var s = val.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        return null;
    }
}
