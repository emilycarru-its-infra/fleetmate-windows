using System.Windows;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Models.Reporting;
using FleetMate.GUI.Views.Shared.Widgets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The widget figures and breakdowns, held to the macOS app's rules.</summary>
public class WidgetParityTests
{
    private static IntuneDevice Device(string? os, string? compliance = "compliant") =>
        new() { OperatingSystem = os, ComplianceState = compliance };

    // MARK: - Devices

    [Theory]
    [InlineData("macOS", "Macintosh")]
    [InlineData("iOS", "iOS/iPadOS")]
    [InlineData("iPadOS", "iOS/iPadOS")]
    [InlineData("Windows", "Windows")]
    [InlineData(null, "")]
    public void PlatformLabels_MatchTheMac(string? os, string expected) =>
        Assert.Equal(expected, WidgetCatalog.PlatformLabel(os));

    [Fact]
    public void Platforms_MergeAppleMobile_KeepFixedColours_AndTakeTopSix()
    {
        var devices = new List<IntuneDevice>();
        devices.AddRange(Enumerable.Range(0, 9).Select(_ => Device("Windows")));
        devices.AddRange(Enumerable.Range(0, 5).Select(_ => Device("macOS")));
        devices.AddRange(Enumerable.Range(0, 2).Select(_ => Device("iOS")));
        devices.AddRange(Enumerable.Range(0, 2).Select(_ => Device("iPadOS")));
        devices.Add(Device("Android"));
        devices.Add(Device("Linux"));
        devices.Add(Device("ChromeOS"));
        devices.Add(Device("Other"));
        devices.Add(Device(null));

        var platforms = WidgetCatalog.PlatformBreakdown(devices);

        Assert.Equal(6, platforms.Count);
        Assert.Equal(new[] { "Windows", "Macintosh", "iOS/iPadOS" }, platforms.Take(3).Select(p => p.Slice.Label));
        Assert.Equal(4, platforms[2].Slice.Value);
        Assert.Equal(WidgetPalette.Orange, platforms.Single(p => p.Slice.Label == "Macintosh").Color);
        Assert.Equal(WidgetPalette.Blue, platforms.Single(p => p.Slice.Label == "Windows").Color);
        Assert.Equal(WidgetPalette.Purple, platforms.Single(p => p.Slice.Label == "iOS/iPadOS").Color);
        Assert.DoesNotContain(platforms, p => p.Slice.Label == "");
    }

    [Fact]
    public void NonCompliant_IsOnlyIntunesNoncompliantState()
    {
        var devices = new[]
        {
            Device("Windows", "compliant"), Device("Windows", "noncompliant"), Device("Windows", "inGracePeriod"),
            Device("Windows", "unknown"), Device("Windows", null),
        };

        Assert.Equal(1, devices.Count(WidgetCatalog.IsNonCompliant));
        var slices = WidgetCatalog.ComplianceBreakdown(devices);
        Assert.Equal(new[] { ("Compliant", 4), ("Non-Compliant", 1) }, slices.Select(s => (s.Slice.Label, s.Slice.Value)));
    }

    [Fact]
    public void Compliance_DropsEmptySlices()
    {
        var slices = WidgetCatalog.ComplianceBreakdown(new[] { Device("Windows"), Device("macOS") });
        Assert.Equal("Compliant", Assert.Single(slices).Slice.Label);
        Assert.Empty(WidgetCatalog.ComplianceBreakdown(Array.Empty<IntuneDevice>()));
    }

    [Fact]
    public void ErrorCategories_SumDevices_TopEight()
    {
        var errors = Enum.GetValues<ErrorCategory>().Take(9)
            .Select((c, i) => new ErrorSummary { Category = c, DeviceCount = i + 1 })
            .Append(new ErrorSummary { Category = Enum.GetValues<ErrorCategory>()[0], DeviceCount = 20 })
            .ToList();

        var slices = WidgetCatalog.ErrorCategorySlices(errors);

        Assert.Equal(8, slices.Count);
        Assert.Equal(21, slices[0].Value);
        Assert.True(slices.Zip(slices.Skip(1)).All(p => p.First.Value >= p.Second.Value));
    }

    [Theory]
    [InlineData("Non-Compliant", "Noncompliant")]
    [InlineData("Compliant", "Compliant")]
    public void ComplianceClick_FindsTheFilterValue(string label, string expected) =>
        Assert.Equal(new[] { expected }, WidgetCatalog.MatchFilterValues(label, new[] { "Compliant", "Noncompliant", "Not Enrolled" }));

    [Fact]
    public void PlatformClick_SelectsEveryValueBehindTheLabel()
    {
        var values = WidgetCatalog.MatchFilterValues("iOS/iPadOS", new[] { "Windows", "iOS", "iPadOS", "macOS" }, WidgetCatalog.PlatformLabel);
        Assert.Equal(new[] { "iOS", "iPadOS" }, values);
        Assert.Equal(new[] { "macOS" },
            WidgetCatalog.MatchFilterValues("Macintosh", new[] { "Windows", "macOS" }, WidgetCatalog.PlatformLabel));
    }

    [Fact]
    public void Click_WithNoMatch_KeepsTheValue() =>
        Assert.Equal(new[] { "Linux" }, WidgetCatalog.MatchFilterValues("Linux", new[] { "Windows" }));

    [Theory]
    [InlineData("Active (12)", "Active")]
    [InlineData("deployed (3)", "deployed")]
    [InlineData("Platform/Repo", "Platform/Repo")]
    public void FilterValue_DropsTheCountSuffix(string label, string expected) =>
        Assert.Equal(expected, WidgetCatalog.FilterValue(label));

    // MARK: - Inventory

    private static SnipeAsset Asset(string? meta, string name, string? category = null) => new()
    {
        StatusLabel = new SnipeStatusLabel { Name = name, StatusMeta = meta },
        Category = category == null ? null : new SnipeRef { Name = category },
    };

    [Fact]
    public void AssetStatus_GroupsByStatusType_FallsBackToName_TopFive()
    {
        var assets = new List<SnipeAsset>
        {
            Asset("deployed", "In Use"), Asset("deployed", "Loaned"), Asset("deployed", "In Use"),
            Asset("deployable", "Ready"), Asset("deployable", "Spare"),
            Asset(null, "Repair"),
            Asset("archived", "Gone"), Asset("pending", "Ordered"), Asset("undeployable", "Broken"),
        };
        assets.Add(new SnipeAsset());

        var slices = WidgetCatalog.AssetStatusSlices(assets);

        Assert.Equal(5, slices.Count);
        Assert.Equal("deployed (3)", slices[0].Label);
        Assert.Equal("deployable (2)", slices[1].Label);
        Assert.Contains(WidgetCatalog.AssetStatusSlices(new[] { Asset(null, "Repair") }), s => s.Label == "Repair (1)");
        Assert.Contains(WidgetCatalog.AssetStatusSlices(new[] { new SnipeAsset() }), s => s.Label == "Unknown (1)");
    }

    [Fact]
    public void AssetCategories_TopEight_UncategorizedFallback()
    {
        var assets = Enumerable.Range(0, 10).SelectMany(i => Enumerable.Range(0, i + 1).Select(_ => Asset("deployed", "x", $"C{i}")))
            .Append(Asset("deployed", "x"))
            .ToList();

        var slices = WidgetCatalog.AssetCategorySlices(assets);

        Assert.Equal(8, slices.Count);
        Assert.Equal("C9", slices[0].Label);
        Assert.Contains(WidgetCatalog.AssetCategorySlices(new[] { Asset("deployed", "x") }), s => s.Label == "Uncategorized");
    }

    // MARK: - Projects

    private static WorkItem Item(string state, string? iteration = null) =>
        new() { Fields = new WorkItemFields { State = state, IterationPath = iteration } };

    [Fact]
    public void WorkItemSlices_CarryTheirCountInTheLabel()
    {
        var slices = WidgetCatalog.WorkItemSlices(new[] { Item("Active"), Item("Active"), Item("New") });
        Assert.Equal(new[] { "Active (2)", "New (1)" }, slices.Select(s => s.Label));
        Assert.Equal("Active", WidgetCards.DisplayLabel(slices[0]));
    }

    [Fact]
    public void SprintCaption_CountsOpenItemsInTheSprint()
    {
        var items = new[] { Item("Active", @"Proj\Sprint 4"), Item("New", @"Proj\Sprint 4"), Item("Active", @"Proj\Sprint 3") };
        Assert.Equal("Sprint: Sprint 4 · 2 open", WidgetCatalog.SprintCaption("Sprint 4", items));
    }

    // MARK: - Development

    [Fact]
    public void UnreadFigure_OpensInboxOnlyWhenSomethingIsUnread()
    {
        Assert.Equal((WidgetCatalog.Category.Segment, WidgetCatalog.InboxSegment), WidgetCatalog.UnreadTarget(3));
        Assert.Equal((WidgetCatalog.Category.Source, "All"), WidgetCatalog.UnreadTarget(0));
    }

    [Theory]
    [InlineData(4, false, "4")]
    [InlineData(0, true, "--")]
    public void Counts_ReadUnknownWhenTheProviderFailed(int value, bool failed, string expected) =>
        Assert.Equal(expected, WidgetCatalog.CountOrUnknown(value, failed));

    [Fact]
    public void RepositorySlices_MostFirst_TopEight()
    {
        var prs = Enumerable.Range(0, 10)
            .SelectMany(i => Enumerable.Range(0, i + 1).Select(_ => new UnifiedPullRequest { Container = "Org", Repository = $"R{i}" }))
            .ToList();

        var slices = WidgetCatalog.RepositorySlices(prs);

        Assert.Equal(8, slices.Count);
        Assert.Equal("Org/R9", slices[0].Label);
        Assert.Equal(10, slices[0].Value);
    }

    // MARK: - Shared components

    [Fact]
    public void Treemap_IsSquarified()
    {
        // Slice-and-dice would cut 400×100 into four thin strips; squarified
        // keeps each tile's worst side ratio low.
        var slices = new[] { new ChartSlice("A", 6), new ChartSlice("B", 6), new ChartSlice("C", 4), new ChartSlice("D", 3), new ChartSlice("E", 2), new ChartSlice("F", 2), new ChartSlice("G", 1) };
        var rects = WidgetCards.TreemapRects(slices, new Rect(0, 0, 600, 400));

        Assert.Equal(7, rects.Count);
        Assert.Equal(600 * 400, rects.Sum(r => r.Rect.Width * r.Rect.Height), 3);
        Assert.All(rects, r => Assert.True(Math.Max(r.Rect.Width / r.Rect.Height, r.Rect.Height / r.Rect.Width) < 3.5));
        foreach (var r in rects)
        {
            Assert.InRange(r.Rect.Left, -0.001, 600.001);
            Assert.InRange(r.Rect.Right, -0.001, 600.001);
            Assert.InRange(r.Rect.Bottom, -0.001, 400.001);
        }
    }

    [Fact]
    public void Treemap_IndexPointsBackToTheCallersSlice()
    {
        var slices = new[] { new ChartSlice("Small", 1), new ChartSlice("Big", 9) };
        var rects = WidgetCards.TreemapRects(slices, new Rect(0, 0, 100, 100));
        Assert.Equal(("Big", 1), (rects[0].Slice.Label, rects[0].Index));
    }

    [Theory]
    [InlineData(1, 3, 33)]
    [InlineData(2, 3, 66)]
    [InlineData(5, 0, 0)]
    public void Treemap_TooltipShareRoundsDown(int value, int total, int expected) =>
        Assert.Equal(expected, WidgetCards.SharePercent(value, total));

    [Theory]
    [InlineData(12, 100, "12")]
    [InlineData(11, 100, "")]
    [InlineData(0, 0, "")]
    public void Donut_NumbersOnlyWedgesWideEnough(int value, int total, string expected) =>
        Assert.Equal(expected, WidgetCards.WedgeLabel(value, total));

    [Fact]
    public void Legend_DropsADuplicateCount()
    {
        Assert.Equal("Active", WidgetCards.DisplayLabel(new ChartSlice("Active (5)", 5)));
        Assert.Equal("Build (5)", WidgetCards.DisplayLabel(new ChartSlice("Build (5)", 4)));
    }
}
