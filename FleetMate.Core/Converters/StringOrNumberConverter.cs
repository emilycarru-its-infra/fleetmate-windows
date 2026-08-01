using System.Text.Json;
using System.Text.Json.Serialization;

namespace FleetMate.Core.Converters;

/// <summary>
/// Accepts APIs that inconsistently encode a scalar as either JSON text or a number.
/// </summary>
public sealed class StringOrNumberConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetRawString(),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Expected a string or number, got {reader.TokenType}.")
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

internal static class Utf8JsonReaderExtensions
{
    public static string GetRawString(this ref Utf8JsonReader reader)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.GetRawText();
    }
}
