using System.Text.Json;
using FleetMate.Core.Models.Tickets;
using Xunit;

namespace FleetMate.Tests;

public class TicketHierarchyBuildTests
{
    private static TdxTicket T(int id, int? parent = null, string title = "T") => new()
    {
        Id = id,
        Title = title,
        // TDX sends 0 rather than null for "no parent", so model that exactly.
        ParentId = parent ?? 0,
    };

    [Fact]
    public void ParentIdOfZeroMeansNoParent()
    {
        // A naive null check leaves every root claiming to be a child of ticket
        // zero, which nests the entire queue under nothing.
        Assert.Null(TicketHierarchy.ParentTicketId(T(1)));
        Assert.Equal(1, TicketHierarchy.ParentTicketId(T(2, parent: 1)));
    }

    [Fact]
    public void ChildrenNestUnderTheirParent()
    {
        var tree = TicketHierarchy.Build(new[]
        {
            T(100, title: "Parent"),
            T(101, parent: 100),
            T(102, parent: 100),
            T(200, title: "Standalone"),
        });

        Assert.Equal(new[] { 100, 200 }, tree.Select(n => n.Id));
        Assert.Equal(new[] { 101, 102 }, tree[0].Children.Select(n => n.Id));
        Assert.Empty(tree[1].Children);
    }

    [Fact]
    public void OrphanedChildStaysVisibleAsARoot()
    {
        // The parent is filtered out of the current view — closed, out of range,
        // another group. Dropping its children would hide real work.
        var tree = TicketHierarchy.Build(new[] { T(101, parent: 999), T(102, parent: 999) });

        Assert.Equal(new[] { 101, 102 }, tree.Select(n => n.Id));
    }

    [Fact]
    public void GrandchildrenNestRecursively()
    {
        var tree = TicketHierarchy.Build(new[] { T(1), T(2, parent: 1), T(3, parent: 2) });

        Assert.Single(tree);
        Assert.Equal(3, tree[0].Children[0].Children[0].Id);
        Assert.Equal(2, tree[0].DescendantCount);
    }

    [Fact]
    public void OrderIsPreservedAtEveryLevel()
    {
        var tree = TicketHierarchy.Build(new[]
        {
            T(1), T(5, parent: 1), T(3, parent: 1), T(4, parent: 1),
        });

        Assert.Equal(new[] { 5, 3, 4 }, tree[0].Children.Select(n => n.Id));
    }

    [Fact]
    public void SelfParentingTicketDoesNotRecurseForever()
    {
        var tree = TicketHierarchy.Build(new[] { T(7, parent: 7) });

        Assert.Equal(new[] { 7 }, tree.Select(n => n.Id));
        Assert.Empty(tree[0].Children);
    }

    [Fact]
    public void ParentCycleTerminates()
    {
        // TDX has no constraint preventing a ticket being its own ancestor, and
        // a cycle would otherwise recurse until the stack gave out — a hang
        // rather than a visible error.
        var tree = TicketHierarchy.Build(new[] { T(1, parent: 2), T(2, parent: 1) });

        Assert.True(tree.Count <= 2);
        Assert.True(TicketHierarchy.Flatten(tree, new HashSet<int>()).Count <= 4);
    }

    [Fact]
    public void LongerCycleAlsoTerminates()
    {
        var tree = TicketHierarchy.Build(new[]
        {
            T(1, parent: 3), T(2, parent: 1), T(3, parent: 2),
        });

        Assert.True(TicketHierarchy.Flatten(tree, new HashSet<int>()).Count <= 3);
    }

    [Fact]
    public void EmptyInputProducesAnEmptyTree()
    {
        Assert.Empty(TicketHierarchy.Build(Array.Empty<TdxTicket>()));
    }
}

public class TicketHierarchyFlattenTests
{
    private static TdxTicket T(int id, int? parent = null) => new() { Id = id, ParentId = parent ?? 0 };

    private static List<TicketRow> Rows(IEnumerable<TdxTicket> tickets, params int[] collapsed) =>
        TicketHierarchy.Flatten(TicketHierarchy.Build(tickets), new HashSet<int>(collapsed));

    [Fact]
    public void EverythingIsExpandedWhenNothingIsCollapsed()
    {
        // Expanded is the default: a queue that opens folded hides the work it
        // exists to show.
        var rows = Rows(new[] { T(1), T(2, 1), T(3, 1) });

        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.Id));
        Assert.Equal(new[] { 0, 1, 1 }, rows.Select(r => r.Depth));
        Assert.Equal(2, rows[0].ChildCount);
        Assert.True(rows[0].IsExpanded);
        Assert.True(rows[0].HasChildren);
    }

    [Fact]
    public void CollapsingAParentHidesItsWholeSubtree()
    {
        var rows = Rows(new[] { T(1), T(2, 1), T(3, 2), T(4) }, collapsed: 1);

        Assert.Equal(new[] { 1, 4 }, rows.Select(r => r.Id));
        Assert.False(rows[0].IsExpanded);
        Assert.True(rows[0].HasChildren);
    }

    [Fact]
    public void CollapsingAnInnerNodeKeepsItsAncestorsVisible()
    {
        var rows = Rows(new[] { T(1), T(2, 1), T(3, 2) }, collapsed: 2);

        Assert.Equal(new[] { 1, 2 }, rows.Select(r => r.Id));
        Assert.True(rows[0].IsExpanded);
        Assert.False(rows[1].IsExpanded);
    }

    [Fact]
    public void DepthTracksNestingLevel()
    {
        var rows = Rows(new[] { T(1), T(2, 1), T(3, 2), T(4, 3) });

        Assert.Equal(new[] { 0, 1, 2, 3 }, rows.Select(r => r.Depth));
    }

    [Fact]
    public void CollapsingALeafChangesNothingVisible()
    {
        var rows = Rows(new[] { T(1), T(2, 1) }, collapsed: 2);

        Assert.Equal(new[] { 1, 2 }, rows.Select(r => r.Id));
        Assert.False(rows[1].HasChildren);
    }

    [Fact]
    public void AllParentIdsFindsEveryFoldableNode()
    {
        var tree = TicketHierarchy.Build(new[] { T(1), T(2, 1), T(3, 2), T(4) });

        // 4 is a leaf and 3 has no children, so neither is foldable.
        Assert.Equal(new[] { 1, 2 }, TicketHierarchy.AllParentIds(tree).OrderBy(x => x));
    }

    [Fact]
    public void CollapseAllLeavesOnlyRoots()
    {
        var tickets = new[] { T(1), T(2, 1), T(3, 2), T(4) };
        var tree = TicketHierarchy.Build(tickets);
        var rows = TicketHierarchy.Flatten(tree, TicketHierarchy.AllParentIds(tree));

        Assert.Equal(new[] { 1, 4 }, rows.Select(r => r.Id));
    }
}

public class TdxTicketAgeTests
{
    private static readonly DateTime Now = new(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AgePrefersTheServerCount()
    {
        // The server's count accounts for the tenant's timezone and business
        // calendar in ways a local subtraction does not.
        var ticket = new TdxTicket { Id = 1, DaysOld = 412 };

        Assert.Equal(412, ticket.AgeInDays(Now));
        // Never "1 yr" — a queue is read in days.
        Assert.Equal("412d", ticket.AgeLabel(Now));
    }

    [Fact]
    public void AgeFallsBackToCreatedDate()
    {
        var ticket = new TdxTicket { Id = 1, CreatedDate = Now.AddDays(-16) };

        Assert.Equal(16, ticket.AgeInDays(Now));
        Assert.Equal("16d", ticket.AgeLabel(Now));
    }

    [Fact]
    public void AgeIsBlankWhenNothingSaysWhenItOpened()
    {
        var ticket = new TdxTicket { Id = 1 };

        Assert.Null(ticket.AgeInDays(Now));
        Assert.Equal("-", ticket.AgeLabel(Now));
    }

    [Fact]
    public void LastActivityMeasuresFromTheModifiedDate()
    {
        // An old ticket touched today is healthy; a young one untouched for
        // three weeks is not. Age alone cannot tell them apart.
        var ticket = new TdxTicket { Id = 1, DaysOld = 400, ModifiedDate = Now.AddDays(-3) };

        Assert.Equal(400, ticket.AgeInDays(Now));
        Assert.Equal(3, ticket.DaysSinceLastActivity(Now));
        Assert.Equal("3d", ticket.LastActivityLabel(Now));
    }

    [Fact]
    public void LastActivityFallsBackToAgeWhenNeverModified()
    {
        var ticket = new TdxTicket { Id = 1, DaysOld = 12 };

        Assert.Equal(12, ticket.DaysSinceLastActivity(Now));
    }
}

public class TdxFeedReplyTests
{
    private static TdxFeedEntry Decode(string json) =>
        JsonSerializer.Deserialize<TdxFeedEntry>(json)!;

    [Fact]
    public void AnEntryWithACountButNoBodiesNeedsHydrating()
    {
        // The ticket feed collection reports RepliesCount but always sends
        // Replies: [] — without hydrating, every thread renders as nothing.
        var entry = Decode("""{ "ID": 1, "RepliesCount": 3, "Replies": [] }""");

        Assert.True(entry.HasUnloadedReplies);
        Assert.Empty(entry.ReplyList);
    }

    [Fact]
    public void AnEntryWithLoadedRepliesDoesNotNeedHydrating()
    {
        var entry = Decode("""{ "ID": 1, "RepliesCount": 1, "Replies": [ { "ID": 2 } ] }""");

        Assert.False(entry.HasUnloadedReplies);
        Assert.Single(entry.ReplyList);
    }

    [Fact]
    public void AnEntryWithNoRepliesNeedsNothing()
    {
        Assert.False(Decode("""{ "ID": 1, "RepliesCount": 0 }""").HasUnloadedReplies);
        Assert.False(Decode("""{ "ID": 1 }""").HasUnloadedReplies);
    }

    [Fact]
    public void WithRepliesPreservesTheOriginalEntry()
    {
        var entry = Decode("""
            { "ID": 1, "Body": "hello", "CreatedFullName": "Ada", "IsPrivate": true, "RepliesCount": 2 }
            """);

        var hydrated = entry.WithReplies(new List<TdxFeedEntry> { new() { Id = 2 }, new() { Id = 3 } });

        Assert.Equal(1, hydrated.Id);
        Assert.Equal("hello", hydrated.Body);
        Assert.Equal("Ada", hydrated.CreatedFullName);
        Assert.True(hydrated.IsPrivate);
        Assert.Equal(2, hydrated.ReplyList.Count);
        Assert.False(hydrated.HasUnloadedReplies);
    }

    [Fact]
    public void WithRepliesFillsInACountWhenTheServerSentNone()
    {
        var hydrated = Decode("""{ "ID": 1 }""")
            .WithReplies(new List<TdxFeedEntry> { new() { Id = 2 } });

        Assert.Equal(1, hydrated.RepliesCount);
    }

    [Fact]
    public void ReplyListIsNeverNull()
    {
        // Callers iterate this directly; a null would be a crash in the feed view.
        Assert.NotNull(Decode("""{ "ID": 1 }""").ReplyList);
        Assert.Empty(Decode("""{ "ID": 1 }""").ReplyList);
    }
}
