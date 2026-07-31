#nullable disable warnings
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Models.Tickets;
using Serilog;

namespace FleetMate.Core.Services.Tickets;

/// <summary>
/// TeamDynamix (TDX) service for ticket management
/// Uses JWT authentication via SSO, username/password, or BEID
/// </summary>
public class TdxService : IDisposable
{
    private readonly HttpClient _client;
    private readonly TdxConfig _config;
    private readonly JsonSerializerOptions _jsonOptions;
    // The service-account token cache is gone with the service account itself —
    // the operator's SSO token in _ssoToken is the only credential now.
    
    // SSO authentication state
    private string? _ssoToken;
    private DateTime _ssoTokenExpiry = DateTime.MinValue;
    private string? _ssoUserId;
    private string? _ssoUserName;

    // Reference data caches
    private readonly Dictionary<int, string> _statusCache = new();
    private readonly Dictionary<int, string> _typeCache = new();
    private readonly Dictionary<int, string> _priorityCache = new();
    private DateTime _refDataExpiry = DateTime.MinValue;
    private readonly TimeSpan _cacheDuration;
    
    /// <summary>
    /// Returns true if SSO authentication is active and valid
    /// </summary>
    public bool IsSsoAuthenticated => !string.IsNullOrEmpty(_ssoToken) && DateTime.UtcNow < _ssoTokenExpiry;
    
    /// <summary>
    /// The authenticated SSO user's display name
    /// </summary>
    public string? AuthenticatedUserName => IsSsoAuthenticated ? _ssoUserName : null;
    
    /// <summary>
    /// The authenticated SSO user's ID
    /// </summary>
    public string? AuthenticatedUserId => IsSsoAuthenticated ? _ssoUserId : null;
    
    /// <summary>
    /// True when TDX is configured but nobody is signed in. SSO is the only way
    /// in, so an unauthenticated TDX is always waiting on a sign-in.
    /// </summary>
    public bool RequiresSsoLogin => _config.SsoEnabled && !IsSsoAuthenticated;

    /// <summary>
    /// Always true where TDX is configured — there is no other credential to try.
    /// </summary>
    public bool ShouldAttemptSso => _config.SsoEnabled;

    public TdxService(TdxConfig config)
    {
        _config = config;
        _cacheDuration = TimeSpan.FromMinutes(config.CacheMinutes);

        _client = new HttpClient
        {
            BaseAddress = new Uri(config.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(60)
        };

        Log.Information("TDX configuration: BaseUrl={BaseUrl} AppId={AppId}", config.BaseUrl, config.AppId);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    #region Authentication
    
    /// <summary>
    /// Set SSO token from external SSO login flow
    /// </summary>
    public void SetSsoToken(string token, DateTime expiry, string? userId = null, string? userName = null)
    {
        _ssoToken = token;
        _ssoTokenExpiry = expiry;
        _ssoUserId = userId;
        _ssoUserName = userName;
        Log.Information("TDX SSO token set for user: {UserName}", userName ?? "(unknown)");
    }
    
    /// <summary>
    /// Clear SSO authentication state
    /// </summary>
    public void ClearSsoToken()
    {
        _ssoToken = null;
        _ssoTokenExpiry = DateTime.MinValue;
        _ssoUserId = null;
        _ssoUserName = null;
        Log.Debug("TDX SSO token cleared");
    }

    /// <summary>
    /// Authenticate and get JWT bearer token
    /// </summary>
    private async Task<string?> GetAccessTokenAsync()
    {
        // The operator's own SSO token is the only credential. If it is valid,
        // use it; if it is not, there is nothing to fall back to, and saying so
        // is the honest answer.
        //
        // There used to be a service-account chain here — loginadmin with a
        // BEID/WebServicesKey pair, then a username/password login, both sourced
        // from config, environment or Key Vault. It made every TDX action look
        // like it came from one shared identity, which is exactly what an audit
        // trail must not do. It also silently masked SSO failures: a broken
        // sign-in still "worked", so nobody noticed until attribution mattered.
        if (!string.IsNullOrEmpty(_ssoToken) && DateTime.UtcNow < _ssoTokenExpiry)
        {
            return _ssoToken;
        }

        // Try the silent SSO chain — Negotiate/Kerberos into loginsso — before
        // giving up. On a domain-joined machine this needs no interaction.
        if (!string.IsNullOrEmpty(_config.BaseUrl))
        {
            try
            {
                var sso = new TdxSsoService(_config.BaseUrl);
                var result = await sso.TrySilentSsoAsync();
                if (result.Success && !string.IsNullOrEmpty(result.Token))
                {
                    SetSsoToken(result.Token, result.Expiry, result.UserEmail, result.UserName);
                    Log.Information("[tdx] Acquired a TDX token via silent SSO as {User}",
                        result.UserName ?? "(unknown)");
                    return _ssoToken;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[tdx] Silent SSO failed");
            }
        }

        Log.Warning("[tdx] No TDX credential — sign in to TeamDynamix. " +
                    "There is no service account to fall back to by design.");
        return null;
    }

    private async Task<bool> SetAuthorizationAsync()
    {
        var token = await GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return true;
    }

    #endregion

    #region Assets

    /// <summary>
    /// Search for assets (partial results)
    /// </summary>
    public async Task<List<TdxAsset>> SearchAssetsAsync(string? searchText = null, int maxResults = 50)
    {
        if (!await SetAuthorizationAsync())
        {
            return new List<TdxAsset>();
        }

        try
        {
            var externalIdSearch = new TdxAssetSearchRequest
            {
                ExternalIds = string.IsNullOrWhiteSpace(searchText)
                    ? null
                    : new List<string> { searchText },
                MaxResults = maxResults
            };

            var assets = await PostAssetSearchAsync(externalIdSearch);
            if (assets.Count == 0 && !string.IsNullOrWhiteSpace(searchText))
            {
                var textSearch = new TdxAssetSearchRequest
                {
                    SearchText = searchText,
                    MaxResults = maxResults
                };

                assets = await PostAssetSearchAsync(textSearch);
            }

            return assets;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to search TDX assets");
            return new List<TdxAsset>();
        }
    }

    private async Task<List<TdxAsset>> PostAssetSearchAsync(TdxAssetSearchRequest request)
    {
        var url = _config.GetAssetsUrl("search");
        var content = new StringContent(JsonSerializer.Serialize(request, _jsonOptions), Encoding.UTF8, "application/json");
        var response = await _client.PostAsync(url, content);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            Log.Warning("TDX asset search failed: {Status} - {Error}", response.StatusCode, error);
            return new List<TdxAsset>();
        }

        var rawJson = await response.Content.ReadAsStringAsync();
        var assets = ParseAssetResponse(rawJson);
        if (assets.Count == 0)
        {
            var requestJson = JsonSerializer.Serialize(request, _jsonOptions);
            Log.Warning("TDX asset search returned no results. Request: {Request} Response: {Response}", requestJson, rawJson);
        }

        return assets;
    }

    private List<TdxAsset> ParseAssetResponse(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new List<TdxAsset>();
        }

        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            var assets = JsonSerializer.Deserialize<List<TdxAsset>>(rawJson, _jsonOptions);
            return assets ?? new List<TdxAsset>();
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "Items", "Assets", "Results", "Data", "Value" })
            {
                if (root.TryGetProperty(propertyName, out var property))
                {
                    if (property.ValueKind == JsonValueKind.Array)
                    {
                        var assets = JsonSerializer.Deserialize<List<TdxAsset>>(property.GetRawText(), _jsonOptions);
                        if (assets != null)
                        {
                            return assets;
                        }
                    }

                    if (property.ValueKind == JsonValueKind.Object)
                    {
                        var asset = JsonSerializer.Deserialize<TdxAsset>(property.GetRawText(), _jsonOptions);
                        if (asset != null)
                        {
                            return new List<TdxAsset> { asset };
                        }
                    }
                }
            }

            var singleAsset = JsonSerializer.Deserialize<TdxAsset>(rawJson, _jsonOptions);
            if (singleAsset != null)
            {
                return new List<TdxAsset> { singleAsset };
            }
        }

        return new List<TdxAsset>();
    }

    /// <summary>
    /// Get an asset by ID
    /// </summary>
    public async Task<TdxAsset?> GetAssetAsync(int assetId)
    {
        if (!await SetAuthorizationAsync())
        {
            return null;
        }

        try
        {
            var url = _config.GetAssetsUrl(assetId.ToString());
            var response = await _client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Warning("TDX asset lookup failed: {Status} - {Error}", response.StatusCode, error);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TdxAsset>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get TDX asset");
            return null;
        }
    }

    #endregion

    #region Tickets

    /// <summary>
    /// Search for tickets
    /// </summary>
    public async Task<List<TdxTicket>> SearchTicketsAsync(TicketSearchRequest? search = null, int maxResults = 50)
    {
        if (!await SetAuthorizationAsync())
        {
            return new List<TdxTicket>();
        }

        try
        {
            search ??= new TicketSearchRequest();
            search.MaxResults = maxResults;

            var url = _config.GetTicketsUrl("search");
            var content = new StringContent(JsonSerializer.Serialize(search, _jsonOptions), Encoding.UTF8, "application/json");

            var response = await _client.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Warning("Failed to search tickets: {Status} - {Error}", response.StatusCode, error);
                return new List<TdxTicket>();
            }

            var tickets = await response.Content.ReadFromJsonAsync<List<TdxTicket>>(_jsonOptions);
            Log.Debug("Found {Count} tickets", tickets?.Count ?? 0);
            return tickets ?? new List<TdxTicket>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to search tickets");
            return new List<TdxTicket>();
        }
    }

    /// <summary>
    /// Get a specific ticket by ID
    /// </summary>
    public async Task<TdxTicket?> GetTicketAsync(int ticketId)
    {
        if (!await SetAuthorizationAsync())
        {
            return null;
        }

        try
        {
            var url = _config.GetTicketsUrl(ticketId.ToString());
            var response = await _client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Warning("Failed to get ticket {Id}: {Status} - {Error}", ticketId, response.StatusCode, error);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TdxTicket>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get ticket {Id}", ticketId);
            return null;
        }
    }

    /// <summary>
    /// Create a new ticket
    /// </summary>
    public async Task<TdxTicket?> CreateTicketAsync(CreateTicketRequest request)
    {
        if (!await SetAuthorizationAsync())
        {
            return null;
        }

        try
        {
            // Apply defaults from config
            request.TypeId = request.TypeId > 0 ? request.TypeId : _config.DefaultTypeId ?? 0;
            request.SourceId ??= _config.DefaultSourceId;
            request.PriorityId ??= _config.DefaultPriorityId;
            request.StatusId ??= _config.DefaultStatusId;
            request.AccountId ??= _config.DefaultAccountId;

            if (request.TypeId <= 0)
            {
                Log.Error("TypeId is required to create a ticket");
                return null;
            }

            var url = _config.GetTicketsUrl();
            var content = new StringContent(JsonSerializer.Serialize(request, _jsonOptions), Encoding.UTF8, "application/json");

            var response = await _client.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Error("Failed to create ticket: {Status} - {Error}", response.StatusCode, error);
                return null;
            }

            var ticket = await response.Content.ReadFromJsonAsync<TdxTicket>(_jsonOptions);
            Log.Information("Created ticket {Id}: {Title}", ticket?.Id, ticket?.Title);
            return ticket;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create ticket");
            return null;
        }
    }

    /// <summary>
    /// Convert a sparse set of field updates into an RFC 6902 JSON Patch document.
    ///
    /// TDX's PATCH takes a JsonPatchDocument — an <em>array</em> of operations —
    /// not an object of field/value pairs. Posting the bare object is what
    /// produced:
    ///   "patch must not be null. Errors: The JsonPatchDocument was malformed
    ///    and could not be parsed."
    ///
    /// A null value is preserved as an explicit JSON null rather than dropped:
    /// clearing a field is a legitimate edit, and silently omitting it would
    /// turn "unset the assignee" into a no-op that reports success.
    /// </summary>
    internal static List<Dictionary<string, object?>> ToJsonPatch(IDictionary<string, object?> updates)
    {
        return updates
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new Dictionary<string, object?>
            {
                ["op"] = "replace",
                // RFC 6902 pointers are /-prefixed, and ~ and / inside a field
                // name have to be escaped or the pointer silently addresses
                // something else.
                ["path"] = "/" + kv.Key.Replace("~", "~0").Replace("/", "~1"),
                ["value"] = kv.Value,
            })
            .ToList();
    }

    /// <summary>
    /// Update a ticket. <paramref name="updates"/> is a sparse map of field name
    /// to new value; it is sent as a JSON Patch document.
    /// </summary>
    public async Task<TdxTicket?> UpdateTicketAsync(int ticketId, IDictionary<string, object?> updates)
    {
        if (!await SetAuthorizationAsync())
        {
            return null;
        }

        if (updates.Count == 0)
        {
            Log.Debug("No changes for ticket {Id}; skipping the PATCH", ticketId);
            return await GetTicketAsync(ticketId);
        }

        try
        {
            var url = _config.GetTicketsUrl(ticketId.ToString());
            var patch = ToJsonPatch(updates);
            Log.Information("TDX PATCH ticket {Id} fields: {Fields}",
                ticketId, string.Join(", ", updates.Keys.OrderBy(k => k, StringComparer.Ordinal)));

            var content = new StringContent(
                JsonSerializer.Serialize(patch, _jsonOptions), Encoding.UTF8, "application/json-patch+json");

            var request = new HttpRequestMessage(HttpMethod.Patch, url) { Content = content };
            var response = await _client.SendAsync(request);

            // If PATCH fails, try POST
            if (!response.IsSuccessStatusCode && response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
            {
                response = await _client.PostAsync(url, content);
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Warning("Failed to update ticket {Id}: {Status} - {Error}", ticketId, response.StatusCode, error);
                return null;
            }

            var ticket = await response.Content.ReadFromJsonAsync<TdxTicket>(_jsonOptions);
            Log.Debug("Updated ticket {Id}", ticketId);
            return ticket;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to update ticket {Id}", ticketId);
            return null;
        }
    }

    /// <summary>
    /// Get feed entries (comments) for a ticket.
    /// </summary>
    /// <param name="includeReplies">
    /// Hydrate threaded replies. One extra request per threaded entry and none
    /// for the rest; pass false for a faster first paint.
    /// </param>
    public async Task<List<TdxFeedEntry>> GetTicketFeedAsync(int ticketId, bool includeReplies = true)
    {
        if (!await SetAuthorizationAsync())
        {
            return new List<TdxFeedEntry>();
        }

        try
        {
            var url = _config.GetTicketsUrl($"{ticketId}/feed");
            var response = await _client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Warning("Failed to get feed for ticket {Id}: {Status} - {Error}", ticketId, response.StatusCode, error);
                return new List<TdxFeedEntry>();
            }

            var feed = await response.Content.ReadFromJsonAsync<List<TdxFeedEntry>>(_jsonOptions)
                       ?? new List<TdxFeedEntry>();

            return includeReplies ? await HydrateRepliesAsync(feed) : feed;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get feed for ticket {Id}", ticketId);
            return new List<TdxFeedEntry>();
        }
    }

    /// <summary>
    /// Replace entries that have unloaded replies with copies carrying them.
    ///
    /// The ticket feed collection reports RepliesCount but always sends
    /// <c>Replies: []</c>, so without this every thread renders as nothing.
    /// </summary>
    private async Task<List<TdxFeedEntry>> HydrateRepliesAsync(List<TdxFeedEntry> feed)
    {
        var pending = feed.Where(e => e.HasUnloadedReplies).ToList();
        if (pending.Count == 0) return feed;

        Log.Debug("[tdx] Hydrating replies for {Count} feed entries", pending.Count);

        var loaded = await Task.WhenAll(pending.Select(async entry =>
        {
            var full = await GetFeedEntryAsync(entry.Id);
            return (entry.Id, Replies: full?.ReplyList.ToList() ?? new List<TdxFeedEntry>());
        }));

        var byId = loaded
            .Where(x => x.Replies.Count > 0)
            .ToDictionary(x => x.Id, x => x.Replies);

        return feed
            .Select(entry => byId.TryGetValue(entry.Id, out var replies) ? entry.WithReplies(replies) : entry)
            .ToList();
    }

    /// <summary>
    /// Fetch one feed entry from the tenant-level Feed API, which is the only
    /// endpoint that carries reply bodies.
    /// </summary>
    public async Task<TdxFeedEntry?> GetFeedEntryAsync(int feedEntryId)
    {
        if (!await SetAuthorizationAsync()) return null;

        try
        {
            var response = await _client.GetAsync(_config.GetApiUrl($"feed/{feedEntryId}"));
            if (!response.IsSuccessStatusCode)
            {
                Log.Debug("[tdx] Feed entry {Id} returned {Status}", feedEntryId, response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TdxFeedEntry>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[tdx] Failed to fetch feed entry {Id}", feedEntryId);
            return null;
        }
    }

    // Posting a threaded reply is not possible through the TDX Web API, and no
    // method for it belongs here. Verified against the live API on 2026-07-28:
    //
    //   • OPTIONS /api/feed/{id} answers `Allow: GET,DELETE` — there is no POST
    //     route under a feed entry at all, under any suffix.
    //   • POST /api/{appId}/tickets/{id}/feed accepts ParentID,
    //     ParentFeedEntryID, ReplyToID and ItemUpdateID without complaint and
    //     ignores every one of them: each returns 201 having created another
    //     top-level entry, with the named parent's RepliesCount still 0.
    //
    // Existing threads *are* readable — see GetTicketFeedAsync(includeReplies).
    // The UI offers quoting into a new comment instead. This note is here so the
    // route does not get re-added a third time.

    /// <summary>
    /// Post a top-level comment on a ticket, returning the created feed entry.
    ///
    /// Throws rather than reporting failure as a <c>false</c> return: a
    /// swallowed failure here looks exactly like a successful post the feed has
    /// not caught up with yet, so the operator retypes a comment that was in
    /// fact rejected.
    /// </summary>
    /// <exception cref="TdxCommentException">The comment was not accepted.</exception>
    public async Task<TdxFeedEntry?> AddCommentAsync(
        int ticketId, string comment, bool isPrivate = false,
        bool isRichHtml = false, List<Guid>? notify = null)
    {
        if (!await SetAuthorizationAsync())
        {
            throw new TdxCommentException(ticketId, "not authenticated to TeamDynamix");
        }

        var request = new CreateFeedEntryRequest
        {
            Comments = comment,
            IsPrivate = isPrivate,
            IsRichHtml = isRichHtml,
            Notify = notify,
        };

        var url = _config.GetTicketsUrl($"{ticketId}/feed");
        var content = new StringContent(
            JsonSerializer.Serialize(request, _jsonOptions), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _client.PostAsync(url, content);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to add comment to ticket {Id}", ticketId);
            throw new TdxCommentException(ticketId, ex.Message, ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            Log.Warning("Failed to add comment to ticket {Id}: {Status} - {Error}",
                ticketId, response.StatusCode, error);
            throw new TdxCommentException(ticketId, $"{(int)response.StatusCode}: {error}");
        }

        Log.Debug("Added comment to ticket {Id}", ticketId);
        return await response.Content.ReadFromJsonAsync<TdxFeedEntry>(_jsonOptions);
    }

    #endregion

    #region Reference Data

    /// <summary>
    /// Get ticket statuses
    /// </summary>
    public async Task<Dictionary<int, string>> GetStatusesAsync()
    {
        if (_statusCache.Count > 0 && DateTime.UtcNow < _refDataExpiry)
        {
            return _statusCache;
        }

        if (!await SetAuthorizationAsync())
        {
            return _statusCache;
        }

        try
        {
            var url = $"api/{_config.AppId}/tickets/statuses";
            var response = await _client.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var statuses = await response.Content.ReadFromJsonAsync<List<TdxStatusItem>>(_jsonOptions);
                _statusCache.Clear();
                foreach (var status in statuses ?? new List<TdxStatusItem>())
                {
                    _statusCache[status.Id] = status.Name ?? $"Status {status.Id}";
                }
                _refDataExpiry = DateTime.UtcNow.Add(_cacheDuration);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load ticket statuses");
        }

        return _statusCache;
    }

    /// <summary>
    /// Get ticket types
    /// </summary>
    public async Task<Dictionary<int, string>> GetTypesAsync()
    {
        if (_typeCache.Count > 0 && DateTime.UtcNow < _refDataExpiry)
        {
            return _typeCache;
        }

        if (!await SetAuthorizationAsync())
        {
            return _typeCache;
        }

        try
        {
            var url = $"api/{_config.AppId}/tickets/types";
            var response = await _client.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var types = await response.Content.ReadFromJsonAsync<List<TdxTypeItem>>(_jsonOptions);
                _typeCache.Clear();
                foreach (var type in types ?? new List<TdxTypeItem>())
                {
                    _typeCache[type.Id] = type.Name ?? $"Type {type.Id}";
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load ticket types");
        }

        return _typeCache;
    }

    /// <summary>
    /// Get ticket priorities
    /// </summary>
    public async Task<Dictionary<int, string>> GetPrioritiesAsync()
    {
        if (_priorityCache.Count > 0 && DateTime.UtcNow < _refDataExpiry)
        {
            return _priorityCache;
        }

        if (!await SetAuthorizationAsync())
        {
            return _priorityCache;
        }

        try
        {
            var url = $"api/{_config.AppId}/tickets/priorities";
            var response = await _client.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var priorities = await response.Content.ReadFromJsonAsync<List<TdxPriorityItem>>(_jsonOptions);
                _priorityCache.Clear();
                foreach (var priority in priorities ?? new List<TdxPriorityItem>())
                {
                    _priorityCache[priority.Id] = priority.Name ?? $"Priority {priority.Id}";
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load ticket priorities");
        }

        return _priorityCache;
    }

    #endregion

    public void Dispose()
    {
        _client.Dispose();
    }
}

// Helper classes for reference data
internal class TdxStatusItem
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? StatusClass { get; set; }
}

internal class TdxTypeItem
{
    public int Id { get; set; }
    public string? Name { get; set; }
}

internal class TdxPriorityItem
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public double Order { get; set; }
}
