using System.Windows;
using FleetMate.GUI.Views.Shared.Widgets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The per-tab Widgets section: row layout, treemap geometry, and which tabs get which data.</summary>
public class WidgetsTests
{
    [Fact]
    public void Rows_FillWidthWhileEachUnitStaysAtLeastMinimum()
    {
        // 1000px fits floor((1000+12)/(240+12)) = 4 units per row.
        var rows = WidgetFlowPanel.Rows(new[] { 1, 1, 1, 2 }, 1000);
        Assert.Equal(new[] { new[] { 0, 1, 2 }, new[] { 3 } }, rows.Select(r => r.ToArray()));

        Assert.Single(WidgetFlowPanel.Rows(new[] { 1, 1, 2 }, 1000));
    }

    [Fact]
    public void Rows_NarrowWindowGivesOneCardPerRow()
    {
        var rows = WidgetFlowPanel.Rows(new[] { 1, 2, 1 }, 300);
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void Treemap_TilesCoverTheBoundsLargestFirst()
    {
        var slices = new[] { new ChartSlice("Windows", 60), new ChartSlice("macOS", 30), new ChartSlice("iOS", 10), new ChartSlice("None", 0) };
        var rects = WidgetCards.TreemapRects(slices, new Rect(0, 0, 400, 100));

        Assert.Equal(new[] { "Windows", "macOS", "iOS" }, rects.Select(r => r.Slice.Label));
        Assert.Equal(40000, rects.Sum(r => r.Rect.Width * r.Rect.Height), 3);
        Assert.Equal(240, rects[0].Rect.Width, 3);
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Projects", true)]
    [InlineData("Devices", true)]
    [InlineData("Inventory", true)]
    [InlineData("Tickets", true)]
    [InlineData("Identity", false)]
    [InlineData("Manage", false)]
    public void OnlyFiveTabsHaveWidgets(string tab, bool expected) =>
        Assert.Equal(expected, WidgetCatalog.HasWidgets(tab));

    [Theory]
    [InlineData("Devices", "Devices", true)]
    [InlineData("Devices", "Assets", false)]
    [InlineData("Projects", "Issues", true)]
    [InlineData("Development", "Runs", true)]
    [InlineData("Development", "Commits", false)]
    public void RedrawOnlyForTheTabsOwnData(string tab, string key, bool expected) =>
        Assert.Equal(expected, WidgetCatalog.DependsOn(tab, key));

    [Theory]
    [InlineData("In Process", true)]
    [InlineData("New", true)]
    [InlineData(null, true)]
    [InlineData("Closed", false)]
    [InlineData("cancelled", false)]
    [InlineData("Canceled", false)]
    public void TicketIsActiveUnlessClosedByName(string? status, bool expected) =>
        Assert.Equal(expected, WidgetCatalog.IsActiveTicket(status));

    [Fact]
    public void CollapsedStateKeyMatchesMacOS() =>
        Assert.Equal("widgets.collapsed.Devices", WidgetsSection.PersistenceKey("Devices"));
}
