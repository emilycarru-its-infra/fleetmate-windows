using System.Text.Json;

namespace FleetMate.Core.Services.Teams;

/// <summary>A GUI account snapshot, used only for token/workspace consistency. The API still authorizes every request.</summary>
public sealed record TeamsWorkspaceIdentity(string Account, string? Tenant = null);

internal sealed class TeamsWorkspaceBinding
{
    private readonly TeamsWorkspaceIdentity _expected;
    private readonly object _gate = new();
    private string? _tenant;
    private string? _objectId;
    internal Func<string?>? CurrentAccount { get; set; }

    internal TeamsWorkspaceBinding(TeamsWorkspaceIdentity expected)
    {
        _expected = expected;
        if (Guid.TryParse(expected.Tenant, out var tenant)) _tenant = tenant.ToString();
    }

    internal void Check(string? token)
    {
        try
        {
            var expected = Normalize(_expected.Account);
            var current = Normalize(CurrentAccount?.Invoke());
            if (expected == null || current != null && current != expected) throw Mismatch();
            var parts = (token ?? "").Split('.');
            if (parts.Length < 2) throw Mismatch();
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            string? Claim(string name) => json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var account = Normalize(Claim("preferred_username") ?? Claim("upn") ?? Claim("unique_name"));
            if (account != expected || !Guid.TryParse(Claim("tid"), out var tenant) || !Guid.TryParse(Claim("oid"), out var objectId)) throw Mismatch();
            lock (_gate)
            {
                if (_tenant != null && _tenant != tenant.ToString() || _objectId != null && _objectId != objectId.ToString()) throw Mismatch();
                _tenant = tenant.ToString();
                _objectId = objectId.ToString();
            }
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            throw Mismatch();
        }
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    private static TeamsSearchException Mismatch() => new(TeamsSearchFailure.AccessNotVerified,
        "The Teams sign-in account changed or could not be verified for this workspace. Refresh sign-in before continuing.");
}
