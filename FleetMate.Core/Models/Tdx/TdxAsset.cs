using System.Text.Json;
using System.Text.Json.Serialization;

namespace FleetMate.Models.Tdx;

/// <summary>
/// TeamDynamix asset (partial model for display)
/// </summary>
public class TdxAsset
{
    [JsonPropertyName("ID")]
    public int Id { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Tag")]
    public string? Tag { get; set; }

    [JsonPropertyName("SerialNumber")]
    public string? SerialNumber { get; set; }

    [JsonPropertyName("ExternalID")]
    public string? ExternalId { get; set; }

    [JsonPropertyName("Model")]
    public string? Model { get; set; }

    [JsonPropertyName("Manufacturer")]
    public string? Manufacturer { get; set; }

    [JsonPropertyName("ProductType")]
    public string? ProductType { get; set; }

    [JsonPropertyName("Status")]
    public string? Status { get; set; }

    [JsonPropertyName("Location")]
    public string? Location { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalFields { get; set; }
}

/// <summary>
/// TeamDynamix asset search request
/// </summary>
public class TdxAssetSearchRequest
{
    [JsonPropertyName("ExternalIDs")]
    public List<string>? ExternalIds { get; set; }

    [JsonPropertyName("SearchText")]
    public string? SearchText { get; set; }

    [JsonPropertyName("MaxResults")]
    public int MaxResults { get; set; } = 50;
}
