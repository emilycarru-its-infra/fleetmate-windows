using System.Text.Json;
using FleetMate.Core.Models.Inventory;
using Xunit;

namespace FleetMate.Tests;

public class SnipeHistoryTests
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new SnipeDateTimeConverter());
        return options;
    }

    private static SnipeActivity Row(string json) => JsonSerializer.Deserialize<SnipeActivity>(json, Options)!;

    [Theory]
    [InlineData("_snipeit_chip_7", "Chip")]
    [InlineData("_snipeit_display_resolution_12", "Display Resolution")]
    [InlineData("status_id", "Status ID")]
    [InlineData("_snipeit_gpu_14", "GPU")]
    [InlineData("_snipeit_intune_id_3", "Intune ID")]
    [InlineData("name", "Name")]
    public void FieldLabel_StripsCustomFieldPrefixAndId(string key, string expected) =>
        Assert.Equal(expected, SnipeHistory.FieldLabel(key));

    [Fact]
    public void ToEntry_ReadsChangesAndPrefersActionDate()
    {
        var entry = SnipeHistory.ToEntry(Row("""
            {
              "id": 5,
              "action_type": "update",
              "created_by": { "id": 1, "name": "Admin One" },
              "target": { "id": 9, "name": "Room 101", "type": "location" },
              "created_at": { "datetime": "2026-09-01 10:00:00", "formatted": "x" },
              "action_date": { "datetime": "2026-09-02 11:30:00", "formatted": "y" },
              "log_meta": { "_snipeit_chip_7": { "old": "M1", "new": "M3 &amp; more" } }
            }
            """));

        Assert.Equal("update", entry.Action);
        Assert.Equal("Admin One", entry.By);
        Assert.Equal("Room 101", entry.Target);
        Assert.Equal("2026-09-02 11:30:00", entry.Date);
        var change = Assert.Single(entry.Changes);
        Assert.Equal(new SnipeFieldChange("Chip", "M1", "M3 & more"), change);
    }

    [Fact]
    public void ToEntry_ToleratesEmptyArrayLogMetaAndFallsBackToAdminAndCreatedAt()
    {
        var entry = SnipeHistory.ToEntry(Row("""
            {
              "id": 6,
              "action_type": "checkout",
              "admin": { "id": 2, "name": "Admin Two" },
              "created_at": { "datetime": "2026-09-03 09:00:00", "formatted": "z" },
              "log_meta": []
            }
            """));

        Assert.Empty(entry.Changes);
        Assert.Equal("Admin Two", entry.By);
        Assert.Equal("2026-09-03 09:00:00", entry.Date);
    }

    [Fact]
    public void ToEntry_ToleratesScalarLogMetaValues()
    {
        var entry = SnipeHistory.ToEntry(Row("""{ "id": 7, "log_meta": { "expected_checkin": "2026-10-01", "qty": 3 } }"""));

        Assert.Equal(new[]
        {
            new SnipeFieldChange("Expected Checkin", null, "2026-10-01"),
            new SnipeFieldChange("Qty", null, "3"),
        }, entry.Changes);
        Assert.Equal("activity", entry.Action);
    }

    [Fact]
    public void ToEntry_StripsTagsFromNotesAndReadsFile()
    {
        var entry = SnipeHistory.ToEntry(Row("""
            {
              "id": 8,
              "action_type": "uploaded",
              "note": "<p>Receipt &quot;A&quot;</p>",
              "file": { "url": "https://snipe.example/hardware/1/showfile/2", "filename": "receipt.pdf" }
            }
            """));

        Assert.Equal("Receipt \"A\"", entry.Note);
        Assert.Equal(new SnipeHistoryFile("receipt.pdf", "https://snipe.example/hardware/1/showfile/2"), entry.File);
        Assert.Contains("receipt.pdf", entry.SearchText);
    }

    [Fact]
    public void ParseChanges_ReadsLogMetaStoredAsJsonString()
    {
        var meta = JsonDocument.Parse("\"{\\\"name\\\":{\\\"old\\\":\\\"a\\\",\\\"new\\\":\\\"b\\\"}}\"").RootElement.Clone();
        Assert.Equal(new SnipeFieldChange("Name", "a", "b"), Assert.Single(SnipeHistory.ParseChanges(meta)));
    }
}
