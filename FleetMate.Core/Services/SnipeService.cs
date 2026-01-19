using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FleetMate.Models.Snipe;
using Serilog;

namespace FleetMate.Services;

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
    
    public SnipeService(string? baseUrl = null, string? apiKey = null, int cacheMinutes = 5)
    {
        BaseUrl = baseUrl?.TrimEnd('/') ?? string.Empty;
        
        _client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
        
        if (!string.IsNullOrEmpty(BaseUrl))
        {
            _client.BaseAddress = new Uri(BaseUrl);
        }
        
        if (!string.IsNullOrEmpty(apiKey))
        {
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        
        _cacheDuration = TimeSpan.FromMinutes(cacheMinutes);
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
        
        Log.Debug("Fetching assets from Snipe-IT...");
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
                Console.Error.WriteLine($"DEBUG: Fetching from {_client.BaseAddress}{url}");
                var response = await _client.GetAsync(url);
                Console.Error.WriteLine($"DEBUG: Response status: {response.StatusCode}");
                
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Log.Warning("Failed to fetch assets: {Status} - {Error}", response.StatusCode, error);
                    break;
                }
                
                var rawJson = await response.Content.ReadAsStringAsync();
                Console.Error.WriteLine($"DEBUG: Response length: {rawJson.Length}, first 200 chars: {rawJson.Substring(0, Math.Min(200, rawJson.Length))}");
                
                // Re-read the response
                SnipeListResponse<SnipeAsset>? wrapper = null;
                try
                {
                    wrapper = System.Text.Json.JsonSerializer.Deserialize<SnipeListResponse<SnipeAsset>>(rawJson, _jsonOptions);
                    Console.Error.WriteLine($"DEBUG: Wrapper.Total={wrapper?.Total}, Rows.Count={wrapper?.Rows?.Count}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"DEBUG: Deserialization failed: {ex.Message}");
                    break;
                }
                if (wrapper?.Rows == null || wrapper.Rows.Count == 0)
                    break;
                
                allAssets.AddRange(wrapper.Rows);
                
                if (wrapper.Rows.Count < limit || allAssets.Count >= wrapper.Total)
                    break;
                
                offset += limit;
            }
            
            // Cache only unfiltered results
            if (!hasFilters)
            {
                _assetCache = allAssets;
                _assetCacheExpiry = DateTime.UtcNow.Add(_cacheDuration);
            }
            
            Log.Information("Retrieved {Count} assets from Snipe-IT", allAssets.Count);
            return allAssets;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch assets from Snipe-IT");
            return _assetCache ?? new List<SnipeAsset>();
        }
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
                Log.Warning("Failed to get asset {Id}: {Status}", id, response.StatusCode);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<SnipeAsset>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get asset {Id}", id);
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
                Log.Warning("Failed to get asset by tag {Tag}: {Status}", assetTag, response.StatusCode);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<SnipeAsset>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get asset by tag {Tag}", assetTag);
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
                Log.Warning("Failed to get asset by serial {Serial}: {Status}", serial, response.StatusCode);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<SnipeAsset>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get asset by serial {Serial}", serial);
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
            Log.Error(ex, "Failed to create asset");
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
            Log.Error(ex, "Failed to update asset {Id}", id);
            return null;
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
            Log.Error(ex, "Failed to delete asset {Id}", id);
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
            Log.Error(ex, "Failed to checkout asset {Id}", assetId);
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
            Log.Error(ex, "Failed to checkin asset {Id}", assetId);
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
            Log.Error(ex, "Failed to audit asset {Id}", assetId);
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
                Log.Warning("Failed to get audit due assets: {Status}", response.StatusCode);
                return new List<SnipeAsset>();
            }
            var wrapper = await response.Content.ReadFromJsonAsync<SnipeListResponse<SnipeAsset>>(_jsonOptions);
            return wrapper?.Rows ?? new List<SnipeAsset>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get audit due assets");
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
                Log.Warning("Failed to get overdue audit assets: {Status}", response.StatusCode);
                return new List<SnipeAsset>();
            }
            var wrapper = await response.Content.ReadFromJsonAsync<SnipeListResponse<SnipeAsset>>(_jsonOptions);
            return wrapper?.Rows ?? new List<SnipeAsset>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get overdue audit assets");
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
        
        Log.Debug("Fetching users from Snipe-IT...");
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
                    Log.Warning("Failed to fetch users: {Status} - {Error}", response.StatusCode, error);
                    break;
                }
                
                var wrapper = await response.Content.ReadFromJsonAsync<SnipeListResponse<SnipeUser>>(_jsonOptions);
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
            
            Log.Information("Retrieved {Count} users from Snipe-IT", allUsers.Count);
            return allUsers;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch users from Snipe-IT");
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
                Log.Warning("Failed to get user {Id}: {Status}", id, response.StatusCode);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<SnipeUser>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get user {Id}", id);
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
                Log.Warning("Failed to get current user: {Status}", response.StatusCode);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<SnipeUser>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get current user");
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
                Log.Warning("Failed to get user assets: {Status}", response.StatusCode);
                return new List<SnipeAsset>();
            }
            var wrapper = await response.Content.ReadFromJsonAsync<SnipeListResponse<SnipeAsset>>(_jsonOptions);
            return wrapper?.Rows ?? new List<SnipeAsset>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get user {UserId} assets", userId);
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
            Log.Error(ex, "Failed to create user");
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
        
        Log.Debug("Fetching locations from Snipe-IT...");
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
                    Log.Warning("Failed to fetch locations: {Status} - {Error}", response.StatusCode, error);
                    break;
                }
                
                var wrapper = await response.Content.ReadFromJsonAsync<SnipeListResponse<SnipeLocation>>(_jsonOptions);
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
            
            Log.Information("Retrieved {Count} locations from Snipe-IT", allLocations.Count);
            return allLocations;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch locations from Snipe-IT");
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
                Log.Warning("Failed to get location {Id}: {Status}", id, response.StatusCode);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<SnipeLocation>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get location {Id}", id);
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
        Log.Debug("Fetching models from Snipe-IT...");
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
                    Log.Warning("Failed to fetch models: {Status} - {Error}", response.StatusCode, error);
                    break;
                }
                
                var wrapper = await response.Content.ReadFromJsonAsync<SnipeListResponse<SnipeModel>>(_jsonOptions);
                if (wrapper?.Rows == null || wrapper.Rows.Count == 0)
                    break;
                
                allModels.AddRange(wrapper.Rows);
                
                if (wrapper.Rows.Count < limit || allModels.Count >= wrapper.Total)
                    break;
                
                offset += limit;
            }
            
            Log.Information("Retrieved {Count} models from Snipe-IT", allModels.Count);
            return allModels;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch models from Snipe-IT");
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
        Log.Debug("Fetching licenses from Snipe-IT...");
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
                    Log.Warning("Failed to fetch licenses: {Status} - {Error}", response.StatusCode, error);
                    break;
                }
                
                var wrapper = await response.Content.ReadFromJsonAsync<SnipeListResponse<SnipeLicense>>(_jsonOptions);
                if (wrapper?.Rows == null || wrapper.Rows.Count == 0)
                    break;
                
                allLicenses.AddRange(wrapper.Rows);
                
                if (wrapper.Rows.Count < limit || allLicenses.Count >= wrapper.Total)
                    break;
                
                offset += limit;
            }
            
            Log.Information("Retrieved {Count} licenses from Snipe-IT", allLicenses.Count);
            return allLicenses;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch licenses from Snipe-IT");
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
                Log.Warning("Failed to get license seats: {Status}", response.StatusCode);
                return new List<SnipeLicenseSeat>();
            }
            var wrapper = await response.Content.ReadFromJsonAsync<SnipeListResponse<SnipeLicenseSeat>>(_jsonOptions);
            return wrapper?.Rows ?? new List<SnipeLicenseSeat>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get license {LicenseId} seats", licenseId);
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
        Log.Debug("Fetching categories from Snipe-IT...");
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
                    Log.Warning("Failed to fetch categories: {Status} - {Error}", response.StatusCode, error);
                    break;
                }
                
                var wrapper = await response.Content.ReadFromJsonAsync<SnipeListResponse<SnipeCategory>>(_jsonOptions);
                if (wrapper?.Rows == null || wrapper.Rows.Count == 0)
                    break;
                
                allCategories.AddRange(wrapper.Rows);
                
                if (wrapper.Rows.Count < limit || allCategories.Count >= wrapper.Total)
                    break;
                
                offset += limit;
            }
            
            Log.Information("Retrieved {Count} categories from Snipe-IT", allCategories.Count);
            return allCategories;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch categories from Snipe-IT");
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
        Log.Debug("Fetching manufacturers from Snipe-IT...");
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
                    Log.Warning("Failed to fetch manufacturers: {Status} - {Error}", response.StatusCode, error);
                    break;
                }
                
                var wrapper = await response.Content.ReadFromJsonAsync<SnipeListResponse<SnipeManufacturer>>(_jsonOptions);
                if (wrapper?.Rows == null || wrapper.Rows.Count == 0)
                    break;
                
                allManufacturers.AddRange(wrapper.Rows);
                
                if (wrapper.Rows.Count < limit || allManufacturers.Count >= wrapper.Total)
                    break;
                
                offset += limit;
            }
            
            Log.Information("Retrieved {Count} manufacturers from Snipe-IT", allManufacturers.Count);
            return allManufacturers;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch manufacturers from Snipe-IT");
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
        Log.Debug("Fetching status labels from Snipe-IT...");
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
                var response = await _client.GetAsync(url);
                
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Log.Warning("Failed to fetch status labels: {Status} - {Error}", response.StatusCode, error);
                    break;
                }
                
                var wrapper = await response.Content.ReadFromJsonAsync<SnipeListResponse<SnipeStatusLabelFull>>(_jsonOptions);
                if (wrapper?.Rows == null || wrapper.Rows.Count == 0)
                    break;
                
                allLabels.AddRange(wrapper.Rows);
                
                if (wrapper.Rows.Count < limit || allLabels.Count >= wrapper.Total)
                    break;
                
                offset += limit;
            }
            
            Log.Information("Retrieved {Count} status labels from Snipe-IT", allLabels.Count);
            return allLabels;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch status labels from Snipe-IT");
            return new List<SnipeStatusLabelFull>();
        }
    }
    
    #endregion
    
    #region Accessories, Consumables, Components
    
    /// <summary>
    /// Get all accessories
    /// </summary>
    public async Task<List<SnipeAccessory>> GetAccessoriesAsync(string? search = null)
    {
        Log.Debug("Fetching accessories from Snipe-IT...");
        try
        {
            var queryParams = new List<string> { "limit=500" };
            if (!string.IsNullOrEmpty(search))
                queryParams.Add($"search={Uri.EscapeDataString(search)}");
            
            var response = await _client.GetAsync($"/api/v1/accessories?{string.Join("&", queryParams)}");
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to fetch accessories: {Status}", response.StatusCode);
                return new List<SnipeAccessory>();
            }
            var wrapper = await response.Content.ReadFromJsonAsync<SnipeListResponse<SnipeAccessory>>(_jsonOptions);
            return wrapper?.Rows ?? new List<SnipeAccessory>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch accessories from Snipe-IT");
            return new List<SnipeAccessory>();
        }
    }
    
    /// <summary>
    /// Get all consumables
    /// </summary>
    public async Task<List<SnipeConsumable>> GetConsumablesAsync(string? search = null)
    {
        Log.Debug("Fetching consumables from Snipe-IT...");
        try
        {
            var queryParams = new List<string> { "limit=500" };
            if (!string.IsNullOrEmpty(search))
                queryParams.Add($"search={Uri.EscapeDataString(search)}");
            
            var response = await _client.GetAsync($"/api/v1/consumables?{string.Join("&", queryParams)}");
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to fetch consumables: {Status}", response.StatusCode);
                return new List<SnipeConsumable>();
            }
            var wrapper = await response.Content.ReadFromJsonAsync<SnipeListResponse<SnipeConsumable>>(_jsonOptions);
            return wrapper?.Rows ?? new List<SnipeConsumable>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch consumables from Snipe-IT");
            return new List<SnipeConsumable>();
        }
    }
    
    /// <summary>
    /// Get all components
    /// </summary>
    public async Task<List<SnipeComponent>> GetComponentsAsync(string? search = null)
    {
        Log.Debug("Fetching components from Snipe-IT...");
        try
        {
            var queryParams = new List<string> { "limit=500" };
            if (!string.IsNullOrEmpty(search))
                queryParams.Add($"search={Uri.EscapeDataString(search)}");
            
            var response = await _client.GetAsync($"/api/v1/components?{string.Join("&", queryParams)}");
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to fetch components: {Status}", response.StatusCode);
                return new List<SnipeComponent>();
            }
            var wrapper = await response.Content.ReadFromJsonAsync<SnipeListResponse<SnipeComponent>>(_jsonOptions);
            return wrapper?.Rows ?? new List<SnipeComponent>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch components from Snipe-IT");
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
        Log.Debug("Fetching activity from Snipe-IT...");
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
                Log.Warning("Failed to fetch activity: {Status}", response.StatusCode);
                return new List<SnipeActivity>();
            }
            var wrapper = await response.Content.ReadFromJsonAsync<SnipeListResponse<SnipeActivity>>(_jsonOptions);
            return wrapper?.Rows ?? new List<SnipeActivity>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch activity from Snipe-IT");
            return new List<SnipeActivity>();
        }
    }
    
    /// <summary>
    /// Get maintenance records
    /// </summary>
    public async Task<List<SnipeMaintenance>> GetMaintenancesAsync(int? assetId = null)
    {
        Log.Debug("Fetching maintenances from Snipe-IT...");
        try
        {
            var queryParams = new List<string> { "limit=500" };
            if (assetId.HasValue)
                queryParams.Add($"asset_id={assetId}");
            
            var response = await _client.GetAsync($"/api/v1/maintenances?{string.Join("&", queryParams)}");
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to fetch maintenances: {Status}", response.StatusCode);
                return new List<SnipeMaintenance>();
            }
            var wrapper = await response.Content.ReadFromJsonAsync<SnipeListResponse<SnipeMaintenance>>(_jsonOptions);
            return wrapper?.Rows ?? new List<SnipeMaintenance>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch maintenances from Snipe-IT");
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
        Log.Debug("Fetching companies from Snipe-IT...");
        try
        {
            var queryParams = new List<string> { "limit=500" };
            if (!string.IsNullOrEmpty(search))
                queryParams.Add($"search={Uri.EscapeDataString(search)}");
            
            var response = await _client.GetAsync($"/api/v1/companies?{string.Join("&", queryParams)}");
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to fetch companies: {Status}", response.StatusCode);
                return new List<SnipeCompany>();
            }
            var wrapper = await response.Content.ReadFromJsonAsync<SnipeListResponse<SnipeCompany>>(_jsonOptions);
            return wrapper?.Rows ?? new List<SnipeCompany>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch companies from Snipe-IT");
            return new List<SnipeCompany>();
        }
    }
    
    /// <summary>
    /// Get all departments
    /// </summary>
    public async Task<List<SnipeDepartment>> GetDepartmentsAsync(string? search = null, int? companyId = null)
    {
        Log.Debug("Fetching departments from Snipe-IT...");
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
                Log.Warning("Failed to fetch departments: {Status}", response.StatusCode);
                return new List<SnipeDepartment>();
            }
            var wrapper = await response.Content.ReadFromJsonAsync<SnipeListResponse<SnipeDepartment>>(_jsonOptions);
            return wrapper?.Rows ?? new List<SnipeDepartment>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch departments from Snipe-IT");
            return new List<SnipeDepartment>();
        }
    }
    
    #endregion
    
    public void Dispose()
    {
        _client.Dispose();
    }
}
