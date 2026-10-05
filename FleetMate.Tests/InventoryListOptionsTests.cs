using FleetMate.Core.Models.Inventory;
using Xunit;

namespace FleetMate.Tests;

public class InventoryListOptionsTests
{
    [Fact]
    public void EveryColumnShowsByDefault()
    {
        var columns = new InventoryColumns();
        Assert.Equal(InventoryColumns.All, columns.Visible);
        Assert.Contains("Location", InventoryColumns.All);
    }

    [Fact]
    public void HidingKeepsDisplayOrderAndTheLastColumnStays()
    {
        var columns = new InventoryColumns(new[] { "Serial", "Asset Tag" });
        Assert.Equal(new[] { "Asset Tag", "Serial" }, columns.Visible);

        Assert.True(columns.Set("Serial", false));
        Assert.False(columns.Set("Asset Tag", false));
        Assert.Equal(new[] { "Asset Tag" }, columns.Visible);

        Assert.True(columns.Set("Location", true));
        Assert.Equal(new[] { "Asset Tag", "Location" }, columns.Visible);
        Assert.False(columns.Set("Not A Column", true));
    }

    [Fact]
    public void SavedChoiceRoundTripsAndBadFilesFallBackToEverything()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fm-inv-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "inventory-columns.json");
        try
        {
            var columns = new InventoryColumns();
            columns.Set("Usage", false);
            columns.Set("Area", false);
            columns.Save(path);

            var loaded = InventoryColumns.Load(path);
            Assert.False(loaded.IsVisible("Usage"));
            Assert.False(loaded.IsVisible("Area"));
            Assert.True(loaded.IsVisible("Location"));

            File.WriteAllText(path, "{ not json");
            Assert.Equal(InventoryColumns.All, InventoryColumns.Load(path).Visible);
            Assert.Equal(InventoryColumns.All, InventoryColumns.Load(Path.Combine(dir, "missing.json")).Visible);

            // Unknown names (an older or newer build's columns) are dropped.
            File.WriteAllText(path, "[\"Retired Column\"]");
            Assert.Equal(InventoryColumns.All, InventoryColumns.Load(path).Visible);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static SnipeAsset At(string? location) =>
        new() { AssetTag = "T" + location, Location = location == null ? null : new SnipeRef { Name = location } };

    [Fact]
    public void LocationOptionsAreDistinctSortedAndDecoded()
    {
        var assets = new[] { At("Room B"), At("room a"), At("Room B"), At(null), At(""), At("Studio &amp; Lab") };
        Assert.Equal(new[] { "All", "room a", "Room B", "Studio & Lab" }, AssetLocationFilter.Options(assets));
    }

    [Fact]
    public void LocationFilterMatchesExactlyAndExcludesAssetsWithoutOne()
    {
        Assert.True(AssetLocationFilter.Matches(At("Room B"), "Room B"));
        Assert.False(AssetLocationFilter.Matches(At("Room B"), "Room A"));
        Assert.False(AssetLocationFilter.Matches(At(null), "Room A"));
        Assert.True(AssetLocationFilter.Matches(At(null), AssetLocationFilter.All));
        Assert.True(AssetLocationFilter.Matches(At(null), null));
    }
}
