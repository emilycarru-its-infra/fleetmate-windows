using System.Text.Json;
using System.Text.Json.Serialization;

namespace FleetMate.Converters;

/// <summary>
/// JSON converter that handles empty strings and various date formats for nullable DateTime
/// </summary>
public class NullableDateTimeConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        
        if (reader.TokenType == JsonTokenType.String)
        {
            var stringValue = reader.GetString();
            
            // Handle empty or whitespace strings
            if (string.IsNullOrWhiteSpace(stringValue))
            {
                return null;
            }
            
            // Try to parse as DateTime
            if (DateTime.TryParse(stringValue, out var dateTime))
            {
                return dateTime;
            }
            
            // If parse fails, return null rather than throwing
            return null;
        }
        
        // Try standard DateTime read
        try
        {
            return reader.GetDateTime();
        }
        catch
        {
            return null;
        }
    }
    
    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteStringValue(value.Value.ToString("O"));
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
