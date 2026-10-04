using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FleetMate.Core.Models.Devices;
using Serilog;

namespace FleetMate.Core.Services.Devices;

/// <summary>
/// Apple School Manager / Apple Business Manager API client — the Windows
/// counterpart of the macOS client's asbmutil-backed service, speaking the
/// same endpoints directly:
/// <list type="bullet">
/// <item>OAuth client credentials at account.apple.com, authenticated with an
/// ES256 client assertion signed by the profile's private key;</item>
/// <item>GET /v1/orgDevices (cursor paging), /v1/mdmServers,
/// /v1/mdmServers/{id}/relationships/devices, /v1/orgDevices/{serial},
/// /v1/orgDevices/{serial}/relationships/assignedServer and
/// /v1/orgDevices/{serial}/appleCareCoverage;</item>
/// <item>POST /v1/orgDeviceActivities, then GET /v1/orgDeviceActivities/{id}
/// until the activity settles.</item>
/// </list>
/// Apple allows an organization about twenty requests a minute, so a full
/// read costs one request per thousand devices plus one per service.
/// </summary>
public sealed class AppleOrgService : IDisposable
{
    public const string TokenEndpoint = "https://account.apple.com/auth/oauth2/v2/token";

    public AppleOrgProfile Profile { get; }
    private readonly AppleOrgCredentialStore.Secret _secret;
    private readonly HttpClient _http;
    private string? _token;
    private DateTimeOffset _tokenExpires = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public AppleOrgService(AppleOrgProfile profile, AppleOrgCredentialStore.Secret secret, HttpMessageHandler? handler = null)
    {
        Profile = profile;
        _secret = secret;
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri($"https://{profile.ApiHost}/");
        _http.Timeout = TimeSpan.FromSeconds(120);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Connect with a stored profile, or null when Credential Manager does not hold it.</summary>
    public static AppleOrgService? Connect(string profileName, AppleOrgCredentialStore? store = null)
    {
        var secret = (store ?? new AppleOrgCredentialStore()).Load(profileName);
        return secret == null ? null : new AppleOrgService(new AppleOrgProfile(profileName, secret.ClientId), secret);
    }

    // ── Authentication ───────────────────────────────────────────────────

    /// <summary>
    /// The client assertion Apple's token endpoint takes: an ES256 JWT with
    /// the key ID in its header and the client ID as issuer and subject.
    /// </summary>
    public static string CreateClientAssertion(string clientId, string keyId, string privateKeyPem, DateTimeOffset now)
    {
        var header = new JsonObject { ["alg"] = "ES256", ["kid"] = keyId, ["typ"] = "JWT" };
        var iat = now.ToUnixTimeSeconds();
        var claims = new JsonObject
        {
            ["iss"] = clientId,
            ["sub"] = clientId,
            ["aud"] = TokenEndpoint,
            ["iat"] = iat,
            ["exp"] = iat + 1200,
            ["jti"] = Guid.NewGuid().ToString(),
        };
        var unsigned = B64Url(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." +
                       B64Url(Encoding.UTF8.GetBytes(claims.ToJsonString()));

        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        // JWS wants r||s, not DER.
        var signature = key.SignData(Encoding.UTF8.GetBytes(unsigned), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return unsigned + "." + B64Url(signature);
    }

    public static string B64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            // Refresh five minutes early, as asbmutil does.
            if (_token != null && DateTimeOffset.UtcNow < _tokenExpires.AddMinutes(-5)) return _token;

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _secret.ClientId,
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = CreateClientAssertion(_secret.ClientId, _secret.KeyId, _secret.PrivateKeyPem, DateTimeOffset.UtcNow),
                ["scope"] = Profile.Scope,
            });
            using var response = await _http.PostAsync(TokenEndpoint, form, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"{Profile.ServiceName} sign-in failed (HTTP {(int)response.StatusCode}): {Trim(body)}");

            var json = JsonNode.Parse(body)!;
            _token = json["access_token"]!.GetValue<string>();
            _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(json["expires_in"]?.GetValue<int>() ?? 3600);
            return _token;
        }
        finally { _tokenLock.Release(); }
    }

    // ── Reading ──────────────────────────────────────────────────────────

    /// <summary>
    /// The whole organization: every device, every service, and which service
    /// each device is assigned to. The device list does not carry the
    /// assignment, so each service's device listing supplies it.
    /// </summary>
    public async Task<AppleOrgSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        var devicesTask = ListDevicesAsync(ct);
        var serversTask = GetAsync<AppleListResponse<AppleServerAttributes>>("v1/mdmServers", ct);
        await Task.WhenAll(devicesTask, serversTask);

        var rawServers = serversTask.Result?.Data ?? new();
        var listings = new Dictionary<string, List<string>>();
        foreach (var server in rawServers)
            listings[server.Id] = await ListServerDevicesAsync(server.Id, ct);

        var assignments = AppleOrgJoin.Assignments(listings);
        var servers = rawServers
            .Select(s => new AppleOrgServer(s.Id, Profile.Name, s.Attributes?.ServerName ?? s.Id, s.Attributes?.ServerType,
                listings.GetValueOrDefault(s.Id)?.Count))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var devices = devicesTask.Result
            .Select(a => Map(a, Profile.Name,
                assignments.GetValueOrDefault(DeviceListJoin.Normalize(a.SerialNumber)) ?? a.DeviceManagementServiceId))
            .ToList();

        Log.Information("Read {Devices} devices and {Servers} services from {Service}", devices.Count, servers.Count, Profile.ServiceName);
        return new AppleOrgSnapshot(Profile, devices, servers);
    }

    private async Task<List<AppleDeviceAttributes>> ListDevicesAsync(CancellationToken ct)
    {
        var all = new List<AppleDeviceAttributes>();
        string? cursor = null;
        do
        {
            var url = "v1/orgDevices?limit=1000" + (cursor != null ? "&cursor=" + Uri.EscapeDataString(cursor) : "");
            var page = await GetAsync<AppleListResponse<AppleDeviceAttributes>>(url, ct);
            all.AddRange(page?.Data.Select(d => d.Attributes).OfType<AppleDeviceAttributes>() ?? Enumerable.Empty<AppleDeviceAttributes>());
            cursor = page?.Meta?.Paging?.NextCursor;
        } while (!string.IsNullOrEmpty(cursor));
        return all;
    }

    private async Task<List<string>> ListServerDevicesAsync(string serverId, CancellationToken ct)
    {
        var serials = new List<string>();
        string? next = $"v1/mdmServers/{Uri.EscapeDataString(serverId)}/relationships/devices?limit=1000";
        for (var page = 0; next != null && page < 50; page++)
        {
            var response = await GetAsync<AppleListResponse<JsonElement>>(next, ct);
            serials.AddRange(response?.Data.Select(d => d.Id) ?? Enumerable.Empty<string>());
            next = response?.Links?.Next;
        }
        return serials;
    }

    /// <summary>Re-read a few devices after an action: their attributes and assigned service.</summary>
    public async Task<List<AppleOrgDevice>> RereadAsync(IEnumerable<string> serials, CancellationToken ct = default)
    {
        var results = new List<AppleOrgDevice>();
        // Apple drops HTTP/2 streams above about four at once.
        using var gate = new SemaphoreSlim(4);
        var tasks = serials.Select(async serial =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var attr = (await GetAsync<AppleSingleResponse<AppleDeviceAttributes>>($"v1/orgDevices/{Uri.EscapeDataString(serial)}", ct))?.Data?.Attributes;
                if (attr == null) return null;
                var server = (await GetAsync<AppleSingleResponse<JsonElement>>(
                    $"v1/orgDevices/{Uri.EscapeDataString(serial)}/relationships/assignedServer", ct))?.Data?.Id;
                return Map(attr, Profile.Name, server);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Re-reading {Serial} failed", serial);
                return null;
            }
            finally { gate.Release(); }
        });
        foreach (var device in await Task.WhenAll(tasks)) if (device != null) results.Add(device);
        return results;
    }

    /// <summary>AppleCare and warranty coverage. Apple serves it one device at a time.</summary>
    public async Task<List<AppleCareAgreement>> AppleCareAsync(string serial, CancellationToken ct = default)
    {
        var response = await GetAsync<AppleListResponse<AppleCareAttributes>>(
            $"v1/orgDevices/{Uri.EscapeDataString(serial)}/appleCareCoverage", ct);
        return (response?.Data ?? new()).Select(d => d.Attributes).OfType<AppleCareAttributes>()
            .Select(a => new AppleCareAgreement(a.Description ?? "Coverage", a.Status, ParseDate(a.StartDateTime),
                ParseDate(a.EndDateTime), a.AgreementNumber, a.PaymentType, a.IsCanceled ?? false))
            .ToList();
    }

    // ── Actions ──────────────────────────────────────────────────────────

    /// <summary>The orgDeviceActivities request body for an action.</summary>
    public static JsonObject ActivityBody(AppleOrgAction action, IEnumerable<string> serials)
    {
        var attributes = new JsonObject { ["activityType"] = action.ActivityType };
        if (action.RequiresDeadline && action.Deadline is { } deadline)
            attributes["activityTypeMetadata"] = new JsonObject { ["mdmMigrationDeadlineDateTime"] = Iso(deadline) };

        var relationships = new JsonObject
        {
            ["devices"] = new JsonObject
            {
                ["data"] = new JsonArray(serials.Select(s => (JsonNode)new JsonObject { ["type"] = "orgDevices", ["id"] = s }).ToArray())
            }
        };
        if (action.RequiresServer)
            relationships["mdmServer"] = new JsonObject { ["data"] = new JsonObject { ["type"] = "mdmServers", ["id"] = action.ServerId } };

        return new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "orgDeviceActivities",
                ["attributes"] = attributes,
                ["relationships"] = relationships,
            }
        };
    }

    /// <summary>Submit an activity and wait (up to three minutes) for Apple to finish it.</summary>
    public async Task<AppleOrgActivityResult> PerformAsync(AppleOrgAction action, IReadOnlyList<string> serials, CancellationToken ct = default)
    {
        if (serials.Count == 0) throw new InvalidOperationException("No devices were selected.");
        if (action.IsBusinessOnly && Profile.IsSchool)
            throw new InvalidOperationException("Releasing devices is available only in Apple Business Manager.");
        if (action.RequiresServer && string.IsNullOrEmpty(action.ServerId))
            throw new InvalidOperationException($"{action.Title} needs a device management service.");
        if (action.RequiresDeadline && action.Deadline == null)
            throw new InvalidOperationException($"{action.Title} needs a deadline.");

        Log.Information("{Action}: {Count} device(s) via {Service}", action.Title, serials.Count, Profile.ServiceName);
        var request = new HttpRequestMessage(HttpMethod.Post, "v1/orgDeviceActivities")
        {
            Content = new StringContent(ActivityBody(action, serials).ToJsonString(), Encoding.UTF8, "application/json")
        };
        var created = await SendAsync<AppleSingleResponse<AppleActivityAttributes>>(request, ct)
                      ?? throw new InvalidOperationException("Apple returned no activity.");
        var id = created.Data?.Id ?? throw new InvalidOperationException("Apple returned no activity ID.");
        var status = created.Data.Attributes?.Status ?? "PENDING";

        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (!IsTerminal(status))
        {
            if (DateTimeOffset.UtcNow > deadline) { status = "TIMEOUT"; break; }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            status = (await GetAsync<AppleSingleResponse<AppleActivityAttributes>>($"v1/orgDeviceActivities/{id}", ct))
                     ?.Data?.Attributes?.Status ?? status;
        }
        Log.Information("{Action} activity {Id} ended {Status}", action.Title, id, status);
        return new AppleOrgActivityResult(id, status, serials);
    }

    public static bool IsTerminal(string status) =>
        status.ToUpperInvariant() is "COMPLETE" or "COMPLETED" or "FAILED" or "ERROR" or "STOPPED";

    // ── HTTP ─────────────────────────────────────────────────────────────

    private Task<T?> GetAsync<T>(string url, CancellationToken ct) =>
        SendAsync<T>(new HttpRequestMessage(HttpMethod.Get, url), ct);

    /// <summary>Send with the bearer token, retrying 429 and 5xx with backoff (Retry-After when given).</summary>
    private async Task<T?> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
        for (var attempt = 0; ; attempt++)
        {
            using var message = new HttpRequestMessage(request.Method, request.RequestUri);
            if (body != null) message.Content = new StringContent(body, Encoding.UTF8, "application/json");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));

            using var response = await _http.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
                return string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<T>(text, Json);

            var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { _token = null; continue; }
            if (!retryable || attempt >= 3)
                throw new HttpRequestException($"{Profile.ServiceName}: HTTP {(int)response.StatusCode} on {request.RequestUri}: {Trim(text)}",
                    null, response.StatusCode);

            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));
            await Task.Delay(wait, ct);
        }
    }

    // ── Mapping ──────────────────────────────────────────────────────────

    public static AppleOrgDevice Map(AppleDeviceAttributes a, string orgId, string? assignedServerId) => new()
    {
        SerialNumber = a.SerialNumber,
        OrgId = orgId,
        Model = a.DeviceModel ?? a.LegacyModel ?? "Unknown",
        ProductFamily = a.ProductFamily,
        Status = a.Status,
        AssignedServerId = assignedServerId,
        OrderNumber = a.OrderNumber,
        PurchaseSource = a.PurchaseSourceType,
        AddedToOrg = ParseDate(a.AddedToOrgDateTime),
        OrderDate = ParseDate(a.OrderDateTime),
        IsMigrationCapable = a.IsMdmMigrationCapable,
        MigrationStatus = a.MdmMigrationStatus,
        MigrationDeadline = ParseDate(a.MdmMigrationDeadlineDateTime),
        ReleasedFromOrg = ParseDate(a.ReleasedFromOrgDateTime),
        WifiMacAddresses = a.WifiMacAddress ?? new(),
        EthernetMacAddresses = a.BuiltInEthernetMacAddress ?? new(),
    };

    public static DateTimeOffset? ParseDate(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public static string Iso(DateTimeOffset d) => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";

    public void Dispose()
    {
        _http.Dispose();
        _tokenLock.Dispose();
    }
}
