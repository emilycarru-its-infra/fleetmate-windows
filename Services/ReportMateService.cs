using System.Net.Http.Json;
using System.Text.Json;
using FleetMate.Converters;
using FleetMate.Models;
using Serilog;

namespace FleetMate.Services;

/// <summary>
/// Client for ReportMate API - fleet monitoring and device inventory
/// </summary>
public class ReportMateService : IDisposable
{
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _jsonOptions;
    
    // Caches
    private List<Device>? _deviceCache;
    private DateTime _deviceCacheExpiry = DateTime.MinValue;
    private List<InstallRecord>? _installCache;
    private DateTime _installCacheExpiry = DateTime.MinValue;
    private readonly TimeSpan _cacheDuration;
    
    public ReportMateService(string baseUrl, string? passphrase = null, int cacheMinutes = 5)
    {
        _client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromSeconds(120)
        };
        
        if (!string.IsNullOrEmpty(passphrase))
        {
            _client.DefaultRequestHeaders.Add("X-Client-Passphrase", passphrase);
        }
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new NullableDateTimeConverter() }
        };
        
        _cacheDuration = TimeSpan.FromMinutes(cacheMinutes);
    }
    
    /// <summary>
    /// Get all devices from the fleet
    /// </summary>
    public async Task<List<Device>> GetDevicesAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _deviceCache != null && DateTime.UtcNow < _deviceCacheExpiry)
        {
            return _deviceCache;
        }
        
        Log.Debug("Fetching devices from ReportMate...");
        
        try
        {
            var allDevices = new List<Device>();
            var offset = 0;
            const int limit = 100;
            
            while (true)
            {
                var response = await _client.GetAsync($"/api/devices?offset={offset}&limit={limit}");
                
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Log.Warning("Failed to fetch devices: {Status} - {Error}", response.StatusCode, error);
                    break;
                }
                
                var wrapper = await response.Content.ReadFromJsonAsync<DevicesResponse>(_jsonOptions);
                if (wrapper?.Devices == null || wrapper.Devices.Count == 0)
                    break;
                
                allDevices.AddRange(wrapper.Devices);
                
                if (wrapper.Devices.Count < limit)
                    break;
                
                offset += limit;
            }
            
            _deviceCache = allDevices;
            _deviceCacheExpiry = DateTime.UtcNow.Add(_cacheDuration);
            Log.Information("Cached {Count} devices from ReportMate", _deviceCache.Count);
            
            return _deviceCache;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch devices from ReportMate");
            return _deviceCache ?? new List<Device>();
        }
    }
    
    /// <summary>
    /// Find a device by serial, hostname, asset tag, or other identifier
    /// </summary>
    public async Task<Device?> FindDeviceAsync(string query)
    {
        var devices = await GetDevicesAsync();
        var normalized = query.Trim().ToUpperInvariant();
        
        return devices.FirstOrDefault(d =>
            d.SerialNumber?.ToUpperInvariant() == normalized ||
            d.DeviceName?.ToUpperInvariant() == normalized ||
            d.Name?.ToUpperInvariant() == normalized ||
            d.Hostname?.ToUpperInvariant() == normalized ||
            d.AssetTag?.ToUpperInvariant() == normalized ||
            d.Owner?.ToUpperInvariant() == normalized ||
            d.IpAddress == query ||
            NormalizeMac(d.MacAddress) == NormalizeMac(query));
    }
    
    /// <summary>
    /// Get all install records
    /// </summary>
    public async Task<List<InstallRecord>> GetInstallsAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _installCache != null && DateTime.UtcNow < _installCacheExpiry)
        {
            return _installCache;
        }
        
        Log.Debug("Fetching install records from ReportMate...");
        
        try
        {
            var response = await _client.GetAsync("/api/devices/installs");
            
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Warning("Failed to fetch installs: {Status} - {Error}", response.StatusCode, error);
                return _installCache ?? new List<InstallRecord>();
            }
            
            var installs = await response.Content.ReadFromJsonAsync<List<InstallRecord>>(_jsonOptions);
            _installCache = installs ?? new List<InstallRecord>();
            _installCacheExpiry = DateTime.UtcNow.Add(_cacheDuration);
            
            Log.Information("Cached {Count} install records from ReportMate", _installCache.Count);
            return _installCache;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch install records from ReportMate");
            return _installCache ?? new List<InstallRecord>();
        }
    }
    
    /// <summary>
    /// Get only install records with errors
    /// </summary>
    public async Task<List<InstallRecord>> GetErrorsAsync(bool forceRefresh = false)
    {
        var installs = await GetInstallsAsync(forceRefresh);
        return installs.Where(i => i.IsError).ToList();
    }
    
    /// <summary>
    /// Get errors grouped by item name
    /// </summary>
    public async Task<List<ErrorSummary>> GetErrorsByItemAsync(bool forceRefresh = false)
    {
        var errors = await GetErrorsAsync(forceRefresh);
        
        return errors
            .GroupBy(e => e.ItemName)
            .Select(g => new ErrorSummary
            {
                ItemName = g.Key,
                DeviceCount = g.Count(),
                Category = g.First().Category,
                SampleError = g.First().LastError ?? string.Empty,
                AffectedDevices = g.Select(e => $"{e.DeviceName} ({e.SerialNumber})").ToList()
            })
            .OrderByDescending(s => s.DeviceCount)
            .ToList();
    }
    
    /// <summary>
    /// Get errors grouped by device
    /// </summary>
    public async Task<List<DeviceErrorSummary>> GetErrorsByDeviceAsync(bool forceRefresh = false)
    {
        var errors = await GetErrorsAsync(forceRefresh);
        
        return errors
            .GroupBy(e => e.SerialNumber)
            .Select(g => new DeviceErrorSummary
            {
                DeviceName = g.First().DeviceName,
                SerialNumber = g.Key,
                Location = g.First().Location,
                ErrorCount = g.Count(),
                FailedItems = g.Select(e => e.ItemName).ToList(),
                LastSeen = g.Max(e => e.LastSeen)
            })
            .OrderByDescending(s => s.ErrorCount)
            .ToList();
    }
    
    /// <summary>
    /// Get install records for a specific device
    /// </summary>
    public async Task<List<InstallRecord>> GetDeviceInstallsAsync(string serialOrName)
    {
        var installs = await GetInstallsAsync();
        var normalized = serialOrName.Trim().ToUpperInvariant();
        
        return installs.Where(i =>
            i.SerialNumber?.ToUpperInvariant() == normalized ||
            i.DeviceName?.ToUpperInvariant() == normalized).ToList();
    }
    
    /// <summary>
    /// Get install records for a specific item across all devices
    /// </summary>
    public async Task<List<InstallRecord>> GetItemInstallsAsync(string itemName)
    {
        var installs = await GetInstallsAsync();
        return installs.Where(i => 
            i.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase)).ToList();
    }
    
    private static string NormalizeMac(string? mac)
    {
        if (string.IsNullOrEmpty(mac)) return string.Empty;
        return mac.Replace(":", "").Replace("-", "").Replace(".", "").ToUpperInvariant();
    }
    
    public void Dispose()
    {
        _client.Dispose();
    }
}
