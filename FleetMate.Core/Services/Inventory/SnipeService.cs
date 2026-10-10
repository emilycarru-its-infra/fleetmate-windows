using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Services;
using Serilog;
using FleetMate.Core.Services.Activity;

namespace FleetMate.Core.Services.Inventory;

/// <summary>
/// Client for Snipe-IT Asset Management API
/// https://snipe-it.readme.io/reference/api-overview
/// </summary>
public class SnipeService : IDisposable
{
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _jsonOptions;
    
    // Caches
    private List<SnipeAsset>? _assetCache;
    private DateTime _assetCacheExpiry = DateTime.MinValue;
    private List<SnipeUser>? _userCache;
    private DateTime _userCacheExpiry = DateTime.MinValue;
    private List<SnipeLocation>? _locationCache;
    private DateTime _locationCacheExpiry = DateTime.MinValue;
    private readonly TimeSpan _cacheDuration;
    
    public string BaseUrl { get; }
    public bool IsConfigured => !string.IsNullOrEmpty(BaseUrl);

    /// <summary>
    /// True when Snipe-IT authenticates with an Entra bearer minted off the
    /// operator's Windows sign-in rather than a shared API key.
    /// </summary>
    public bool UsesOidc { get; }

    /// <summary>
    /// True when requests carry a credential: an Entra audience or a legacy API
    /// key. Without either, Snipe-IT answers every call 401, so the cause is
    /// named here rather than left to read as a refused sign-in.
    /// </summary>
    public bool HasCredential { get; }

    /// <summary>Why Snipe-IT cannot be reached as configured, or null when it can be tried.</summary>
    public string? MissingCredentialReason => HasCredential
        ? null
        : "no Snipe-IT sign-in is set: SnipeOidcAudience (the Entra audience of the Snipe-IT API) is missing";

    /// <summary>
    /// Build from config so every call site authenticates the same way — Entra
    /// SSO by default, the legacy API key only where no audience is configured.
    /// </summary>
    public static SnipeService FromConfig(FleetMate.Core.Config.FleetMateConfig config)
    {
#pragma warning disable CS0618 // legacy fallback for unmigrated configs
        var apiKey = config.SnipeApiKey;
#pragma warning restore CS0618
        return new SnipeService(config.SnipeUrl, apiKey, config.CacheMinutes, config.SnipeOidcAudience);
    }

    public SnipeService(string? baseUrl = null, string? apiKey = null, int cacheMinutes = 5, string? oidcAudience = null)
        : this(baseUrl, apiKey, cacheMinutes, oidcAudience, transport: null)
    {
    }

    /// <summary>Test seam: <paramref name="transport"/> stands in for the network and the token handler.</summary>
    internal SnipeService(string? baseUrl, string? apiKey, int cacheMinutes, string? oidcAudience, HttpMessageHandler? transport)
    {
        BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : ServiceUri.Normalize(baseUrl);
        UsesOidc = !string.IsNullOrWhiteSpace(oidcAudience);
        HasCredential = UsesOidc || !string.IsNullOrWhiteSpace(apiKey);

        // Prefer-bearer: an Entra audience beats a shared key wherever both are
        // set, so migrating an estate is a matter of setting the audience rather
        // than of racing to delete keys everywhere first.
        _client = transport != null
            ? new HttpClient(transport)
            : UsesOidc
                ? new HttpClient(new ActivityLogHandler("Inventory", new EntraBearerHandler(oidcAudience!)))
                : new HttpClient(new ActivityLogHandler("Inventory"));
        _client.Timeout = TimeSpan.FromSeconds(120);

        if (!string.IsNullOrEmpty(BaseUrl))
        {
            _client.BaseAddress = new Uri(BaseUrl);
        }

        if (!UsesOidc && !string.IsNullOrEmpty(apiKey))
        {
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        _jsonOptions.Converters.Add(new SnipeDateConverter());
        _jsonOptions.Converters.Add(new SnipeDateTimeConverter());
        
        _cacheDuration = TimeSpan.FromMinutes(cacheMinutes);
    }

    /// <summary>
    /// Swap the Authorization header to a delegated SSO bearer at runtime.
    /// Overrides the retired static API-key path for this client.
    /// </summary>
    public void SetBearerToken(string token)
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>
    /// Whether Snipe-IT accepts this client's credentials: one asset is asked
    /// for, and anything but success is returned as the reason. The asset
    /// response must also hold a list: a 200 carrying Snipe-IT's error envelope
    /// or a web page is a failure too.
    /// </summary>
    public async Task<string?> CheckAccessAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(BaseUrl)) return "no Snipe-IT address is set";
        if (MissingCredentialReason is { } missing) return missing;
        try
        {
            using var response = await _client.GetAsync("/api/v1/hardware?limit=1", ct);
            await ReadListBodyAsync(response);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Describe(ex);
        }
    }

    /// <summary>
    /// Why the last Snipe-IT call failed, or null when the last list call
    /// succeeded. The calls that predate exceptions still return an empty list
    /// or null on failure; this is what lets their callers tell "nothing there"
    /// from "could not ask", the way the asset and status lists now throw.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>Forget the last failure, before a call whose outcome is to be checked.</summary>
    public void ClearLastError() => LastError = null;

    /// <summary>
    /// The body of a list response, or a <see cref="SnipeException"/> that says
    /// why there is no list in it. Snipe-IT can answer 200 with no list at all:
    /// its own error envelope (<c>{"status":"error","messages":…}</c>), or a
    /// sign-in web page when the address reaches the site but not its API. Both
    /// used to read as an empty inventory.
    /// </summary>
    internal static async Task<string> ReadListBodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var reason = EnvelopeMessage(body);
            var code = (int)response.StatusCode;
            throw new SnipeException(response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                ? $"Snipe-IT refused the sign-in ({code}){(reason is null ? "" : $": {reason}")}"
                : $"Snipe-IT answered {code} {response.ReasonPhrase}{(reason is null ? "" : $": {reason}")}");
        }

        if (LooksLikeHtml(body))
            throw new SnipeException(
                $"Snipe-IT answered {response.RequestMessage?.RequestUri?.GetLeftPart(UriPartial.Path) ?? "the request"} with a web page, not JSON; " +
                "check that SnipeUrl is the Snipe-IT site's address");

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
                return body;
            throw new SnipeException(EnvelopeMessage(body) is { } message
                ? $"Snipe-IT returned an error: {message}"
                : "Snipe-IT returned JSON without a list of rows");
        }
        catch (JsonException ex)
        {
            throw new SnipeException($"Snipe-IT returned something that is not JSON: {ex.Message}", ex);
        }
    }

    /// <summary><see cref="ReadListBodyAsync"/>, parsed.</summary>
    private async Task<SnipeListResponse<T>> ReadListAsync<T>(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<SnipeListResponse<T>>(await ReadListBodyAsync(response), _jsonOptions)
        ?? new SnipeListResponse<T>();

    private static bool LooksLikeHtml(string body)
    {
        var start = body.TrimStart();
        return start.StartsWith('<');
    }

    /// <summary>Snipe-IT's own reason, from its error envelope or a guard's <c>{"error":…}</c>.</summary>
    private static string? EnvelopeMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || LooksLikeHtml(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in new[] { "messages", "message", "error" })
            {
                if (!root.TryGetProperty(name, out var value)) continue;
                switch (value.ValueKind)
                {
                    case JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()):
                        return value.GetString()!.Trim();
                    case JsonValueKind.Object:
                        // Validation errors: {"field": ["reason", …]}
                        return string.Join("; ", value.EnumerateObject()
                            .SelectMany(p => p.Value.ValueKind == JsonValueKind.Array
                                ? p.Value.EnumerateArray().Select(v => v.ToString())
                                : new[] { p.Value.ToString() }));
                }
            }
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>
    /// A failure in the words the Inventory tab and the CLI show. A token that
    /// could not be had is a sign-in failure, not a Snipe-IT one.
    /// </summary>
    public static string Describe(Exception ex) => ex switch
    {
        EntraTokenException token => $"Sign-in failed: {token.Message}",
        HttpRequestException http when http.InnerException is EntraTokenException token => $"Sign-in failed: {token.Message}",
        TaskCanceledException => "Snipe-IT did not answer in time",
        _ => ex.Message,
    };

    private SnipeException Failure(SnipeException ex, string what)
    {
        LastError = ex.Message;
        Log.Warning(ex.InnerException, "[snipe] {What}: {Reason}", what, ex.Message);
        return ex;
    }

    /// <summary>Log a failed call and keep its reason as <see cref="LastError"/>.</summary>
    private void Warn(string template, params object?[] args)
    {
        Log.Warning("[snipe] " + template, args);
        LastError = Render(template, args);
    }

    /// <summary>Log a call that threw and keep its reason as <see cref="LastError"/>.</summary>
    private void Fail(Exception ex, string template, params object?[] args)
    {
        Log.Warning(ex, "[snipe] " + template, args);
        LastError = $"{Render(template, args)}: {Describe(ex)}";
    }

    private static string Render(string template, object?[] args)
    {
        var i = 0;
        return System.Text.RegularExpressions.Regex.Replace(template, @"\{[^}]+\}", _ =>
        {
            if (i >= args.Length) return "";
            var value = args[i++];
            var text = value switch
            {
                System.Net.HttpStatusCode code => $"{(int)code} {code}",
                string str when LooksLikeHtml(str) => "(a web page)",
                _ => value?.ToString() ?? "",
            };
            return text.Length > 300 ? text[..300] + "…" : text;
        });
    }

    #region Hardware/Assets
    
    /// <summary>
    /// Get all hardware assets
    /// </summary>
    public async Task<List<SnipeAsset>> GetAssetsAsync(
        bool forceRefresh = false,
        string? search = null,
        int? statusId = null,
        int? modelId = null,
        int? categoryId = null,
        int? locationId = null,
        int? companyId = null)
    {
        // Use cache only for unfiltered requests
        var hasFilters = !string.IsNullOrEmpty(search) || statusId.HasValue || 
                         modelId.HasValue || categoryId.HasValue || 
                         locationId.HasValue || companyId.HasValue;
        
        if (!forceRefresh && !hasFilters && _assetCache != null && DateTime.UtcNow < _assetCacheExpiry)
        {
            return _assetCache;
        }
        
        if (MissingCredentialReason is { } missing)
            throw Failure(new SnipeException(missing), "Not fetching assets");

        Log.Debug("[snipe] Fetching assets from Snipe-IT...");
        var allAssets = new List<SnipeAsset>();
        var offset = 0;
        const int limit = 500;

        try
        {
            while (true)
            {
                var queryParams = new List<string>
                {
                    $"limit={limit}",
                    $"offset={offset}"
                };

                if (!string.IsNullOrEmpty(search))
                    queryParams.Add($"search={Uri.EscapeDataString(search)}");
                if (statusId.HasValue)
                    queryParams.Add($"status_id={statusId}");
                if (modelId.HasValue)
                    queryParams.Add($"model_id={modelId}");
                if (categoryId.HasValue)
                    queryParams.Add($"category_id={categoryId}");
                if (locationId.HasValue)
                    queryParams.Add($"location_id={locationId}");
                if (companyId.HasValue)
                    queryParams.Add($"company_id={companyId}");

                var url = $"/api/v1/hardware?{string.Join("&", queryParams)}";
                using var response = await _client.GetAsync(url);
                var rawJson = await ReadListBodyAsync(response);

                SnipeListResponse<SnipeAsset> wrapper;
                try
                {
                    wrapper = JsonSerializer.Deserialize<SnipeListResponse<SnipeAsset>>(rawJson, _jsonOptions)
                              ?? new SnipeListResponse<SnipeAsset>();
                }
                catch (JsonException)
                {
                    // One malformed row must not cost the whole list: keep the rest.
                    wrapper = new SnipeListResponse<SnipeAsset>();
                    using var doc = JsonDocument.Parse(rawJson);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("total", out var totalProp) && totalProp.TryGetInt32(out var total))
                        wrapper.Total = total;

                    foreach (var row in root.GetProperty("rows").EnumerateArray())
                    {
                        try
                        {
                            if (row.Deserialize<SnipeAsset>(_jsonOptions) is { } asset)
                                wrapper.Rows.Add(asset);
                        }
                        catch (JsonException ex)
                        {
                            Log.Debug(ex, "[snipe] Skipped an asset row that did not parse");
                        }
                    }
                }
                if (wrapper.Rows.Count == 0)
                    break;

                allAssets.AddRange(wrapper.Rows);

                if (wrapper.Rows.Count < limit || allAssets.Count >= wrapper.Total)
                    break;

                offset += limit;
            }
        }
        catch (Exception ex) when (ex is not SnipeException)
        {
            throw Failure(new SnipeException(Describe(ex), ex), "Failed to fetch assets");
        }
        catch (SnipeException ex)
        {
            throw Failure(ex, "Failed to fetch assets");
        }

        // Cache only unfiltered results
        if (!hasFilters)
        {
            _assetCache = allAssets;
            _assetCacheExpiry = DateTime.UtcNow.Add(_cacheDuration);
        }

        LastError = null;
        Log.Information("[snipe] Retrieved {Count} assets from Snipe-IT", allAssets.Count);
        return allAssets;
    }

    /// <summary>
    /// Get a specific asset by ID
    /// </summary>
    public async Task<SnipeAsset?> GetAssetAsync(int id)
    {
        try
        {
            var response = await _client.GetAsync($"/api/v1/hardware/{id}");
            if (!response.IsSuccessStatusCode)
            {
                Warn("Failed to get asset {Id}: {Status}", id, response.StatusCode);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<SnipeAsset>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to get asset {Id}", id);
            return null;
        }
    }
    
    /// <summary>
    /// Get an asset by asset tag
    /// </summary>
    public async Task<SnipeAsset?> GetAssetByTagAsync(string assetTag)
    {
        try
        {
            var response = await _client.GetAsync($"/api/v1/hardware/bytag/{Uri.EscapeDataString(assetTag)}");
            if (!response.IsSuccessStatusCode)
            {
                Warn("Failed to get asset by tag {Tag}: {Status}", assetTag, response.StatusCode);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<SnipeAsset>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to get asset by tag {Tag}", assetTag);
            return null;
        }
    }
    
    /// <summary>
    /// Get an asset by serial number
    /// </summary>
    public async Task<SnipeAsset?> GetAssetBySerialAsync(string serial)
    {
        try
        {
            var response = await _client.GetAsync($"/api/v1/hardware/byserial/{Uri.EscapeDataString(serial)}");
            if (!response.IsSuccessStatusCode)
            {
                Warn("Failed to get asset by serial {Serial}: {Status}", serial, response.StatusCode);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<SnipeAsset>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to get asset by serial {Serial}", serial);
            return null;
        }
    }
    
    /// <summary>
    /// Create a new asset
    /// </summary>
    public async Task<SnipeResponse<SnipeAsset>?> CreateAssetAsync(SnipeAssetRequest request)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _client.PostAsync("/api/v1/hardware", content);
            return await response.Content.ReadFromJsonAsync<SnipeResponse<SnipeAsset>>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to create asset");
            return null;
        }
    }
    
    /// <summary>
    /// Update an existing asset
    /// </summary>
    public async Task<SnipeResponse?> UpdateAssetAsync(int id, SnipeAssetRequest request)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _client.PutAsync($"/api/v1/hardware/{id}", content);
            return await response.Content.ReadFromJsonAsync<SnipeResponse>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to update asset {Id}", id);
            return null;
        }
    }
    
    /// <summary>
    /// Patch a single field on an asset. The apiField is the payload key —
    /// a native column ("serial", "lease_usage") or a custom field's
    /// _snipeit_* db column name.
    /// </summary>
    public async Task<SnipeResponse?> PatchAssetFieldAsync(int assetId, string apiField, string value)
    {
        try
        {
            var json = JsonSerializer.Serialize(new Dictionary<string, string> { [apiField] = value });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _client.PatchAsync($"/api/v1/hardware/{assetId}", content);
            _assetCache = null;
            return await response.Content.ReadFromJsonAsync<SnipeResponse>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to patch asset {Id} field {Field}", assetId, apiField);
            return null;
        }
    }

    /// <summary>
    /// Custom field definitions — the element types and listbox options the
    /// asset payload doesn't carry.
    /// </summary>
    public async Task<List<SnipeFieldDef>> GetFieldDefinitionsAsync()
    {
        try
        {
            var response = await _client.GetFromJsonAsync<SnipeListResponse<SnipeFieldDef>>(
                "/api/v1/fields?limit=200", _jsonOptions);
            return response?.Rows ?? new List<SnipeFieldDef>();
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to fetch field definitions");
            return new List<SnipeFieldDef>();
        }
    }

    /// <summary>
    /// Delete an asset
    /// </summary>
    public async Task<SnipeResponse?> DeleteAssetAsync(int id)
    {
        try
        {
            var response = await _client.DeleteAsync($"/api/v1/hardware/{id}");
            return await response.Content.ReadFromJsonAsync<SnipeResponse>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to delete asset {Id}", id);
            return null;
        }
    }
    
    /// <summary>
    /// Checkout an asset to a user, location, or another asset
    /// </summary>
    public async Task<SnipeResponse?> CheckoutAssetAsync(int assetId, SnipeCheckoutRequest request)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _client.PostAsync($"/api/v1/hardware/{assetId}/checkout", content);
            return await response.Content.ReadFromJsonAsync<SnipeResponse>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to checkout asset {Id}", assetId);
            return null;
        }
    }
    
    /// <summary>
    /// Checkin an asset
    /// </summary>
    public async Task<SnipeResponse?> CheckinAssetAsync(int assetId, SnipeCheckinRequest? request = null)
    {
        try
        {
            var json = JsonSerializer.Serialize(request ?? new SnipeCheckinRequest(), _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _client.PostAsync($"/api/v1/hardware/{assetId}/checkin", content);
            return await response.Content.ReadFromJsonAsync<SnipeResponse>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to checkin asset {Id}", assetId);
            return null;
        }
    }
    
    /// <summary>
    /// Audit an asset
    /// </summary>
    public async Task<SnipeResponse?> AuditAssetAsync(int assetId, SnipeAuditRequest? request = null)
    {
        try
        {
            var json = JsonSerializer.Serialize(request ?? new SnipeAuditRequest(), _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _client.PostAsync($"/api/v1/hardware/{assetId}/audit", content);
            return await response.Content.ReadFromJsonAsync<SnipeResponse>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to audit asset {Id}", assetId);
            return null;
        }
    }
    
    /// <summary>
    /// Get assets due for audit
    /// </summary>
    public async Task<List<SnipeAsset>> GetAuditDueAsync()
    {
        try
        {
            var response = await _client.GetAsync("/api/v1/hardware/audit/due");
            if (!response.IsSuccessStatusCode)
            {
                Warn("Failed to get audit due assets: {Status}", response.StatusCode);
                return new List<SnipeAsset>();
            }
            var wrapper = await ReadListAsync<SnipeAsset>(response);
            return wrapper?.Rows ?? new List<SnipeAsset>();
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to get audit due assets");
            return new List<SnipeAsset>();
        }
    }
    
    /// <summary>
    /// Get overdue audit assets
    /// </summary>
    public async Task<List<SnipeAsset>> GetAuditOverdueAsync()
    {
        try
        {
            var response = await _client.GetAsync("/api/v1/hardware/audit/overdue");
            if (!response.IsSuccessStatusCode)
            {
                Warn("Failed to get overdue audit assets: {Status}", response.StatusCode);
                return new List<SnipeAsset>();
            }
            var wrapper = await ReadListAsync<SnipeAsset>(response);
            return wrapper?.Rows ?? new List<SnipeAsset>();
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to get overdue audit assets");
            return new List<SnipeAsset>();
        }
    }
    
    #endregion
    
    #region Users
    
    /// <summary>
    /// Get all users
    /// </summary>
    public async Task<List<SnipeUser>> GetUsersAsync(
        bool forceRefresh = false,
        string? search = null,
        int? departmentId = null,
        int? companyId = null,
        int? locationId = null)
    {
        var hasFilters = !string.IsNullOrEmpty(search) || departmentId.HasValue || 
                         companyId.HasValue || locationId.HasValue;
        
        if (!forceRefresh && !hasFilters && _userCache != null && DateTime.UtcNow < _userCacheExpiry)
        {
            return _userCache;
        }
        
        Log.Debug("[snipe] Fetching users from Snipe-IT...");
        var allUsers = new List<SnipeUser>();
        var offset = 0;
        const int limit = 500;
        
        try
        {
            while (true)
            {
                var queryParams = new List<string>
                {
                    $"limit={limit}",
                    $"offset={offset}"
                };
                
                if (!string.IsNullOrEmpty(search))
                    queryParams.Add($"search={Uri.EscapeDataString(search)}");
                if (departmentId.HasValue)
                    queryParams.Add($"department_id={departmentId}");
                if (companyId.HasValue)
                    queryParams.Add($"company_id={companyId}");
                if (locationId.HasValue)
                    queryParams.Add($"location_id={locationId}");
                
                var url = $"/api/v1/users?{string.Join("&", queryParams)}";
                var response = await _client.GetAsync(url);
                
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Warn("Failed to fetch users: {Status} - {Error}", response.StatusCode, error);
                    break;
                }
                
                var wrapper = await ReadListAsync<SnipeUser>(response);
                if (wrapper?.Rows == null || wrapper.Rows.Count == 0)
                    break;
                
                allUsers.AddRange(wrapper.Rows);
                
                if (wrapper.Rows.Count < limit || allUsers.Count >= wrapper.Total)
                    break;
                
                offset += limit;
            }
            
            if (!hasFilters)
            {
                _userCache = allUsers;
                _userCacheExpiry = DateTime.UtcNow.Add(_cacheDuration);
            }
            
            Log.Information("[snipe] Retrieved {Count} users from Snipe-IT", allUsers.Count);
            return allUsers;
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to fetch users from Snipe-IT");
            return _userCache ?? new List<SnipeUser>();
        }
    }
    
    /// <summary>
    /// Get a specific user by ID
    /// </summary>
    public async Task<SnipeUser?> GetUserAsync(int id)
    {
        try
        {
            var response = await _client.GetAsync($"/api/v1/users/{id}");
            if (!response.IsSuccessStatusCode)
            {
                Warn("Failed to get user {Id}: {Status}", id, response.StatusCode);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<SnipeUser>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to get user {Id}", id);
            return null;
        }
    }
    
    /// <summary>
    /// Get the current API user
    /// </summary>
    public async Task<SnipeUser?> GetCurrentUserAsync()
    {
        try
        {
            var response = await _client.GetAsync("/api/v1/users/me");
            if (!response.IsSuccessStatusCode)
            {
                Warn("Failed to get current user: {Status}", response.StatusCode);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<SnipeUser>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to get current user");
            return null;
        }
    }
    
    /// <summary>
    /// Get assets checked out to a user
    /// </summary>
    public async Task<List<SnipeAsset>> GetUserAssetsAsync(int userId)
    {
        try
        {
            var response = await _client.GetAsync($"/api/v1/users/{userId}/assets");
            if (!response.IsSuccessStatusCode)
            {
                Warn("Failed to get user assets: {Status}", response.StatusCode);
                return new List<SnipeAsset>();
            }
            var wrapper = await ReadListAsync<SnipeAsset>(response);
            return wrapper?.Rows ?? new List<SnipeAsset>();
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to get user {UserId} assets", userId);
            return new List<SnipeAsset>();
        }
    }
    
    /// <summary>
    /// Create a new user
    /// </summary>
    public async Task<SnipeResponse<SnipeUser>?> CreateUserAsync(SnipeUserRequest request)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _client.PostAsync("/api/v1/users", content);
            return await response.Content.ReadFromJsonAsync<SnipeResponse<SnipeUser>>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to create user");
            return null;
        }
    }
    
    #endregion
    
    #region Locations
    
    /// <summary>
    /// Get all locations
    /// </summary>
    public async Task<List<SnipeLocation>> GetLocationsAsync(bool forceRefresh = false, string? search = null)
    {
        var hasFilters = !string.IsNullOrEmpty(search);
        
        if (!forceRefresh && !hasFilters && _locationCache != null && DateTime.UtcNow < _locationCacheExpiry)
        {
            return _locationCache;
        }
        
        Log.Debug("[snipe] Fetching locations from Snipe-IT...");
        var allLocations = new List<SnipeLocation>();
        var offset = 0;
        const int limit = 500;
        
        try
        {
            while (true)
            {
                var queryParams = new List<string>
                {
                    $"limit={limit}",
                    $"offset={offset}"
                };
                
                if (!string.IsNullOrEmpty(search))
                    queryParams.Add($"search={Uri.EscapeDataString(search)}");
                
                var url = $"/api/v1/locations?{string.Join("&", queryParams)}";
                var response = await _client.GetAsync(url);
                
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Warn("Failed to fetch locations: {Status} - {Error}", response.StatusCode, error);
                    break;
                }
                
                var wrapper = await ReadListAsync<SnipeLocation>(response);
                if (wrapper?.Rows == null || wrapper.Rows.Count == 0)
                    break;
                
                allLocations.AddRange(wrapper.Rows);
                
                if (wrapper.Rows.Count < limit || allLocations.Count >= wrapper.Total)
                    break;
                
                offset += limit;
            }
            
            if (!hasFilters)
            {
                _locationCache = allLocations;
                _locationCacheExpiry = DateTime.UtcNow.Add(_cacheDuration);
            }
            
            Log.Information("[snipe] Retrieved {Count} locations from Snipe-IT", allLocations.Count);
            return allLocations;
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to fetch locations from Snipe-IT");
            return _locationCache ?? new List<SnipeLocation>();
        }
    }
    
    /// <summary>
    /// Get a specific location by ID
    /// </summary>
    public async Task<SnipeLocation?> GetLocationAsync(int id)
    {
        try
        {
            var response = await _client.GetAsync($"/api/v1/locations/{id}");
            if (!response.IsSuccessStatusCode)
            {
                Warn("Failed to get location {Id}: {Status}", id, response.StatusCode);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<SnipeLocation>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to get location {Id}", id);
            return null;
        }
    }
    
    #endregion
    
    #region Models
    
    /// <summary>
    /// Get all asset models
    /// </summary>
    public async Task<List<SnipeModel>> GetModelsAsync(string? search = null, int? categoryId = null, int? manufacturerId = null)
    {
        Log.Debug("[snipe] Fetching models from Snipe-IT...");
        var allModels = new List<SnipeModel>();
        var offset = 0;
        const int limit = 500;
        
        try
        {
            while (true)
            {
                var queryParams = new List<string>
                {
                    $"limit={limit}",
                    $"offset={offset}"
                };
                
                if (!string.IsNullOrEmpty(search))
                    queryParams.Add($"search={Uri.EscapeDataString(search)}");
                if (categoryId.HasValue)
                    queryParams.Add($"category_id={categoryId}");
                if (manufacturerId.HasValue)
                    queryParams.Add($"manufacturer_id={manufacturerId}");
                
                var url = $"/api/v1/models?{string.Join("&", queryParams)}";
                var response = await _client.GetAsync(url);
                
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Warn("Failed to fetch models: {Status} - {Error}", response.StatusCode, error);
                    break;
                }
                
                var wrapper = await ReadListAsync<SnipeModel>(response);
                if (wrapper?.Rows == null || wrapper.Rows.Count == 0)
                    break;
                
                allModels.AddRange(wrapper.Rows);
                
                if (wrapper.Rows.Count < limit || allModels.Count >= wrapper.Total)
                    break;
                
                offset += limit;
            }
            
            Log.Information("[snipe] Retrieved {Count} models from Snipe-IT", allModels.Count);
            return allModels;
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to fetch models from Snipe-IT");
            return new List<SnipeModel>();
        }
    }
    
    #endregion
    
    #region Licenses
    
    /// <summary>
    /// Get all licenses
    /// </summary>
    public async Task<List<SnipeLicense>> GetLicensesAsync(string? search = null, int? categoryId = null, int? manufacturerId = null)
    {
        Log.Debug("[snipe] Fetching licenses from Snipe-IT...");
        var allLicenses = new List<SnipeLicense>();
        var offset = 0;
        const int limit = 500;
        
        try
        {
            while (true)
            {
                var queryParams = new List<string>
                {
                    $"limit={limit}",
                    $"offset={offset}"
                };
                
                if (!string.IsNullOrEmpty(search))
                    queryParams.Add($"search={Uri.EscapeDataString(search)}");
                if (categoryId.HasValue)
                    queryParams.Add($"category_id={categoryId}");
                if (manufacturerId.HasValue)
                    queryParams.Add($"manufacturer_id={manufacturerId}");
                
                var url = $"/api/v1/licenses?{string.Join("&", queryParams)}";
                var response = await _client.GetAsync(url);
                
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Warn("Failed to fetch licenses: {Status} - {Error}", response.StatusCode, error);
                    break;
                }
                
                var wrapper = await ReadListAsync<SnipeLicense>(response);
                if (wrapper?.Rows == null || wrapper.Rows.Count == 0)
                    break;
                
                allLicenses.AddRange(wrapper.Rows);
                
                if (wrapper.Rows.Count < limit || allLicenses.Count >= wrapper.Total)
                    break;
                
                offset += limit;
            }
            
            Log.Information("[snipe] Retrieved {Count} licenses from Snipe-IT", allLicenses.Count);
            return allLicenses;
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to fetch licenses from Snipe-IT");
            return new List<SnipeLicense>();
        }
    }
    
    /// <summary>
    /// Get license seats
    /// </summary>
    public async Task<List<SnipeLicenseSeat>> GetLicenseSeatsAsync(int licenseId)
    {
        try
        {
            var response = await _client.GetAsync($"/api/v1/licenses/{licenseId}/seats");
            if (!response.IsSuccessStatusCode)
            {
                Warn("Failed to get license seats: {Status}", response.StatusCode);
                return new List<SnipeLicenseSeat>();
            }
            var wrapper = await ReadListAsync<SnipeLicenseSeat>(response);
            return wrapper?.Rows ?? new List<SnipeLicenseSeat>();
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to get license {LicenseId} seats", licenseId);
            return new List<SnipeLicenseSeat>();
        }
    }
    
    #endregion
    
    #region Categories
    
    /// <summary>
    /// Get all categories
    /// </summary>
    public async Task<List<SnipeCategory>> GetCategoriesAsync(string? search = null, string? categoryType = null)
    {
        Log.Debug("[snipe] Fetching categories from Snipe-IT...");
        var allCategories = new List<SnipeCategory>();
        var offset = 0;
        const int limit = 500;
        
        try
        {
            while (true)
            {
                var queryParams = new List<string>
                {
                    $"limit={limit}",
                    $"offset={offset}"
                };
                
                if (!string.IsNullOrEmpty(search))
                    queryParams.Add($"search={Uri.EscapeDataString(search)}");
                if (!string.IsNullOrEmpty(categoryType))
                    queryParams.Add($"category_type={categoryType}");
                
                var url = $"/api/v1/categories?{string.Join("&", queryParams)}";
                var response = await _client.GetAsync(url);
                
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Warn("Failed to fetch categories: {Status} - {Error}", response.StatusCode, error);
                    break;
                }
                
                var wrapper = await ReadListAsync<SnipeCategory>(response);
                if (wrapper?.Rows == null || wrapper.Rows.Count == 0)
                    break;
                
                allCategories.AddRange(wrapper.Rows);
                
                if (wrapper.Rows.Count < limit || allCategories.Count >= wrapper.Total)
                    break;
                
                offset += limit;
            }
            
            Log.Information("[snipe] Retrieved {Count} categories from Snipe-IT", allCategories.Count);
            return allCategories;
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to fetch categories from Snipe-IT");
            return new List<SnipeCategory>();
        }
    }
    
    #endregion
    
    #region Manufacturers
    
    /// <summary>
    /// Get all manufacturers
    /// </summary>
    public async Task<List<SnipeManufacturer>> GetManufacturersAsync(string? search = null)
    {
        Log.Debug("[snipe] Fetching manufacturers from Snipe-IT...");
        var allManufacturers = new List<SnipeManufacturer>();
        var offset = 0;
        const int limit = 500;
        
        try
        {
            while (true)
            {
                var queryParams = new List<string>
                {
                    $"limit={limit}",
                    $"offset={offset}"
                };
                
                if (!string.IsNullOrEmpty(search))
                    queryParams.Add($"search={Uri.EscapeDataString(search)}");
                
                var url = $"/api/v1/manufacturers?{string.Join("&", queryParams)}";
                var response = await _client.GetAsync(url);
                
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Warn("Failed to fetch manufacturers: {Status} - {Error}", response.StatusCode, error);
                    break;
                }
                
                var wrapper = await ReadListAsync<SnipeManufacturer>(response);
                if (wrapper?.Rows == null || wrapper.Rows.Count == 0)
                    break;
                
                allManufacturers.AddRange(wrapper.Rows);
                
                if (wrapper.Rows.Count < limit || allManufacturers.Count >= wrapper.Total)
                    break;
                
                offset += limit;
            }
            
            Log.Information("[snipe] Retrieved {Count} manufacturers from Snipe-IT", allManufacturers.Count);
            return allManufacturers;
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to fetch manufacturers from Snipe-IT");
            return new List<SnipeManufacturer>();
        }
    }
    
    #endregion
    
    #region Status Labels
    
    /// <summary>
    /// Get all status labels
    /// </summary>
    public async Task<List<SnipeStatusLabelFull>> GetStatusLabelsAsync(string? statusType = null)
    {
        if (MissingCredentialReason is { } missing)
            throw Failure(new SnipeException(missing), "Not fetching status labels");

        Log.Debug("[snipe] Fetching status labels from Snipe-IT...");
        var allLabels = new List<SnipeStatusLabelFull>();
        var offset = 0;
        const int limit = 500;

        try
        {
            while (true)
            {
                var queryParams = new List<string>
                {
                    $"limit={limit}",
                    $"offset={offset}"
                };

                if (!string.IsNullOrEmpty(statusType))
                    queryParams.Add($"status_type={statusType}");

                var url = $"/api/v1/statuslabels?{string.Join("&", queryParams)}";
                using var response = await _client.GetAsync(url);
                var wrapper = JsonSerializer.Deserialize<SnipeListResponse<SnipeStatusLabelFull>>(
                    await ReadListBodyAsync(response), _jsonOptions) ?? new SnipeListResponse<SnipeStatusLabelFull>();
                if (wrapper.Rows.Count == 0)
                    break;

                allLabels.AddRange(wrapper.Rows);

                if (wrapper.Rows.Count < limit || allLabels.Count >= wrapper.Total)
                    break;

                offset += limit;
            }
        }
        catch (Exception ex) when (ex is not SnipeException)
        {
            throw Failure(new SnipeException(Describe(ex), ex), "Failed to fetch status labels");
        }
        catch (SnipeException ex)
        {
            throw Failure(ex, "Failed to fetch status labels");
        }

        LastError = null;
        Log.Information("[snipe] Retrieved {Count} status labels from Snipe-IT", allLabels.Count);
        return allLabels;
    }
    
    #endregion
    
    #region Accessories, Consumables, Components
    
    /// <summary>
    /// Get all accessories
    /// </summary>
    public async Task<List<SnipeAccessory>> GetAccessoriesAsync(string? search = null)
    {
        Log.Debug("[snipe] Fetching accessories from Snipe-IT...");
        try
        {
            var queryParams = new List<string> { "limit=500" };
            if (!string.IsNullOrEmpty(search))
                queryParams.Add($"search={Uri.EscapeDataString(search)}");
            
            var response = await _client.GetAsync($"/api/v1/accessories?{string.Join("&", queryParams)}");
            if (!response.IsSuccessStatusCode)
            {
                Warn("Failed to fetch accessories: {Status}", response.StatusCode);
                return new List<SnipeAccessory>();
            }
            var wrapper = await ReadListAsync<SnipeAccessory>(response);
            return wrapper?.Rows ?? new List<SnipeAccessory>();
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to fetch accessories from Snipe-IT");
            return new List<SnipeAccessory>();
        }
    }
    
    /// <summary>
    /// Get all consumables
    /// </summary>
    public async Task<List<SnipeConsumable>> GetConsumablesAsync(string? search = null)
    {
        Log.Debug("[snipe] Fetching consumables from Snipe-IT...");
        try
        {
            var queryParams = new List<string> { "limit=500" };
            if (!string.IsNullOrEmpty(search))
                queryParams.Add($"search={Uri.EscapeDataString(search)}");
            
            var response = await _client.GetAsync($"/api/v1/consumables?{string.Join("&", queryParams)}");
            if (!response.IsSuccessStatusCode)
            {
                Warn("Failed to fetch consumables: {Status}", response.StatusCode);
                return new List<SnipeConsumable>();
            }
            var wrapper = await ReadListAsync<SnipeConsumable>(response);
            return wrapper?.Rows ?? new List<SnipeConsumable>();
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to fetch consumables from Snipe-IT");
            return new List<SnipeConsumable>();
        }
    }
    
    /// <summary>
    /// Get all components
    /// </summary>
    public async Task<List<SnipeComponent>> GetComponentsAsync(string? search = null)
    {
        Log.Debug("[snipe] Fetching components from Snipe-IT...");
        try
        {
            var queryParams = new List<string> { "limit=500" };
            if (!string.IsNullOrEmpty(search))
                queryParams.Add($"search={Uri.EscapeDataString(search)}");
            
            var response = await _client.GetAsync($"/api/v1/components?{string.Join("&", queryParams)}");
            if (!response.IsSuccessStatusCode)
            {
                Warn("Failed to fetch components: {Status}", response.StatusCode);
                return new List<SnipeComponent>();
            }
            var wrapper = await ReadListAsync<SnipeComponent>(response);
            return wrapper?.Rows ?? new List<SnipeComponent>();
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to fetch components from Snipe-IT");
            return new List<SnipeComponent>();
        }
    }
    
    #endregion
    
    #region Activity & Maintenance
    
    /// <summary>
    /// Get activity log
    /// </summary>
    public async Task<List<SnipeActivity>> GetActivityAsync(
        string? search = null,
        string? actionType = null,
        int? targetId = null,
        string? targetType = null,
        int limit = 50)
    {
        Log.Debug("[snipe] Fetching activity from Snipe-IT...");
        try
        {
            var queryParams = new List<string> { $"limit={limit}" };
            if (!string.IsNullOrEmpty(search))
                queryParams.Add($"search={Uri.EscapeDataString(search)}");
            if (!string.IsNullOrEmpty(actionType))
                queryParams.Add($"action_type={actionType}");
            if (targetId.HasValue)
                queryParams.Add($"target_id={targetId}");
            if (!string.IsNullOrEmpty(targetType))
                queryParams.Add($"target_type={Uri.EscapeDataString(targetType)}");
            
            var response = await _client.GetAsync($"/api/v1/reports/activity?{string.Join("&", queryParams)}");
            if (!response.IsSuccessStatusCode)
            {
                Warn("Failed to fetch activity: {Status}", response.StatusCode);
                return new List<SnipeActivity>();
            }
            // The activity report replies with "charset=utf8" (not "utf-8"),
            // which both ReadFromJsonAsync AND ReadAsStringAsync reject as an
            // unknown encoding — so read raw bytes, which ignore the header,
            // and deserialize them ourselves.
            var bytes = await response.Content.ReadAsByteArrayAsync();
            var wrapper = System.Text.Json.JsonSerializer.Deserialize<SnipeListResponse<SnipeActivity>>(bytes, _jsonOptions);
            return wrapper?.Rows ?? new List<SnipeActivity>();
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to fetch activity from Snipe-IT");
            return new List<SnipeActivity>();
        }
    }
    
    /// <summary>
    /// Change history for one asset, newest first. Reads the asset's own
    /// history endpoint and falls back to the activity report filtered to the
    /// asset where that endpoint is missing or refused.
    /// </summary>
    public async Task<List<SnipeActivity>> GetAssetHistoryAsync(int assetId, int limit = 500)
    {
        var rows = await GetActivityRowsAsync(
            $"/api/v1/hardware/{assetId}/history?limit={limit}&order=desc&sort=created_at");
        return rows ?? await GetActivityRowsAsync(
            $"/api/v1/reports/activity?item_type=asset&item_id={assetId}&limit={limit}&order=desc&sort=created_at")
            ?? new List<SnipeActivity>();
    }

    /// <summary>Null when the request fails, so a caller can try another route.</summary>
    private async Task<List<SnipeActivity>?> GetActivityRowsAsync(string url)
    {
        try
        {
            var response = await _client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Warn("Activity request {Url} failed: {Status}", url, response.StatusCode);
                return null;
            }
            // Same "charset=utf8" header as the activity report — read raw bytes.
            // Snipe reports some errors as 200 {"status":"error"}; no rows
            // array means this route did not answer.
            var bytes = await response.Content.ReadAsByteArrayAsync();
            using var doc = JsonDocument.Parse(bytes);
            if (!doc.RootElement.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                Warn("Activity request {Url} returned no rows", url);
                return null;
            }
            return rows.Deserialize<List<SnipeActivity>>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Fail(ex, "Activity request {Url} failed", url);
            return null;
        }
    }
    
    /// <summary>
    /// Get maintenance records
    /// </summary>
    public async Task<List<SnipeMaintenance>> GetMaintenancesAsync(int? assetId = null)
    {
        Log.Debug("[snipe] Fetching maintenances from Snipe-IT...");
        try
        {
            var queryParams = new List<string> { "limit=500" };
            if (assetId.HasValue)
                queryParams.Add($"asset_id={assetId}");
            
            var response = await _client.GetAsync($"/api/v1/maintenances?{string.Join("&", queryParams)}");
            if (!response.IsSuccessStatusCode)
            {
                Warn("Failed to fetch maintenances: {Status}", response.StatusCode);
                return new List<SnipeMaintenance>();
            }
            var wrapper = await ReadListAsync<SnipeMaintenance>(response);
            return wrapper?.Rows ?? new List<SnipeMaintenance>();
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to fetch maintenances from Snipe-IT");
            return new List<SnipeMaintenance>();
        }
    }
    
    #endregion
    
    #region Companies & Departments
    
    /// <summary>
    /// Get all companies
    /// </summary>
    public async Task<List<SnipeCompany>> GetCompaniesAsync(string? search = null)
    {
        Log.Debug("[snipe] Fetching companies from Snipe-IT...");
        try
        {
            var queryParams = new List<string> { "limit=500" };
            if (!string.IsNullOrEmpty(search))
                queryParams.Add($"search={Uri.EscapeDataString(search)}");
            
            var response = await _client.GetAsync($"/api/v1/companies?{string.Join("&", queryParams)}");
            if (!response.IsSuccessStatusCode)
            {
                Warn("Failed to fetch companies: {Status}", response.StatusCode);
                return new List<SnipeCompany>();
            }
            var wrapper = await ReadListAsync<SnipeCompany>(response);
            return wrapper?.Rows ?? new List<SnipeCompany>();
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to fetch companies from Snipe-IT");
            return new List<SnipeCompany>();
        }
    }
    
    /// <summary>
    /// Get all departments
    /// </summary>
    public async Task<List<SnipeDepartment>> GetDepartmentsAsync(string? search = null, int? companyId = null)
    {
        Log.Debug("[snipe] Fetching departments from Snipe-IT...");
        try
        {
            var queryParams = new List<string> { "limit=500" };
            if (!string.IsNullOrEmpty(search))
                queryParams.Add($"name={Uri.EscapeDataString(search)}");
            if (companyId.HasValue)
                queryParams.Add($"company_id={companyId}");
            
            var response = await _client.GetAsync($"/api/v1/departments?{string.Join("&", queryParams)}");
            if (!response.IsSuccessStatusCode)
            {
                Warn("Failed to fetch departments: {Status}", response.StatusCode);
                return new List<SnipeDepartment>();
            }
            var wrapper = await ReadListAsync<SnipeDepartment>(response);
            return wrapper?.Rows ?? new List<SnipeDepartment>();
        }
        catch (Exception ex)
        {
            Fail(ex, "Failed to fetch departments from Snipe-IT");
            return new List<SnipeDepartment>();
        }
    }
    
    #endregion
    
    public void Dispose()
    {
        _client.Dispose();
    }
}

/// <summary>
/// A Snipe-IT call that could not produce what was asked for, with the reason
/// in words the Inventory tab and the CLI can show as they are.
/// </summary>
public sealed class SnipeException : Exception
{
    public SnipeException(string message, Exception? inner = null) : base(message, inner) { }
}
