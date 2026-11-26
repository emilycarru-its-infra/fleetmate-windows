using FleetMate.Core.Development;
using FleetMate.Core.Inventory;
using FleetMate.Core.Models.Inventory;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The Inventory logic both Windows apps share: list query, graph cards, detail layout and re-allocation.</summary>
public class InventoryCoreTests
{
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0);

    private static SnipeAsset Asset(int id, string tag, string? status = "Deployed", string? meta = "deployed",
        string? category = "Laptops", string? person = null, string? location = null, string? updated = null) => new()
    {
        Id = id,
        AssetTag = tag,
        Name = $"Asset {tag}",
        Serial = $"SER{id}",
        StatusLabel = status == null ? null : new SnipeStatusLabel { Name = status, StatusMeta = meta },
        Category = category == null ? null : new SnipeRef { Name = category },
        AssignedTo = person == null ? null : new SnipeAssignee { Name = person, Type = "user" },
        Location = location == null ? null : new SnipeRef { Name = location },
        UpdatedAt = updated == null ? null : new SnipeDateTime { DateTime = updated, Formatted = updated },
    };

    // MARK: - List query

    [Fact]
    public void Query_SearchesNameTagSerialAndAssignee_IgnoringCase()
    {
        var assets = new[] { Asset(1, "T-1", person: "Avery Quinn"), Asset(2, "T-2"), Asset(3, "X-3") };
        var query = new AssetListQuery { Search = "avery" };
        Assert.Equal(new[] { 1 }, query.Apply(assets).Select(a => a.Id));
        query.Search = "ser2";
        Assert.Equal(new[] { 2 }, query.Apply(assets).Select(a => a.Id));
        query.Search = "  ";
        Assert.Equal(3, query.Apply(assets).Count);
    }

    [Fact]
    public void Query_StatusTypeSelectsEveryStatusNameOfThatType()
    {
        var assets = new[]
        {
            Asset(1, "A", "Ready", "deployable"), Asset(2, "B", "Spare", "deployable"), Asset(3, "C", "In Use", "deployed"),
        };
        var query = new AssetListQuery { [AssetFilter.StatusType] = "Deployable" };
        Assert.Equal(new[] { 1, 2 }, query.Apply(assets).Select(a => a.Id).OrderBy(i => i));
        Assert.True(query.HasActiveFilters);

        query[AssetFilter.StatusType] = AssetListQuery.All;
        query[AssetFilter.Status] = "Spare";
        Assert.Equal(new[] { 2 }, query.Apply(assets).Select(a => a.Id));

        query.ClearFilters();
        Assert.False(query.HasActiveFilters);
        Assert.Equal(3, query.Apply(assets).Count);
    }

    [Fact]
    public void Query_LocationFilterNeverMatchesAnAssetWithNoLocation()
    {
        var assets = new[] { Asset(1, "A", location: "Studio"), Asset(2, "B") };
        var query = new AssetListQuery { [AssetFilter.Location] = "Studio" };
        Assert.Equal(new[] { 1 }, query.Apply(assets).Select(a => a.Id));
        Assert.Equal(new[] { "All", "Studio" }, AssetListQuery.Options(assets, AssetFilter.Location));
    }

    [Fact]
    public void Query_OptionsStartWithAllAndSkipBlanks()
    {
        var assets = new[] { Asset(1, "A", category: "Tablets"), Asset(2, "B", category: "Laptops"), Asset(3, "C", category: null) };
        Assert.Equal(new[] { "All", "Laptops", "Tablets" }, AssetListQuery.Options(assets, AssetFilter.Category));
        Assert.Equal(new[] { "All", "Deployed" }, AssetListQuery.Options(assets, AssetFilter.StatusType));
    }

    [Fact]
    public void Query_SortsByRecentActivityFirst_ThenByHeaderClicks()
    {
        var assets = new[]
        {
            Asset(1, "B", updated: "2026-01-01 10:00:00"), Asset(2, "C", updated: "2026-03-01 10:00:00"),
            Asset(3, "A", updated: "2026-02-01 10:00:00"),
        };
        var query = new AssetListQuery();
        Assert.Equal(new[] { 2, 3, 1 }, query.Apply(assets).Select(a => a.Id));

        query.SortBy("Asset Tag");
        Assert.False(query.SortDescending);
        Assert.Equal(new[] { 3, 1, 2 }, query.Apply(assets).Select(a => a.Id));
        query.SortBy("Asset Tag");
        Assert.Equal(new[] { 2, 1, 3 }, query.Apply(assets).Select(a => a.Id));

        query.SortBy("Last Activity");
        Assert.True(query.SortDescending);
        Assert.Equal("2026-03-01 10:00:00", AssetListQuery.Cell(query.Apply(assets)[0], "Last Activity"));
    }

    // MARK: - Graph cards

    [Fact]
    public void Cards_CountAssetsDeployedAndUnassigned()
    {
        var assets = new[]
        {
            Asset(1, "A", person: "Avery"), Asset(2, "B", "Ready", "deployable"), Asset(3, "C", category: "Displays"),
        };
        var cards = InventoryGraphCards.Build(assets, loading: false, loadError: null);
        var kpis = cards[0].Kpis;
        Assert.Equal(new[] { "3", "2", "2" }, kpis.Select(k => k.Value));
        Assert.Equal(new[] { "Assets", "Deployed", "Unassigned" }, kpis.Select(k => k.Title));
    }

    [Fact]
    public void Cards_CategoryBarsAndStatusWedgesOpenTheirFilters()
    {
        var assets = new[] { Asset(1, "A"), Asset(2, "B"), Asset(3, "C", "Ready", "deployable", "Displays") };
        var cards = InventoryGraphCards.Build(assets, false, null);

        var categories = cards.Single(c => c.Title == "Assets by Category");
        Assert.Equal(GraphCardKind.Bars, categories.Kind);
        Assert.Equal(new GraphFilter(InventoryGraphCategory.Category, "Laptops"), categories.Bars[0].Open);
        Assert.Equal("Laptops, 2 assets", categories.Bars[0].Description);

        var status = cards.Single(c => c.Title == "Asset Status");
        Assert.Equal(GraphCardKind.Donut, status.Kind);
        Assert.Equal("deployed (2)", status.Bars[0].Label);
        Assert.Equal(new GraphFilter(InventoryGraphCategory.StatusType, "deployed"), status.Bars[0].Open);
    }

    [Fact]
    public void Cards_SayWhyThereIsNoData()
    {
        var empty = Array.Empty<SnipeAsset>();
        Assert.All(InventoryGraphCards.Build(empty, true, null).Skip(1), c => Assert.Equal(GraphCardBody.Loading, c.Body));
        Assert.True(InventoryGraphCards.Build(empty, true, null)[0].Kpis.All(k => k.Loading));
        var failed = InventoryGraphCards.Build(empty, false, "Sign-in failed: no token");
        Assert.All(failed.Skip(1), c => Assert.Equal("Sign-in failed", c.Message));
        Assert.All(InventoryGraphCards.Build(empty, false, null).Skip(1), c => Assert.Equal("No asset data", c.Message));
    }

    // MARK: - Detail layout

    [Fact]
    public void Detail_GroupsCustomFieldsByServerSlugThenSeedTaxonomy()
    {
        var asset = Asset(1, "A");
        asset.CustomFields = new()
        {
            ["Memory"] = new() { Field = "_mem", Value = "16 GB" },
            ["Hostname"] = new() { Field = "_host", Value = "a.example.com", FieldGroup = "identity" },
            ["Colour Name"] = new() { Field = "_col", Value = "Silver" },
            ["Empty One"] = new() { Field = "_e", Value = "" },
            ["Username"] = new() { Field = "_u", Value = "hidden" },
        };
        Assert.Contains(AssetDetailLayout.HardwareRows(asset), r => r.Label == "Memory" && r.EditKey == "_mem");
        var identity = AssetDetailLayout.GroupRows(asset, "identity");
        Assert.Equal("Hostname", Assert.Single(identity).Label);
        Assert.True(identity[0].Mono);
        Assert.Empty(AssetDetailLayout.GroupRows(asset, "networking"));
        Assert.Equal(new[] { "Colour Name" }, AssetDetailLayout.OtherRows(asset).Select(r => r.Label));
    }

    [Fact]
    public void Detail_InventoryRowsPutLocationAfterCatalog()
    {
        var asset = Asset(1, "A");
        asset.RtdLocation = new SnipeRef { Name = "Studio" };
        asset.CustomFields = new()
        {
            ["Catalog"] = new() { Field = "_cat", Value = "Standard" },
            ["Fleet"] = new() { Field = "_fleet", Value = "Studio Fleet" },
        };
        Assert.Equal(new[] { "Fleet", "Catalog", "Location", "Usage", "Area" },
            AssetDetailLayout.InventoryRows(asset).Select(r => r.Label));
    }

    [Fact]
    public void Detail_LifecycleBarsAndEolText()
    {
        var asset = Asset(1, "A");
        asset.PurchaseDate = new SnipeDate { Date = "2024-06-15" };
        asset.AssetEolDate = new SnipeDate { Date = "2028-06-15", Formatted = "2028-06-15" };
        asset.WarrantyExpires = new SnipeDate { Date = "2025-06-15", Formatted = "2025-06-15" };

        var bars = AssetDetailLayout.Lifecycle(asset, Now);
        Assert.Equal("Device EOL", bars[0].Title);
        Assert.Equal("24 months left · 50%", bars[0].Trailing);
        Assert.False(bars[0].Overdue);
        Assert.True(bars[1].Overdue);
        Assert.Equal("2028-06-15 — 2 years from now", AssetDetailLayout.DeviceEolText(asset, Now));
        Assert.Null(AssetDetailLayout.LifecycleFraction(null, Now, Now));
    }

    [Fact]
    public void Detail_AssigneeThatIsALocationShowsOnlyAsThePlace()
    {
        var asset = Asset(1, "A");
        asset.AssignedTo = new SnipeAssignee { Name = "Room 4", Type = "location" };
        Assert.Equal((null, "Room 4"), AssetDetailLayout.WhoWhere(asset));
        Assert.True(AssetDetailLayout.CanReAllocate(asset));
        Assert.False(AssetDetailLayout.CanReAllocate(Asset(2, "B", "Broken", "archived")));
    }

    [Fact]
    public void Detail_ListboxEditKeepsAValueThatIsNotAnOption()
    {
        Assert.Equal(new[] { "", "Odd", "A", "B" }, AssetDetailLayout.EditChoices("Odd", new[] { "A", "B" }));
        Assert.Equal(new[] { "", "A", "B" }, AssetDetailLayout.EditChoices("A", new[] { "A", "B" }));
        var options = AssetDetailLayout.ListboxOptions(new[]
        {
            new SnipeFieldDef { DbColumnName = "_a", Type = "listbox", FieldValuesArray = new() { "x" } },
            new SnipeFieldDef { DbColumnName = "_b", Type = "text", FieldValuesArray = new() { "y" } },
        });
        Assert.Equal(new[] { "_a" }, options.Keys);
    }

    [Fact]
    public void History_FiltersCountsAndResolvesFiles()
    {
        var history = new[]
        {
            new SnipeHistoryEntry { Action = "checkout", Target = "Avery" },
            new SnipeHistoryEntry { Action = "audit" },
        };
        Assert.Single(AssetDetailLayout.FilterHistory(history, " AVERY "));
        Assert.Equal("1 of 2 events", AssetDetailLayout.HistoryCount(1, 2));
        Assert.Equal("1 event", AssetDetailLayout.HistoryCount(1, 1));
        Assert.Equal("No events match the filter.", AssetDetailLayout.HistoryEmpty(2));
        Assert.Equal("Checkout", AssetDetailLayout.ActionTitle("checkout"));
        Assert.Equal("", AssetDetailLayout.ActionGlyph("checkout"));

        Assert.Equal("https://inventory.example.com/uploads/a.pdf",
            AssetDetailLayout.ResolveFileUrl("/uploads/a.pdf", "https://inventory.example.com/")?.ToString());
        Assert.Null(AssetDetailLayout.ResolveFileUrl("javascript:alert(1)", "https://inventory.example.com"));
        Assert.Null(AssetDetailLayout.ResolveFileUrl("uploads/a.pdf", null));
    }

    // MARK: - Re-allocation

    private sealed class FakeSnipe : IAssetCheckout
    {
        public List<string> Calls { get; } = new();
        public bool CheckinFails { get; init; }

        public Task<SnipeResponse?> CheckinAssetAsync(int assetId, SnipeCheckinRequest? request = null)
        {
            Calls.Add($"checkin {assetId}");
            return Task.FromResult<SnipeResponse?>(new SnipeResponse { Status = CheckinFails ? "error" : "success", Messages = "locked" });
        }

        public Task<SnipeResponse?> CheckoutAssetAsync(int assetId, SnipeCheckoutRequest request)
        {
            Calls.Add($"checkout {assetId} to {request.AssignedUser}: {request.Note}");
            return Task.FromResult<SnipeResponse?>(new SnipeResponse { Status = "success" });
        }
    }

    [Fact]
    public async Task Reallocation_ChecksInFirstOnlyWhenAssigned()
    {
        var snipe = new FakeSnipe();
        Assert.Null(await AssetReallocation.RunAsync(snipe, Asset(1, "A", person: "Avery"), 9, "  "));
        Assert.Null(await AssetReallocation.RunAsync(snipe, Asset(2, "B"), 9, "Term loan"));
        Assert.Equal(new[] { "checkin 1", "checkout 1 to 9: Re-allocated via FleetMate", "checkout 2 to 9: Term loan" }, snipe.Calls);

        var failing = new FakeSnipe { CheckinFails = true };
        Assert.Equal("Check-in failed: locked", await AssetReallocation.RunAsync(failing, Asset(3, "C", person: "Avery"), 9, null));
        Assert.Single(failing.Calls);
    }

    // MARK: - Demo data

    [Fact]
    public void DemoData_IsFictional()
    {
        var assets = InventoryDemoData.Assets(Now);
        Assert.All(assets, a => Assert.StartsWith("DEMO-", a.AssetTag));
        Assert.All(assets.Where(a => a.AssignedTo != null), a => Assert.EndsWith("@example.com", a.AssignedTo!.Email));
        Assert.All(assets, a => Assert.NotEmpty(InventoryDemoData.History(a, Now)));
        Assert.Equal(assets.Count, assets.Select(a => a.Id).Distinct().Count());
        var facets = FleetMate.Core.Knowledge.HandbookAssetFacets.For(assets[0].Model?.Name, assets[0].Category?.Name,
            assets[0].Platform, assets[0].CustomFieldByName("Device Management Service"), assets[0].CustomFieldByName("Fleet"));
        Assert.NotEmpty(InventoryDemoData.Handbook().RelatedByFacet(facets));
    }
}
