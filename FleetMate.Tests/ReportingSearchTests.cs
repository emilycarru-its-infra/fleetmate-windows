using FleetMate.Core.Models.Reporting;
using FleetMate.Core.Services.Search;
using FleetMate.GUI.Views.Reporting;
using Xunit;
using L = FleetMate.Core.Links.FleetMateLink;
using RmDevice = ReportMate.App.Services.FleetDevice;
using RmInventory = ReportMate.App.Services.InventorySummary;
using RmModules = ReportMate.App.Services.DeviceModuleSummaries;

namespace FleetMate.Tests;

public class ReportingSearchTests
{
    private static SearchSources Sources() => new()
    {
        ReportingDevices = new[]
        {
            new ReportingDevice("C02ABC123", "Lab-Mac-01", Hostname: "labmac01", User: "Alex Doe", AssetTag: "A1001", Platform: "macOS"),
            new ReportingDevice("PF3XYZ99", "Studio-PC", User: "Sam Roe", AssetTag: "A1002", Platform: "Windows"),
        },
    };

    private static SearchGroup Reporting(string query) =>
        Assert.Single(GlobalSearch.Search(query, Sources()), g => g.Category == SearchCategory.Reporting);

    [Theory]
    [InlineData("studio", "Name")]
    [InlineData("pf3xyz", "Serial")]
    [InlineData("A1001", "Asset tag")]
    [InlineData("alex", "User")]
    [InlineData("labmac", "Host")]
    public void MatchesEachSearchableField(string query, string field) =>
        Assert.StartsWith(field + ":", Assert.Single(Reporting(query).Hits).MatchLabel);

    [Fact]
    public void HitOpensTheDeviceOnTheReportingTab()
    {
        var hit = Assert.Single(Reporting("Studio").Hits);
        Assert.Equal("Studio-PC", hit.Title);
        Assert.Equal("Windows · Sam Roe", hit.Subtitle);
        Assert.Equal("fleetmate://reporting/device/PF3XYZ99", hit.Link);
        var link = Assert.IsType<L.Reporting>(L.Parse(hit.Link));
        Assert.Equal("reportmate://device/PF3XYZ99", link.ToReportMateUrl());
    }

    [Fact]
    public void ReportingSitsBetweenDevicesAndInventory() =>
        Assert.Equal(SearchCategory.Devices + 1, SearchCategory.Reporting);

    [Fact]
    public void ReportingLinksRoundTrip()
    {
        Assert.Equal(new L.Reporting("dashboard"), L.Parse("fleetmate://reporting"));
        Assert.Equal(new L.Reporting("device/SAMPLE1?tab=installs"), L.Parse("fleetmate://reporting/device/SAMPLE1?tab=installs"));
        Assert.Equal(new L.Reporting("applications/usage/Visual%20Studio%20Code"),
            L.Parse("fleetmate://reporting/applications/usage/Visual%20Studio%20Code"));
        var link = L.Parse("fleetmate://reporting/devices?search=ABC");
        Assert.Equal("fleetmate://reporting/devices?search=ABC", link.ToLink());
    }

    [Fact]
    public void RecordCarriesTheInventoryOwnerAndTag()
    {
        var device = new RmDevice
        {
            SerialNumber = "ABC123", Name = "", Hostname = "lab01", Platform = "Windows",
            Modules = new RmModules { Inventory = new RmInventory { Owner = "Alex Doe", AssetTag = "A9" } },
        };
        Assert.Equal(new ReportingDevice("ABC123", "ABC123", "lab01", "Alex Doe", "A9", "Windows"),
            ReportingDeviceList.Record(device));
    }

    [Fact]
    public void ReloadsWhenStaleButNotRightAfterAFailedTry()
    {
        var now = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(ReportingDeviceList.Stale(null, null, now));
        Assert.False(ReportingDeviceList.Stale(now.AddMinutes(-2), now.AddMinutes(-2), now));
        Assert.True(ReportingDeviceList.Stale(now.AddMinutes(-6), now.AddMinutes(-6), now));
        Assert.False(ReportingDeviceList.Stale(null, now.AddSeconds(-20), now));
    }
}
