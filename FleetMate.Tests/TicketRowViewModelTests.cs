using System.Windows;
using FleetMate.Core.Models.Tickets;
using FleetMate.GUI.Views.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The outline row's display shape: indent, disclosure glyph, and the
/// passthroughs the template binds.
/// </summary>
public class TicketRowViewModelTests
{
    private static TicketRowViewModel Vm(
        int depth = 0, int childCount = 0, bool expanded = true, int id = 1) => new()
    {
        Row = new TicketRow
        {
            Ticket = new TdxTicket { Id = id, Title = "Printer jam" },
            Depth = depth,
            ChildCount = childCount,
            IsExpanded = expanded,
        },
    };

    [Theory]
    [InlineData(0, 4)]
    [InlineData(1, 20)]
    [InlineData(2, 36)]
    [InlineData(3, 52)]
    public void IndentGrowsWithDepth(int depth, double expectedLeft)
    {
        Assert.Equal(expectedLeft, Vm(depth).IndentMargin.Left);
    }

    [Fact]
    public void IndentKeepsTheRowsOwnPadding()
    {
        // The base padding has to survive indenting, or a root row's text sits
        // flush against the edge.
        var margin = Vm(depth: 0).IndentMargin;

        Assert.Equal(4, margin.Top);
        Assert.Equal(4, margin.Bottom);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 12)]
    [InlineData(2, 24)]
    public void CardIndentStepsNarrowerThanTheList(int depth, double expectedLeft)
    {
        // A board column is only 280px wide, so the list's 16px step would eat it.
        Assert.Equal(expectedLeft, Vm(depth).CardIndentMargin.Left);
    }

    [Fact]
    public void CardIndentKeepsTheGapBetweenStackedCards()
    {
        Assert.Equal(8, Vm().CardIndentMargin.Bottom);
    }

    [Fact]
    public void GlyphPointsDownWhenOpenAndRightWhenFolded()
    {
        // Matching the glyph to the direction the content moves is what makes a
        // disclosure control legible without a label.
        Assert.Equal("▾", Vm(childCount: 2, expanded: true).TriangleGlyph);
        Assert.Equal("▸", Vm(childCount: 2, expanded: false).TriangleGlyph);
    }

    [Fact]
    public void LeavesReserveTheTriangleSpaceWithoutShowingOne()
    {
        // Hidden, not Collapsed: collapsing would pull leaf titles left of their
        // siblings' and break the visual column.
        Assert.Equal(Visibility.Hidden, Vm(childCount: 0).TriangleVisibility);
        Assert.Equal(Visibility.Visible, Vm(childCount: 1).TriangleVisibility);
    }

    [Theory]
    [InlineData(1, "1 child ticket")]
    [InlineData(3, "3 child tickets")]
    public void ChildCountTooltipReadsNaturally(int count, string expected)
    {
        Assert.Equal(expected, Vm(childCount: count).ChildCountTooltip);
    }

    [Fact]
    public void PassesTicketFieldsThroughForTheTemplate()
    {
        var vm = new TicketRowViewModel
        {
            Row = new TicketRow
            {
                Ticket = new TdxTicket
                {
                    Id = 7,
                    Title = "Printer jam",
                    StatusName = "In Progress",
                    PriorityName = "High",
                    RequestorName = "Ada Lovelace",
                    ResponsibleFullName = "Grace Hopper",
                },
            },
        };

        Assert.Equal(7, vm.Id);
        Assert.Equal("Printer jam", vm.Title);
        Assert.Equal("In Progress", vm.StatusName);
        Assert.Equal("High", vm.PriorityName);
        Assert.Equal("Ada Lovelace", vm.RequestorName);
        Assert.Equal("Grace Hopper", vm.ResponsibleFullName);
    }

    [Fact]
    public void SurfacesAgeAndIdleTimeSeparately()
    {
        var vm = new TicketRowViewModel
        {
            Row = new TicketRow
            {
                Ticket = new TdxTicket
                {
                    Id = 1,
                    DaysOld = 400,
                    ModifiedDate = DateTime.UtcNow.AddDays(-3),
                },
            },
        };

        Assert.Equal("400d", vm.AgeLabel);
        Assert.Equal("3d", vm.LastActivityLabel);
    }
}

/// <summary>
/// The fold interactions the page performs on the shared collapse set. Exercised
/// through the Core helpers the page calls, since the page itself needs a
/// Dispatcher.
/// </summary>
public class TicketOutlineInteractionTests
{
    private static TdxTicket T(int id, int? parent = null) => new() { Id = id, ParentId = parent ?? 0 };

    private static readonly TdxTicket[] Sample =
    {
        T(1), T(2, 1), T(3, 2), T(4),
    };

    [Fact]
    public void TogglingAParentHidesAndRestoresItsSubtree()
    {
        var collapsed = new HashSet<int>();
        var tree = TicketHierarchy.Build(Sample);

        Assert.Equal(4, TicketHierarchy.Flatten(tree, collapsed).Count);

        collapsed.Add(1);
        Assert.Equal(new[] { 1, 4 }, TicketHierarchy.Flatten(tree, collapsed).Select(r => r.Id));

        collapsed.Remove(1);
        Assert.Equal(4, TicketHierarchy.Flatten(tree, collapsed).Count);
    }

    [Fact]
    public void ExpandingAncestorsRevealsADeepChild()
    {
        // A deep link to a child lands on nothing if an ancestor is folded, so
        // the page walks the parent chain and unfolds it first.
        var collapsed = new HashSet<int> { 1, 2 };
        var byId = Sample.ToDictionary(t => t.Id);

        var cursor = TicketHierarchy.ParentTicketId(byId[3]);
        var guard = new HashSet<int>();
        while (cursor is { } parentId && guard.Add(parentId))
        {
            collapsed.Remove(parentId);
            cursor = byId.TryGetValue(parentId, out var parent)
                ? TicketHierarchy.ParentTicketId(parent)
                : null;
        }

        var visible = TicketHierarchy.Flatten(TicketHierarchy.Build(Sample), collapsed).Select(r => r.Id);
        Assert.Contains(3, visible);
    }

    [Fact]
    public void RevealTerminatesOnACyclicParentChain()
    {
        // The same guard that stops Build recursing has to stop the reveal walk,
        // or a deep link into cyclic data hangs the UI thread.
        var cyclic = new[] { T(1, 2), T(2, 1) };
        var byId = cyclic.ToDictionary(t => t.Id);

        var collapsed = new HashSet<int> { 1, 2 };
        var cursor = TicketHierarchy.ParentTicketId(byId[1]);
        var guard = new HashSet<int>();
        var iterations = 0;

        while (cursor is { } parentId && guard.Add(parentId))
        {
            if (++iterations > 100) break;
            collapsed.Remove(parentId);
            cursor = byId.TryGetValue(parentId, out var parent)
                ? TicketHierarchy.ParentTicketId(parent)
                : null;
        }

        Assert.True(iterations <= 2);
    }

    [Fact]
    public void CollapseAllThenExpandAllRoundTrips()
    {
        var tree = TicketHierarchy.Build(Sample);

        var collapsed = TicketHierarchy.AllParentIds(tree);
        Assert.Equal(new[] { 1, 4 }, TicketHierarchy.Flatten(tree, collapsed).Select(r => r.Id));

        collapsed.Clear();
        Assert.Equal(4, TicketHierarchy.Flatten(tree, collapsed).Count);
    }

    [Fact]
    public void BoardColumnsNestWithinThemselves()
    {
        // Grouped by status, a child whose parent is in another column has no
        // parent to nest under and renders as a root — the honest reading.
        var open = new[] { T(1), T(2, 1) };
        var closed = new[] { T(3, 2) };

        var openRows = TicketHierarchy.Flatten(TicketHierarchy.Build(open), new HashSet<int>());
        var closedRows = TicketHierarchy.Flatten(TicketHierarchy.Build(closed), new HashSet<int>());

        Assert.Equal(new[] { 0, 1 }, openRows.Select(r => r.Depth));
        Assert.Equal(new[] { 0 }, closedRows.Select(r => r.Depth));
    }

    [Fact]
    public void CollapseStateIsSharedAcrossViews()
    {
        // One set, not one per view: switching modes must not silently
        // re-expand everything the operator folded.
        var collapsed = new HashSet<int> { 1 };

        var listRows = TicketHierarchy.Flatten(TicketHierarchy.Build(Sample), collapsed);
        var boardRows = TicketHierarchy.Flatten(
            TicketHierarchy.Build(Sample.Where(t => t.Id != 4)), collapsed);

        Assert.DoesNotContain(2, listRows.Select(r => r.Id));
        Assert.DoesNotContain(2, boardRows.Select(r => r.Id));
    }
}
