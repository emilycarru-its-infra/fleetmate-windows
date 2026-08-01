using System.Text.Json;
using System.Text.Json.Serialization;

namespace FleetMate.Core.Converters;

/// <summary>
/// Handles TeamDynamix boolean fields that may be null or encoded as 0/1 or text.
/// </summary>
public sealed class FlexibleBooleanConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False or JsonTokenType.Null => false,
            JsonTokenType.Number => reader.TryGetInt64(out var number) && number != 0,
            JsonTokenType.String => Parse(reader.GetString()),
            _ => throw new JsonException($"Expected a boolean-compatible value, got {reader.TokenType}.")
        };

    private static bool Parse(string? value) =>
        bool.TryParse(value, out var boolean)
            ? boolean
            : long.TryParse(value, out var number) && number != 0;

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
        writer.WriteBooleanValue(value);
}
