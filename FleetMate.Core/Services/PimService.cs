using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FleetMate.Core.Services;

/// <summary>
/// Privileged Identity Management — the <c>security</c> elevation domain.
///
/// This deliberately does NOT go through <see cref="ElevationSession"/>, and the
/// reason is the whole point of the class. Elevation runs a per-domain *managed
/// identity*: a service principal. PIM self-activation is a statement about a
/// **user** — "activate this eligible role for the person asking" — so a service
/// principal cannot do it on the operator's behalf. Attempting it returns the same
/// missing-role failure the operator started with.
///
/// So the security domain calls Graph as the signed-in operator, using the token
/// source the rest of the app already uses. The privilege still comes from the
/// operator's own PIM eligibility; the app is not a privilege, which matches the
/// model the elevation session documents for the other five domains.
///
/// Worked example of why this exists: minting a bulk device-join token for silent
/// directory join is refused unless the caller holds a device- or endpoint-admin
/// role. Those roles are PIM-eligible rather than standing, so without a way to
/// activate one the whole unattended-join path is blocked.
/// </summary>
public sealed class PimService
{
    private const string GraphBase = "https://graph.microsoft.com/v1.0/";

    // Named delegated scopes, not "{audience}/.default". PIM is not in the default
    // consented set, so a .default token comes back without it and Graph refuses
    // with PermissionScopeNotGranted. EntraTokenSource passes a named permission
    // through unchanged, so asking for exactly what is needed is both possible and
    // the least-privilege choice.
    private const string ReadScope  = "https://graph.microsoft.com/RoleManagement.Read.Directory";
    private const string WriteScope = "https://graph.microsoft.com/RoleAssignmentSchedule.ReadWrite.Directory";

    // Resolving the caller only needs to know who they are.
    private const string MeScope = "https://graph.microsoft.com/User.Read";

    private readonly EntraTokenSource _tokens;
    private readonly HttpClient _http;

    public PimService(EntraTokenSource tokens, HttpClient? http = null)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _http = http ?? new HttpClient();
    }

    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string path, string scope, CancellationToken ct)
    {
        var token = await _tokens.GetTokenAsync(scope, ct);
        var req = new HttpRequestMessage(method, GraphBase + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        // Graph nests the useful sentence; surfacing the raw envelope helps nobody.
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var msg))
            {
                return msg.GetString() ?? body;
            }
        }
        catch (JsonException) { /* not JSON; fall through to the raw body */ }
        return string.IsNullOrWhiteSpace(body) ? $"HTTP {(int)resp.StatusCode}" : body;
    }

    /// <summary>The signed-in operator's directory object id.</summary>
    public async Task<string> GetMyIdAsync(CancellationToken ct = default)
    {
        using var req = await RequestAsync(HttpMethod.Get, "me?$select=id", MeScope, ct);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new PimException($"Could not resolve the signed-in user: {await ReadErrorAsync(resp, ct)}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("id").GetString()
               ?? throw new PimException("Graph returned no id for the signed-in user");
    }

    private static PimRole ReadRole(JsonElement item)
    {
        var def = item.TryGetProperty("roleDefinition", out var d) ? d : default;
        return new PimRole(
            RoleDefinitionId: item.TryGetProperty("roleDefinitionId", out var rid) ? rid.GetString() ?? "" : "",
            DisplayName: def.ValueKind == JsonValueKind.Object && def.TryGetProperty("displayName", out var n)
                ? n.GetString() ?? "" : "",
            DirectoryScopeId: item.TryGetProperty("directoryScopeId", out var s) ? s.GetString() ?? "/" : "/",
            EndDateTime: item.TryGetProperty("endDateTime", out var e) && e.ValueKind != JsonValueKind.Null
                ? e.GetString() : null);
    }

    private async Task<List<PimRole>> ReadRolesAsync(string resource, string what, CancellationToken ct)
    {
        var me = await GetMyIdAsync(ct);
        var path = $"roleManagement/directory/{resource}?$filter=principalId eq '{me}'&$expand=roleDefinition";

        using var req = await RequestAsync(HttpMethod.Get, path, ReadScope, ct);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new PimException($"Could not read {what}: {await ReadErrorAsync(resp, ct)}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(ReadRole).ToList();
    }

    /// <summary>
    /// Roles the operator is eligible to activate. An empty list is a real answer,
    /// not an error: it means no PIM eligibility, which is exactly what an operator
    /// hitting a missing-role failure needs to be told plainly.
    /// </summary>
    public Task<List<PimRole>> GetEligibleRolesAsync(CancellationToken ct = default) =>
        ReadRolesAsync("roleEligibilityScheduleInstances", "PIM eligibilities", ct);

    /// <summary>Roles currently active for the operator, so a second activation is not attempted.</summary>
    public Task<List<PimRole>> GetActiveRolesAsync(CancellationToken ct = default) =>
        ReadRolesAsync("roleAssignmentScheduleInstances", "active role assignments", ct);

    /// <summary>
    /// Self-activate an eligible role. <paramref name="justification"/> is required by
    /// most tenant policies and is recorded in the audit log, so it is not optional here.
    /// </summary>
    public async Task<PimActivationResult> ActivateAsync(
        string roleName,
        string justification,
        int durationHours = 8,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(justification))
            throw new PimException("A justification is required — tenant PIM policy records it in the audit log.");

        var eligible = await GetEligibleRolesAsync(ct);
        var match = eligible.FirstOrDefault(r =>
            string.Equals(r.DisplayName, roleName, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            var names = eligible.Count == 0
                ? "(none — you hold no PIM eligibilities)"
                : string.Join(", ", eligible.Select(r => r.DisplayName));
            throw new PimException($"'{roleName}' is not among your eligible roles. Eligible: {names}");
        }

        var already = (await GetActiveRolesAsync(ct))
            .FirstOrDefault(r => r.RoleDefinitionId == match.RoleDefinitionId);
        if (already is not null)
            return new PimActivationResult(match.DisplayName, "AlreadyActive", already.EndDateTime);

        var me = await GetMyIdAsync(ct);
        var payload = new
        {
            action = "selfActivate",
            principalId = me,
            roleDefinitionId = match.RoleDefinitionId,
            directoryScopeId = match.DirectoryScopeId,
            justification,
            scheduleInfo = new
            {
                startDateTime = DateTimeOffset.UtcNow.ToString("o"),
                expiration = new { type = "afterDuration", duration = $"PT{durationHours}H" }
            }
        };

        using var req = await RequestAsync(HttpMethod.Post, "roleManagement/directory/roleAssignmentScheduleRequests", WriteScope, ct);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new PimException($"Activation of '{match.DisplayName}' failed: {await ReadErrorAsync(resp, ct)}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var status = doc.RootElement.TryGetProperty("status", out var st) ? st.GetString() ?? "Unknown" : "Unknown";

        // A tenant that requires approval returns a pending request rather than an
        // active role. Reporting "activated" there would be a lie the operator only
        // discovers when the next call is still refused.
        return new PimActivationResult(match.DisplayName, status, null);
    }

    /// <summary>Give a role back early rather than waiting for it to expire.</summary>
    public async Task<string> DeactivateAsync(string roleName, CancellationToken ct = default)
    {
        var active = await GetActiveRolesAsync(ct);
        var match = active.FirstOrDefault(r =>
            string.Equals(r.DisplayName, roleName, StringComparison.OrdinalIgnoreCase))
            ?? throw new PimException($"'{roleName}' is not currently active for you.");

        var me = await GetMyIdAsync(ct);
        var payload = new
        {
            action = "selfDeactivate",
            principalId = me,
            roleDefinitionId = match.RoleDefinitionId,
            directoryScopeId = match.DirectoryScopeId,
            justification = "Deactivated via FleetMate"
        };

        using var req = await RequestAsync(HttpMethod.Post, "roleManagement/directory/roleAssignmentScheduleRequests", WriteScope, ct);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new PimException($"Deactivation of '{match.DisplayName}' failed: {await ReadErrorAsync(resp, ct)}");

        return match.DisplayName;
    }
}

/// <param name="EndDateTime">Null means permanent eligibility, or an activation with no fixed end.</param>
public sealed record PimRole(
    string RoleDefinitionId,
    string DisplayName,
    string DirectoryScopeId,
    string? EndDateTime);

/// <param name="Status">Graph's request status — <c>Provisioned</c> is active; anything
/// else (notably <c>PendingApproval</c>) is not, and must not be reported as success.</param>
public sealed record PimActivationResult(string RoleName, string Status, string? EndDateTime)
{
    public bool IsActive =>
        Status.Equals("Provisioned", StringComparison.OrdinalIgnoreCase) ||
        Status.Equals("AlreadyActive", StringComparison.OrdinalIgnoreCase);
}

public sealed class PimException : Exception
{
    public PimException(string message) : base(message) { }
}
