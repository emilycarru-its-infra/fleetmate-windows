using System.Text.Json.Serialization;
using FleetMate.Converters;

namespace FleetMate.Models.ReportMate;

/// <summary>
/// Device information from ReportMate API
/// </summary>
public class Device
{
    public string Id { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string AssetTag { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Catalog { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string OsBuild { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    
    [JsonConverter(typeof(NullableDateTimeConverter))]
    public DateTime? LastSeen { get; set; }
    
    [JsonConverter(typeof(NullableDateTimeConverter))]
    public DateTime? CollectedAt { get; set; }
    
    // Cimian-specific fields
    public string CimianVersion { get; set; } = string.Empty;
    public string ManifestUrl { get; set; } = string.Empty;
    public string CatalogUrl { get; set; } = string.Empty;
    
    /// <summary>
    /// Display name for the device (prefers DeviceName, falls back to Name/Hostname)
    /// </summary>
    [JsonIgnore]
    public string DisplayName => !string.IsNullOrEmpty(DeviceName) ? DeviceName 
        : !string.IsNullOrEmpty(Name) ? Name 
        : !string.IsNullOrEmpty(Hostname) ? Hostname 
        : SerialNumber;
}

/// <summary>
/// Response wrapper from ReportMate /api/devices endpoint
/// </summary>
public class DevicesResponse
{
    public List<Device> Devices { get; set; } = new();
    public int Total { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; }
}
