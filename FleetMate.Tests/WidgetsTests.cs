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
        // 1000px fits floor((1000+12)/(240+12)) = 4 units per row. Five
        // units need two rows, balanced two and three rather than three and
        // a stretched leftover.
        var rows = WidgetFlowPanel.Rows(new[] { 1, 1, 1, 2 }, 1000);
        Assert.Equal(new[] { new[] { 0, 1 }, new[] { 2, 3 } }, rows.Select(r => r.ToArray()));

        Assert.Single(WidgetFlowPanel.Rows(new[] { 1, 1, 2 }, 1000));
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1200)]
    [InlineData(600)]
    public void Rows_SingleRowKeepsTheTicketCardsTogetherWhereTheyWouldWrap(double width)
    {
        // The Tickets strip: the figures card and five charts.
        var units = new[] { 1, 1, 1, 1, 1, 1 };

        Assert.True(WidgetFlowPanel.Rows(units, width).Count > 1);
        var row = Assert.Single(WidgetFlowPanel.Rows(units, width, singleRow: true));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, row);
        Assert.Empty(WidgetFlowPanel.Rows(Array.Empty<int>(), width, singleRow: true));
    }

    [Fact]
    public void SingleRowPanel_ArrangesEveryCardSideBySideWithinTheWidth()
    {
        RunSta(() =>
        {
            var panel = new WidgetFlowPanel { SingleRow = true, EqualHeights = true };
            for (var i = 0; i < 6; i++)
                panel.Children.Add(new System.Windows.Controls.Border { Height = 40 + i * 10, VerticalAlignment = VerticalAlignment.Stretch });

            panel.Measure(new Size(1100, double.PositiveInfinity));
            panel.Arrange(new Rect(panel.DesiredSize));

            var slots = panel.Children.Cast<FrameworkElement>()
                .Select(c => System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(c)).ToList();
            Assert.All(slots, s => Assert.Equal(0, s.Y));
            Assert.All(slots, s => Assert.Equal(slots[0].Width, s.Width, 3));
            Assert.All(slots, s => Assert.Equal(90, s.Height, 3));
            Assert.True(slots[^1].Right <= 1100 + 0.001);
            Assert.Equal(90, panel.DesiredSize.Height, 3);
        });
    }

    [Fact]
    public void KpiTiles_GoFromThreeColumnsToTwoCompactColumnsWhenNarrow()
    {
        RunSta(() =>
        {
            var panel = new KpiTilePanel();
            for (var i = 0; i < 6; i++) panel.Children.Add(new System.Windows.Controls.Border { Height = 30 });

            panel.Measure(new Size(300, double.PositiveInfinity));
            Assert.Equal(60, panel.DesiredSize.Height, 3);

            panel.Measure(new Size(150, double.PositiveInfinity));
            Assert.Equal(90, panel.DesiredSize.Height, 3);
            panel.Arrange(new Rect(0, 0, 150, 120));
            var last = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot((FrameworkElement)panel.Children[5]);
            Assert.Equal(new Rect(75, 80, 75, 40), last);
        });
    }

    [Fact]
    public void Donut_KeepsTheLegendBesideItWhereItFitsAndStacksASmallerDonutOverItWhereNot()
    {
        RunSta(() =>
        {
            var sizes = new List<double>();
            var chart = new System.Windows.Controls.Border();
            var legend = new System.Windows.Controls.Border { Height = 50 };
            var panel = new AdaptiveDonutPanel(chart, legend, 110, sizes.Add);

            // A wide card: donut and legend side by side, the legend after the donut.
            panel.Measure(new Size(300, 240));
            panel.Arrange(new Rect(0, 0, 300, panel.DesiredSize.Height));
            Assert.False(panel.IsStacked);
            Assert.Equal(110, panel.DesiredSize.Height, 3);
            Assert.Equal(new Rect(124, 0, 176, 110), System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(legend));

            // A one-unit card at a laptop width: a 90px donut over the full-width legend.
            panel.Measure(new Size(180, 240));
            panel.Arrange(new Rect(0, 0, 180, panel.DesiredSize.Height));
            Assert.True(panel.IsStacked);
            Assert.Equal(90 + 6 + 50, panel.DesiredSize.Height, 3);
            Assert.Equal(new Rect(45, 0, 90, 90), System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(chart));
            Assert.Equal(new Rect(0, 96, 180, 50), System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(legend));

            // The donut is resized only when the layout changes.
            Assert.Equal(new double[] { 110, 90 }, sizes);
            panel.Measure(new Size(170, 240));
            Assert.Equal(2, sizes.Count);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
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
    [InlineData("Development", "Commits", true)]
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

    [Theory]
    [InlineData("Resolved", true)]
    [InlineData("Completed", true)]
    [InlineData("closed", true)]
    [InlineData("Active", false)]
    [InlineData("New", false)]
    public void ResolvedWorkIsFinished(string state, bool expected) =>
        Assert.Equal(expected, WidgetCatalog.IsClosed(state));

    [Fact]
    public void CollapsedStateKeyMatchesMacOS() =>
        Assert.Equal("widgets.collapsed.Devices", WidgetVisibility.PersistenceKey("Devices"));
}
