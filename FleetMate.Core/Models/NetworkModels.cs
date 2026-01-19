using System.Text.Json;
using System.Text.Json.Serialization;
using FleetMate.Converters;

namespace FleetMate.Models;

/// <summary>
/// Device installation log from Cimian client
/// </summary>
public class DeviceLog
{
    [JsonPropertyName("serialNumber")]
    public string SerialNumber { get; set; } = string.Empty;
    
    [JsonPropertyName("log")]
    public string Log { get; set; } = string.Empty;
    
    [JsonPropertyName("collectedAt")]
    [JsonConverter(typeof(NullableDateTimeConverter))]
    public DateTime? CollectedAt { get; set; }
}

/// <summary>
/// Network information for a device
/// </summary>
public class NetworkInfo
{
    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }
    
    [JsonPropertyName("domainName")]
    public string? DomainName { get; set; }
    
    [JsonPropertyName("dnsServers")]
    public List<string>? DnsServers { get; set; }
    
    [JsonPropertyName("interfaces")]
    public List<NetworkInterface>? Interfaces { get; set; }
    
    /// <summary>
    /// Get the primary IPv4 address (first non-loopback, non-link-local)
    /// </summary>
    [JsonIgnore]
    public string? PrimaryIpv4 => Interfaces?
        .SelectMany(i => i.Addresses ?? new List<string>())
        .FirstOrDefault(a => IsValidIpv4(a));
    
    /// <summary>
    /// Get all IPv4 addresses
    /// </summary>
    [JsonIgnore]
    public List<string> AllIpv4Addresses => Interfaces?
        .SelectMany(i => i.Addresses ?? new List<string>())
        .Where(a => IsValidIpv4(a))
        .ToList() ?? new List<string>();
    
    private static bool IsValidIpv4(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return false;
        if (ip.Contains(':')) return false; // IPv6
        if (ip.StartsWith("127.")) return false; // Loopback
        if (ip.StartsWith("169.254.")) return false; // Link-local
        return true;
    }
}

/// <summary>
/// Network interface information
/// </summary>
public class NetworkInterface
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    
    [JsonPropertyName("macAddress")]
    public string? MacAddress { get; set; }
    
    [JsonPropertyName("addresses")]
    public List<string>? Addresses { get; set; }
    
    [JsonPropertyName("status")]
    public string? Status { get; set; }
    
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

/// <summary>
/// Device with network info from /api/devices/network endpoint
/// </summary>
public class DeviceNetworkInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
    
    [JsonPropertyName("serialNumber")]
    public string SerialNumber { get; set; } = string.Empty;
    
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;
    
    [JsonPropertyName("assetTag")]
    public string? AssetTag { get; set; }
    
    [JsonPropertyName("location")]
    public string? Location { get; set; }
    
    [JsonPropertyName("catalog")]
    public string? Catalog { get; set; }
    
    [JsonPropertyName("usage")]
    public string? Usage { get; set; }
    
    [JsonPropertyName("lastSeen")]
    [JsonConverter(typeof(NullableDateTimeConverter))]
    public DateTime? LastSeen { get; set; }
    
    [JsonPropertyName("raw")]
    public JsonElement? Raw { get; set; }
    
    /// <summary>
    /// Get the network info from the raw data
    /// </summary>
    [JsonIgnore]
    public NetworkInfo? NetworkInfo
    {
        get
        {
            if (Raw == null) return null;
            try
            {
                return JsonSerializer.Deserialize<NetworkInfo>(Raw.Value.GetRawText());
            }
            catch
            {
                return null;
            }
        }
    }
    
    /// <summary>
    /// Primary IP address
    /// </summary>
    [JsonIgnore]
    public string? PrimaryIp => NetworkInfo?.PrimaryIpv4;
}

/// <summary>
/// Full device data with all modules from /api/device/{serial}
/// </summary>
public class FullDevice
{
    [JsonPropertyName("serialNumber")]
    public string SerialNumber { get; set; } = string.Empty;
    
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }
    
    [JsonPropertyName("deviceName")]
    public string? DeviceName { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("platform")]
    public string? Platform { get; set; }
    
    [JsonPropertyName("osName")]
    public string? OsName { get; set; }
    
    [JsonPropertyName("osVersion")]
    public string? OsVersion { get; set; }
    
    [JsonPropertyName("lastSeen")]
    [JsonConverter(typeof(NullableDateTimeConverter))]
    public DateTime? LastSeen { get; set; }
    
    [JsonPropertyName("archived")]
    public bool Archived { get; set; }
    
    [JsonPropertyName("clientVersion")]
    public string? ClientVersion { get; set; }
    
    [JsonPropertyName("modules")]
    public Dictionary<string, JsonElement>? Modules { get; set; }
    
    /// <summary>
    /// Get network info from modules
    /// </summary>
    [JsonIgnore]
    public NetworkInfo? Network => GetModule<NetworkInfo>("network");
    
    /// <summary>
    /// Get a typed module from the modules dictionary
    /// </summary>
    public T? GetModule<T>(string moduleName) where T : class
    {
        if (Modules == null || !Modules.TryGetValue(moduleName, out var element))
            return null;
        
        try
        {
            return JsonSerializer.Deserialize<T>(element.GetRawText());
        }
        catch
        {
            return null;
        }
    }
    
    /// <summary>
    /// Display name (prefers DeviceName)
    /// </summary>
    [JsonIgnore]
    public string DisplayName => !string.IsNullOrEmpty(DeviceName) ? DeviceName 
        : !string.IsNullOrEmpty(Name) ? Name 
        : SerialNumber;
}

/// <summary>
/// Wrapper for module data responses
/// </summary>
public class ModuleDataWrapper
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    
    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

/// <summary>
/// Extension methods for JsonElement
/// </summary>
public static class JsonElementExtensions
{
    public static T? ToObject<T>(this JsonElement? element, JsonSerializerOptions? options = null) where T : class
    {
        if (element == null) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(element.Value.GetRawText(), options);
        }
        catch
        {
            return null;
        }
    }
    
    public static T? ToObject<T>(this JsonElement element, JsonSerializerOptions? options = null) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(element.GetRawText(), options);
        }
        catch
        {
            return null;
        }
    }
}
