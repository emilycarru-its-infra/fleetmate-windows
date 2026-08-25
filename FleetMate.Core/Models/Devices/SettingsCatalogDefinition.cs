using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Devices;

/// <summary>
/// One setting definition from the Intune Settings Catalog.
///
/// A configuration profile references settings by definition id. Graph does not
/// resolve a wrong id to anything helpful — it rejects the whole profile with
/// "Setting Id is not found in the Settings Catalog" — so the id has to be
/// correct before the profile is written, and the catalog is the only place it
/// can be looked up.
/// </summary>
public class SettingsCatalogDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("categoryId")]
    public string? CategoryId { get; set; }

    [JsonPropertyName("helpText")]
    public string? HelpText { get; set; }

    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; set; } = new();

    [JsonPropertyName("applicability")]
    public SettingApplicability? Applicability { get; set; }

    /// <summary>
    /// The @odata.type, which is what tells a caller the SHAPE of the value the
    /// setting takes — choice, simple, group collection. A profile that supplies
    /// the right id with the wrong value shape is rejected just as firmly as one
    /// with a bad id, so this is part of the answer, not decoration.
    /// </summary>
    [JsonPropertyName("@odata.type")]
    public string? OdataType { get; set; }

    /// <summary>The value shape, with the Graph type prefix stripped.</summary>
    [JsonIgnore]
    public string Kind =>
        string.IsNullOrEmpty(OdataType)
            ? "-"
            : OdataType.Replace("#microsoft.graph.deviceManagementConfiguration", "")
                       .Replace("SettingDefinition", "");
}

public class SettingApplicability
{
    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    [JsonPropertyName("deviceMode")]
    public string? DeviceMode { get; set; }

    [JsonPropertyName("technologies")]
    public string? Technologies { get; set; }
}

public class SettingsCatalogListResponse
{
    [JsonPropertyName("value")]
    public List<SettingsCatalogDefinition> Value { get; set; } = new();

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }
}
