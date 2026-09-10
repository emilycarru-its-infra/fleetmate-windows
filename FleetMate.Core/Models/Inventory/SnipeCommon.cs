using System.Text.Json;
using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Inventory;

/// <summary>
/// Common types shared across Snipe-IT API responses
/// </summary>
/// 
/// <summary>
/// Generic reference to another entity (id + name)
/// </summary>
public class SnipeRef
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Status label with type information
/// </summary>
public class SnipeStatusLabel
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("status_type")]
    public string? StatusType { get; set; } // deployable, pending, undeployable, archived
    
    [JsonPropertyName("status_meta")]
    public string? StatusMeta { get; set; }
}

/// <summary>
/// Date-only representation
/// </summary>
public class SnipeDate
{
    [JsonPropertyName("date")]
    public string? Date { get; set; }
    
    [JsonPropertyName("formatted")]
    public string? Formatted { get; set; }
}

/// <summary>
/// Handles Snipe date fields that can be either an object or a string.
/// </summary>
public class SnipeDateConverter : JsonConverter<SnipeDate?>
{
    public override SnipeDate? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return new SnipeDate
            {
                Date = value,
                Formatted = value
            };
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            return new SnipeDate
            {
                Date = root.TryGetProperty("date", out var dateProp) ? dateProp.GetString() : null,
                Formatted = root.TryGetProperty("formatted", out var formattedProp) ? formattedProp.GetString() : null
            };
        }

        throw new JsonException("Unsupported SnipeDate JSON token.");
    }

    public override void Write(Utf8JsonWriter writer, SnipeDate? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("date", value.Date);
        writer.WriteString("formatted", value.Formatted ?? value.Date);
        writer.WriteEndObject();
    }
}

/// <summary>
/// Full datetime representation
/// </summary>
public class SnipeDateTime
{
    [JsonPropertyName("datetime")]
    public string? DateTime { get; set; }
    
    [JsonPropertyName("formatted")]
    public string? Formatted { get; set; }
}

/// <summary>
/// Handles Snipe datetime fields that can be either an object or a string.
/// </summary>
public class SnipeDateTimeConverter : JsonConverter<SnipeDateTime?>
{
    public override SnipeDateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return new SnipeDateTime
            {
                DateTime = value,
                Formatted = value
            };
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            return new SnipeDateTime
            {
                DateTime = root.TryGetProperty("datetime", out var dateTimeProp) ? dateTimeProp.GetString() : null,
                Formatted = root.TryGetProperty("formatted", out var formattedProp) ? formattedProp.GetString() : null
            };
        }

        throw new JsonException("Unsupported SnipeDateTime JSON token.");
    }

    public override void Write(Utf8JsonWriter writer, SnipeDateTime? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("datetime", value.DateTime);
        writer.WriteString("formatted", value.Formatted ?? value.DateTime);
        writer.WriteEndObject();
    }
}

/// <summary>
/// Entity an asset is assigned to
/// </summary>
public class SnipeAssignee
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty; // user, asset, location
    
    [JsonPropertyName("username")]
    public string? Username { get; set; }
    
    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }
    
    [JsonPropertyName("last_name")]
    public string? LastName { get; set; }
    
    [JsonPropertyName("employee_number")]
    public string? EmployeeNumber { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }
}

/// <summary>
/// Custom field value
/// </summary>
public class SnipeCustomField
{
    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("field_format")]
    public string? FieldFormat { get; set; }

    [JsonPropertyName("element")]
    public string? Element { get; set; }

    /// <summary>
    /// Slug of the fork's field group ("inventory", "specs", "networking",
    /// "procurement", "identity", "management"). Sent by the ECU Snipe-IT fork;
    /// null from stock Snipe-IT, in which case the client falls back to its
    /// mirrored copy of the seeded taxonomy.
    /// </summary>
    [JsonPropertyName("field_group")]
    public string? FieldGroup { get; set; }
}

/// <summary>
/// A Snipe-IT decimal column. Laravel's decimal cast serialises as a JSON
/// string ("2711.00"), but an uncast column comes back as a number — accept
/// either, and render as currency.
/// </summary>
[JsonConverter(typeof(SnipeMoneyConverter))]
public class SnipeMoney
{
    public string? Raw { get; set; }

    /// <summary>"$2,711.00" when the value parses, the raw string otherwise.</summary>
    [JsonIgnore]
    public string? Formatted
    {
        get
        {
            if (string.IsNullOrEmpty(Raw)) return null;
            return double.TryParse(Raw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value.ToString("C2", System.Globalization.CultureInfo.CurrentCulture)
                : Raw;
        }
    }
}

public class SnipeMoneyConverter : JsonConverter<SnipeMoney?>
{
    public override SnipeMoney? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => new SnipeMoney { Raw = reader.GetString() },
            JsonTokenType.Number => new SnipeMoney { Raw = reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture) },
            _ => throw new JsonException("Unsupported SnipeMoney JSON token.")
        };

    public override void Write(Utf8JsonWriter writer, SnipeMoney? value, JsonSerializerOptions options)
    {
        if (value?.Raw == null) writer.WriteNullValue();
        else writer.WriteStringValue(value.Raw);
    }
}

/// <summary>
/// A field that Snipe-IT sends as either a number or a string (e.g.
/// warranty_months arrives as "36 months" once the transformer bakes in the
/// unit). Decodes both into the string form.
/// </summary>
public class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new JsonException("Unsupported token for flexible string.")
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value == null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}

/// <summary>
/// A custom field definition from GET /api/v1/fields — carries what the asset
/// payload doesn't: the field's element type and, for listboxes, its options.
/// </summary>
public class SnipeFieldDef
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("db_column_name")]
    public string? DbColumnName { get; set; }

    /// <summary>Element type — "listbox" is the one that changes the editor.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("field_values_array")]
    public List<string>? FieldValuesArray { get; set; }
}

/// <summary>
/// Available actions for an entity
/// </summary>
public class SnipeActions
{
    [JsonPropertyName("checkout")]
    public bool Checkout { get; set; }
    
    [JsonPropertyName("checkin")]
    public bool Checkin { get; set; }
    
    [JsonPropertyName("clone")]
    public bool Clone { get; set; }
    
    [JsonPropertyName("restore")]
    public bool Restore { get; set; }
    
    [JsonPropertyName("update")]
    public bool Update { get; set; }
    
    [JsonPropertyName("delete")]
    public bool Delete { get; set; }
}

/// <summary>
/// Generic paginated response wrapper
/// </summary>
public class SnipeListResponse<T>
{
    [JsonPropertyName("total")]
    public int Total { get; set; }
    
    [JsonPropertyName("rows")]
    public List<T> Rows { get; set; } = new();
}

/// <summary>
/// API response for mutations
/// </summary>
public class SnipeResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
    
    [JsonPropertyName("messages")]
    public string? Messages { get; set; }
    
    [JsonPropertyName("payload")]
    public object? Payload { get; set; }
    
    public bool IsSuccess => Status.Equals("success", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// API response with typed payload
/// </summary>
public class SnipeResponse<T>
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
    
    [JsonPropertyName("messages")]
    public string? Messages { get; set; }
    
    [JsonPropertyName("payload")]
    public T? Payload { get; set; }
    
    public bool IsSuccess => Status.Equals("success", StringComparison.OrdinalIgnoreCase);
}
