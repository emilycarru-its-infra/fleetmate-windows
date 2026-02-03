#nullable disable warnings
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FleetMate.Models.Tdx;
using Serilog;

namespace FleetMate.Services;

/// <summary>
/// TeamDynamix (TDX) service for ticket management
/// Uses JWT authentication via SSO, username/password, or BEID
/// </summary>
public class TdxService : IDisposable
{
    private readonly HttpClient _client;
    private readonly TdxConfig _config;
    private readonly JsonSerializerOptions _jsonOptions;
    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    
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
    /// Returns true if SSO login is required based on config
    /// </summary>
    public bool RequiresSsoLogin => _config.AuthMethod == TdxAuthMethod.BrowserSSO && !IsSsoAuthenticated;
    
    /// <summary>
    /// Returns true if SSO should be attempted (based on config)
    /// </summary>
    public bool ShouldAttemptSso => _config.AuthMethod == TdxAuthMethod.BrowserSSO || _config.AuthMethod == TdxAuthMethod.Auto;

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
        var authMethod = _config.AuthMethod;
        
        // Check for valid SSO token first (if SSO is configured)
        if (authMethod == TdxAuthMethod.BrowserSSO || authMethod == TdxAuthMethod.Auto)
        {
            if (!string.IsNullOrEmpty(_ssoToken) && DateTime.UtcNow < _ssoTokenExpiry)
            {
                return _ssoToken;
            }
            
            // If browserSSO is required and no valid token, return null
            if (authMethod == TdxAuthMethod.BrowserSSO)
            {
                Log.Warning("TDX SSO authentication required but no valid token available");
                return null;
            }
        }
        
        // Check cached service account / password token
        if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry)
        {
            return _cachedToken;
        }
        
        // For SSO-only mode, don't fall back to service account
        if (authMethod == TdxAuthMethod.BrowserSSO)
        {
            return null;
        }

        try
        {
            // Try admin login first (BEID + WebServicesKey)
            if (authMethod == TdxAuthMethod.ServiceAccount || authMethod == TdxAuthMethod.Auto)
            {
                var (beid, webServicesKey) = _config.GetAdminCredentials();
                if (!string.IsNullOrEmpty(beid) && !string.IsNullOrEmpty(webServicesKey))
                {
                    var loginUrl = "api/auth/loginadmin";
                    var loginBody = new
                    {
                        BEID = beid,
                        WebServicesKey = webServicesKey
                    };

                    var content = new StringContent(JsonSerializer.Serialize(loginBody), Encoding.UTF8, "application/json");
                    var response = await _client.PostAsync(loginUrl, content);

                    if (response.IsSuccessStatusCode)
                    {
                        _cachedToken = await response.Content.ReadAsStringAsync();
                        _cachedToken = _cachedToken.Trim('"');
                        _tokenExpiry = DateTime.UtcNow.AddHours(23);
                        Log.Debug("Acquired TDX JWT token via admin login (BEID)");
                        return _cachedToken;
                    }

                    var error = await response.Content.ReadAsStringAsync();
                    Log.Warning("TDX admin login failed: {Status} - {Error}, trying regular login", response.StatusCode, error);
                }
            }

            // Fallback to regular login (Username + Password)
            if (authMethod == TdxAuthMethod.UserPassword || authMethod == TdxAuthMethod.Auto)
            {
                var (username, password) = _config.GetRegularCredentials();
                if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                {
                    Log.Error("TDX credentials not configured. Set BEID/WebServicesKey or Username/Password in config, environment variables, or Key Vault.");
                    return null;
                }

                var regularLoginUrl = "api/auth/login";
                var regularLoginBody = new
                {
                    UserName = username,
                    Password = password
                };
                var regularContent = new StringContent(JsonSerializer.Serialize(regularLoginBody), Encoding.UTF8, "application/json");
                var regularResponse = await _client.PostAsync(regularLoginUrl, regularContent);

                if (!regularResponse.IsSuccessStatusCode)
                {
                    var error = await regularResponse.Content.ReadAsStringAsync();
                    Log.Error("TDX authentication failed: {Status} - {Error}", regularResponse.StatusCode, error);
                    return null;
                }

                // Response body is the JWT token as a string
                _cachedToken = await regularResponse.Content.ReadAsStringAsync();
                _cachedToken = _cachedToken.Trim('"');
                _tokenExpiry = DateTime.UtcNow.AddHours(23);

                Log.Debug("Acquired TDX JWT token via regular login");
                return _cachedToken;
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to authenticate with TDX");
            return null;
        }
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
    /// Update a ticket
    /// </summary>
    public async Task<TdxTicket?> UpdateTicketAsync(int ticketId, object updates)
    {
        if (!await SetAuthorizationAsync())
        {
            return null;
        }

        try
        {
            var url = _config.GetTicketsUrl(ticketId.ToString());
            var content = new StringContent(JsonSerializer.Serialize(updates, _jsonOptions), Encoding.UTF8, "application/json");

            // Use PATCH for partial updates
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
    /// Get feed entries (comments) for a ticket
    /// </summary>
    public async Task<List<TdxFeedEntry>> GetTicketFeedAsync(int ticketId)
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

            var feed = await response.Content.ReadFromJsonAsync<List<TdxFeedEntry>>(_jsonOptions);
            return feed ?? new List<TdxFeedEntry>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get feed for ticket {Id}", ticketId);
            return new List<TdxFeedEntry>();
        }
    }

    /// <summary>
    /// Add a comment to a ticket
    /// </summary>
    public async Task<bool> AddCommentAsync(int ticketId, string comment, bool isPrivate = false, List<Guid>? notify = null)
    {
        if (!await SetAuthorizationAsync())
        {
            return false;
        }

        try
        {
            var request = new CreateFeedEntryRequest
            {
                Comments = comment,
                IsPrivate = isPrivate,
                IsRichHtml = false,
                Notify = notify
            };

            var url = _config.GetTicketsUrl($"{ticketId}/feed");
            var content = new StringContent(JsonSerializer.Serialize(request, _jsonOptions), Encoding.UTF8, "application/json");

            var response = await _client.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Warning("Failed to add comment to ticket {Id}: {Status} - {Error}", ticketId, response.StatusCode, error);
                return false;
            }

            Log.Debug("Added comment to ticket {Id}", ticketId);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to add comment to ticket {Id}", ticketId);
            return false;
        }
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
