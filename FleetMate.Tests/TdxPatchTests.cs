using System.Text.Json;
using FleetMate.Core.Services.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// TDX's PATCH takes an RFC 6902 JsonPatchDocument — an array of operations, not
/// an object of field/value pairs. Sending the bare object produced "The
/// JsonPatchDocument was malformed and could not be parsed" and every ticket
/// save failed with a 400.
/// </summary>
public class TdxPatchTests
{
    private static List<Dictionary<string, object?>> Patch(IDictionary<string, object?> updates) =>
        TdxService.ToJsonPatch(updates);

    [Fact]
    public void ProducesAnArrayOfReplaceOperations()
    {
        var patch = Patch(new Dictionary<string, object?>
        {
            ["Title"] = "Printer jam",
            ["StatusID"] = 42,
        });

        Assert.Equal(2, patch.Count);
        Assert.All(patch, op => Assert.Equal("replace", op["op"]));
    }

    [Fact]
    public void PathsArePointerPrefixed()
    {
        var patch = Patch(new Dictionary<string, object?> { ["Title"] = "x" });

        Assert.Equal("/Title", patch[0]["path"]);
        Assert.Equal("x", patch[0]["value"]);
    }

    [Fact]
    public void PreservesNullAsAnExplicitJsonNull()
    {
        // Clearing a field is a legitimate edit. Dropping the key would turn
        // "unset the assignee" into a no-op that still reports success.
        var patch = Patch(new Dictionary<string, object?> { ["ResponsibleUid"] = null });

        Assert.Single(patch);
        Assert.Null(patch[0]["value"]);
        Assert.True(patch[0].ContainsKey("value"));

        var json = JsonSerializer.Serialize(patch);
        Assert.Contains("\"value\":null", json);
    }

    [Theory]
    // RFC 6902 pointer escaping: ~ is ~0 and / is ~1. Without this a field name
    // containing either silently addresses a different location.
    [InlineData("a/b", "/a~1b")]
    [InlineData("a~b", "/a~0b")]
    [InlineData("a~/b", "/a~0~1b")]
    [InlineData("plain", "/plain")]
    public void EscapesPointerMetacharacters(string field, string expectedPath)
    {
        var patch = Patch(new Dictionary<string, object?> { [field] = 1 });
        Assert.Equal(expectedPath, patch[0]["path"]);
    }

    [Fact]
    public void OrdersOperationsDeterministically()
    {
        // Stable ordering keeps request bodies diffable and log lines comparable
        // between runs.
        var patch = Patch(new Dictionary<string, object?>
        {
            ["Zulu"] = 1,
            ["Alpha"] = 2,
            ["Mike"] = 3,
        });

        Assert.Equal(new[] { "/Alpha", "/Mike", "/Zulu" }, patch.Select(op => op["path"]));
    }

    [Fact]
    public void EmptyUpdatesProduceAnEmptyPatch()
    {
        Assert.Empty(Patch(new Dictionary<string, object?>()));
    }

    [Fact]
    public void SerializesToAJsonArrayNotAnObject()
    {
        // The shape is the whole point: TDX rejects an object outright.
        var json = JsonSerializer.Serialize(Patch(new Dictionary<string, object?>
        {
            ["Title"] = "Printer jam",
        }));

        Assert.StartsWith("[", json);
        Assert.EndsWith("]", json);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);

        var op = doc.RootElement[0];
        Assert.Equal("replace", op.GetProperty("op").GetString());
        Assert.Equal("/Title", op.GetProperty("path").GetString());
        Assert.Equal("Printer jam", op.GetProperty("value").GetString());
    }

    [Fact]
    public void CarriesMixedValueTypesThrough()
    {
        var patch = Patch(new Dictionary<string, object?>
        {
            ["Title"] = "text",
            ["Count"] = 7,
            ["Flag"] = true,
        });

        var byPath = patch.ToDictionary(op => (string)op["path"]!, op => op["value"]);

        Assert.Equal("text", byPath["/Title"]);
        Assert.Equal(7, byPath["/Count"]);
        Assert.Equal(true, byPath["/Flag"]);
    }
}
