using System.Globalization;
using FleetMate.Core.Models.Tickets;
using FleetMate.GUI.Views.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The List view's table: the macOS columns, sorting by header, the saved
/// column layout, and children staying under their parent in any sort.
/// </summary>
public class TicketListColumnsTests
{
    [Fact]
    public void ColumnsFollowTheMacOrder()
    {
        Assert.Equal(
            new[] { "ID", "Title", "Status", "Priority", "Requestor", "Responsible", "Age", "Modified", "Created" },
            TicketListColumns.All.Select(TicketListColumns.Header));
    }

    [Theory]
    [InlineData(TicketListColumn.Id, 130)]
    [InlineData(TicketListColumn.Title, 500)]
    [InlineData(TicketListColumn.Status, 110)]
    [InlineData(TicketListColumn.Priority, 90)]
    [InlineData(TicketListColumn.Requestor, 130)]
    [InlineData(TicketListColumn.Responsible, 130)]
    [InlineData(TicketListColumn.Age, 70)]
    [InlineData(TicketListColumn.Modified, 120)]
    [InlineData(TicketListColumn.Created, 120)]
    public void DefaultWidthsAreTheMacWidths(TicketListColumn column, double width)
    {
        Assert.Equal(width, TicketListColumns.DefaultWidth(column));
    }

    [Fact]
    public void EveryHeaderSortsByAFieldTheToolbarOffers()
    {
        // A header naming a field the toolbar lacks would set a sort the
        // toolbar could not show.
        foreach (var column in TicketListColumns.All)
            Assert.Contains(TicketListColumns.SortField(column), TicketListSort.Fields);
    }

    [Fact]
    public void HeadersRoundTrip()
    {
        foreach (var column in TicketListColumns.All)
            Assert.Equal(column, TicketListColumns.FromHeader(TicketListColumns.Header(column)));
        Assert.Null(TicketListColumns.FromHeader("Bogus"));
        Assert.Null(TicketListColumns.FromHeader(null));
    }
}

public class TicketListSortTests
{
    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static TdxTicket T(int id, string? title = null, string? status = null, int createdDay = 0, int? modifiedDay = null, int parent = 0) => new()
    {
        Id = id,
        Title = title,
        StatusName = status,
        CreatedDate = Base.AddDays(createdDay),
        ModifiedDate = modifiedDay is { } m ? Base.AddDays(m) : null,
        ParentId = parent,
    };

    private static int[] Ids(IEnumerable<TdxTicket> tickets) => tickets.Select(t => t.Id).ToArray();

    [Fact]
    public void SortsByIdBothWays()
    {
        var tickets = new[] { T(2), T(3), T(1) };
        Assert.Equal(new[] { 1, 2, 3 }, Ids(TicketListSort.Apply(tickets, "ID", ascending: true)));
        Assert.Equal(new[] { 3, 2, 1 }, Ids(TicketListSort.Apply(tickets, "ID", ascending: false)));
    }

    [Fact]
    public void TextSortIgnoresCaseAndPutsMissingFirstWhenAscending()
    {
        var tickets = new[] { T(1, title: "banana"), T(2, title: null), T(3, title: "Apple") };
        Assert.Equal(new[] { 2, 3, 1 }, Ids(TicketListSort.Apply(tickets, "Title", ascending: true)));
    }

    [Fact]
    public void DescendingAgeIsOldestFirst()
    {
        // A queue is read by what has waited longest, as on Mac.
        var tickets = new[] { T(1, createdDay: 5), T(2, createdDay: 1), T(3, createdDay: 9) };
        Assert.Equal(new[] { 2, 1, 3 }, Ids(TicketListSort.Apply(tickets, "Age", ascending: false)));
        Assert.Equal(new[] { 3, 1, 2 }, Ids(TicketListSort.Apply(tickets, "Age", ascending: true)));
    }

    [Fact]
    public void UnknownFieldSortsByModified()
    {
        var tickets = new[] { T(1, modifiedDay: 1), T(2, modifiedDay: 3), T(3, modifiedDay: 2) };
        Assert.Equal(new[] { 2, 3, 1 }, Ids(TicketListSort.Apply(tickets, "Nonsense", ascending: false)));
    }

    [Fact]
    public void TiesKeepTheirOrder()
    {
        var tickets = new[] { T(5, status: "New"), T(4, status: "New"), T(6, status: "New") };
        Assert.Equal(new[] { 5, 4, 6 }, Ids(TicketListSort.Apply(tickets, "Status", ascending: true)));
    }

    [Fact]
    public void ClickingAnotherHeaderSortsItAscending()
    {
        Assert.Equal(("Title", true), TicketListSort.AfterHeaderClick("Modified", false, TicketListColumn.Title));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClickingTheSortedHeaderReversesIt(bool ascending)
    {
        Assert.Equal(("Modified", !ascending), TicketListSort.AfterHeaderClick("Modified", ascending, TicketListColumn.Modified));
    }

    [Fact]
    public void HeadersAnnounceTheirSortState()
    {
        Assert.Equal("Age, sorted ascending", TicketListSort.SpokenHeader(TicketListColumn.Age, "Age", true));
        Assert.Equal("Age, sorted descending", TicketListSort.SpokenHeader(TicketListColumn.Age, "Age", false));
        Assert.Equal("Title, not sorted", TicketListSort.SpokenHeader(TicketListColumn.Title, "Age", true));
        Assert.Equal("Created, sorted ascending", TicketListSort.SpokenHeader(TicketListColumn.Created, "Created", true));
    }

    [Fact]
    public void ChildrenStayUnderTheirParentInSortedOrder()
    {
        // The outline is built from the sorted list, so children follow the
        // same sort beneath their parent rather than scattering through it.
        var tickets = new[]
        {
            T(10, title: "Parent"),
            T(11, title: "Zebra child", parent: 10),
            T(12, title: "Alpha child", parent: 10),
            T(20, title: "Another"),
        };

        var sorted = TicketListSort.Apply(tickets, "Title", ascending: true);
        var rows = TicketHierarchy.Flatten(TicketHierarchy.Build(sorted), new HashSet<int>());

        Assert.Equal(new[] { 20, 10, 12, 11 }, rows.Select(r => r.Id));
        Assert.Equal(new[] { 0, 0, 1, 1 }, rows.Select(r => r.Depth));
        Assert.Equal(2, rows[1].ChildCount);
    }
}

public class TicketListColumnLayoutTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    public void UnreadableSaveFallsBackToDefaults(string? json)
    {
        var layout = TicketListColumnLayout.Parse(json);

        Assert.Equal(TicketListColumns.All, layout.Arrange());
        Assert.All(TicketListColumns.All, c => Assert.Equal(TicketListColumns.DefaultWidth(c), layout.WidthOf(c)));
    }

    [Fact]
    public void LayoutRoundTrips()
    {
        var order = new[]
        {
            TicketListColumn.Title, TicketListColumn.Id, TicketListColumn.Status, TicketListColumn.Priority,
            TicketListColumn.Requestor, TicketListColumn.Responsible, TicketListColumn.Modified, TicketListColumn.Age,
            TicketListColumn.Created,
        };
        var saved = TicketListColumnLayout.Capture(order.Select(c => (c, c == TicketListColumn.Title ? 321.4 : 100.0)));

        var restored = TicketListColumnLayout.Parse(saved.Serialize());

        Assert.Equal(order, restored.Arrange());
        Assert.Equal(321, restored.WidthOf(TicketListColumn.Title));
        Assert.Equal(100, restored.WidthOf(TicketListColumn.Age));
    }

    [Fact]
    public void UnknownAndRepeatedColumnsAreDroppedAndMissingOnesKeepTheirPlace()
    {
        var layout = new TicketListColumnLayout
        {
            Order = { "Modified", "Bogus", "ID", "Modified", "Title" },
        };

        // Status follows Title by default, and so on down the defaults.
        Assert.Equal(new[]
        {
            TicketListColumn.Modified, TicketListColumn.Created, TicketListColumn.Id, TicketListColumn.Title, TicketListColumn.Status,
            TicketListColumn.Priority, TicketListColumn.Requestor, TicketListColumn.Responsible, TicketListColumn.Age,
        }, layout.Arrange());
    }

    [Fact]
    public void ALayoutSavedBeforeCreatedExistedPutsCreatedAfterModified()
    {
        var layout = new TicketListColumnLayout
        {
            Order = { "ID", "Title", "Status", "Priority", "Requestor", "Responsible", "Age", "Modified" },
        };

        Assert.Equal(TicketListColumns.All, layout.Arrange());
        Assert.Equal(TicketListColumns.DefaultWidth(TicketListColumn.Created), layout.WidthOf(TicketListColumn.Created));
    }

    [Theory]
    [InlineData(5, TicketListColumns.MinWidth)]
    [InlineData(250, 250)]
    [InlineData(-1, 130)]
    [InlineData(0, 130)]
    [InlineData(99999, 130)]
    [InlineData(double.NaN, 130)]
    [InlineData(double.PositiveInfinity, 130)]
    public void SavedWidthsAreValidated(double saved, double expected)
    {
        var layout = new TicketListColumnLayout { Widths = { ["ID"] = saved } };
        Assert.Equal(expected, layout.WidthOf(TicketListColumn.Id));
    }

    [Fact]
    public void CaptureSkipsUnusableWidths()
    {
        var layout = TicketListColumnLayout.Capture(new[] { (TicketListColumn.Id, double.NaN) });
        Assert.Equal(new[] { "ID" }, layout.Order);
        Assert.Empty(layout.Widths);
    }
}

public class TicketListFormatTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(13, false)]
    [InlineData(14, true)]
    [InlineData(400, true)]
    public void AgeTurnsOrangeAtTwoWeeks(int? days, bool highlighted)
    {
        Assert.Equal(highlighted, TicketListFormat.IsAgeHighlighted(days));
    }

    [Fact]
    public void ModifiedReadsAsTheMacDoes()
    {
        var local = new DateTime(2026, 10, 15, 19, 1, 0, DateTimeKind.Local);
        Assert.Equal("Oct 15 at 7:01 PM", TicketListFormat.Modified(local, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void MissingModifiedIsADash()
    {
        Assert.Equal("-", TicketListFormat.Modified(null));
        Assert.Equal("-", TicketListFormat.Modified(default(DateTime)));
    }
}

public class TicketTableRowTests
{
    private static TicketRowViewModel Vm(int depth = 0, int childCount = 0, bool expanded = true) => new()
    {
        Row = new TicketRow
        {
            Ticket = new TdxTicket { Id = 42, Title = "Printer jam", StatusName = "New" },
            Depth = depth,
            ChildCount = childCount,
            IsExpanded = expanded,
        },
    };

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 14)]
    [InlineData(2, 28)]
    public void ChildrenIndentUnderTheirParent(int depth, double left)
    {
        Assert.Equal(left, Vm(depth).TableIndent.Left);
    }

    [Fact]
    public void ParentShowsItsChildCountBadge()
    {
        Assert.Equal(System.Windows.Visibility.Visible, Vm(childCount: 3).ChildBadgeVisibility);
        Assert.Equal("3", Vm(childCount: 3).ChildCountLabel);
        Assert.Equal(System.Windows.Visibility.Collapsed, Vm().ChildBadgeVisibility);
    }

    [Fact]
    public void RowsHaveSpokenNames()
    {
        Assert.Equal("Ticket 42, Printer jam, New", Vm().SpokenName);
        Assert.Equal("Ticket 42, Printer jam, New, 2 child tickets, collapsed", Vm(childCount: 2, expanded: false).SpokenName);
        Assert.Equal("Ticket 42, Printer jam, New, child ticket", Vm(depth: 1).SpokenName);
    }

    [Fact]
    public void DisclosureSaysWhatItWillDo()
    {
        Assert.Equal("Hide 1 child ticket", Vm(childCount: 1, expanded: true).DisclosureName);
        Assert.Equal("Show 2 child tickets", Vm(childCount: 2, expanded: false).DisclosureName);
    }

    [Fact]
    public void IdReadsWithAHash()
    {
        Assert.Equal("#42", Vm().IdLabel);
    }
}

public class TicketListColumnFitTests
{
    private static Dictionary<TicketListColumn, double> Defaults() =>
        TicketListColumns.All.ToDictionary(c => c, TicketListColumns.DefaultWidth);

    private static double Others(IReadOnlyDictionary<TicketListColumn, double> widths) =>
        widths.Where(p => p.Key != TicketListColumn.Title).Sum(p => p.Value);

    [Fact]
    public void TitleTakesTheLeftoverWidthInAWidePane()
    {
        var fit = TicketListColumnFit.Fit(Defaults(), 1600);

        Assert.Equal(1600 - Others(Defaults()), fit[TicketListColumn.Title]);
        Assert.All(fit.Where(p => p.Key != TicketListColumn.Title), p => Assert.Equal(TicketListColumns.DefaultWidth(p.Key), p.Value));
        Assert.Equal(1600, fit.Values.Sum());
    }

    [Theory]
    [InlineData(860)]
    [InlineData(1000)]
    [InlineData(1200)]
    public void ColumnsNeverRunPastANormalPane(double pane)
    {
        var fit = TicketListColumnFit.Fit(Defaults(), pane);

        Assert.True(fit.Values.Sum() <= pane, $"{fit.Values.Sum()} > {pane}");
        Assert.True(fit[TicketListColumn.Title] >= TicketListColumnFit.TitleMinWidth);
        Assert.All(fit.Where(p => p.Key != TicketListColumn.Title),
            p => Assert.True(p.Value >= TicketListColumnFit.Floor(p.Key) - 1, $"{p.Key} at {p.Value}"));
    }

    [Fact]
    public void SavedWidthsApplyWhenTheyFit()
    {
        var desired = Defaults();
        desired[TicketListColumn.Requestor] = 200;

        var fit = TicketListColumnFit.Fit(desired, 1600);

        Assert.Equal(200, fit[TicketListColumn.Requestor]);
        Assert.Equal(1600 - Others(desired), fit[TicketListColumn.Title]);
    }

    [Fact]
    public void AWideSavedWidthGivesWayInsteadOfScrolling()
    {
        var desired = Defaults();
        desired[TicketListColumn.Requestor] = 900;

        var fit = TicketListColumnFit.Fit(desired, 1200);

        Assert.True(fit.Values.Sum() <= 1200);
        Assert.True(fit[TicketListColumn.Requestor] < 900);
        // The widest column gives the most.
        Assert.True(900 - fit[TicketListColumn.Requestor] > TicketListColumns.DefaultWidth(TicketListColumn.Id) - fit[TicketListColumn.Id]);
    }

    [Fact]
    public void ADraggedColumnKeepsItsWidthWhileTheOthersMakeRoom()
    {
        var desired = Defaults();
        desired[TicketListColumn.Status] = 300;

        var fit = TicketListColumnFit.Fit(desired, 1000, held: TicketListColumn.Status);

        Assert.Equal(300, fit[TicketListColumn.Status]);
        Assert.True(fit.Values.Sum() <= 1000);
    }

    [Fact]
    public void FittingNeverWidensANarrowColumn()
    {
        var desired = Defaults();
        desired[TicketListColumn.Age] = 40;

        var fit = TicketListColumnFit.Fit(desired, 700);

        Assert.Equal(40, fit[TicketListColumn.Age]);
    }

    [Fact]
    public void ATooNarrowPaneScrollsWithEveryColumnAtItsFloor()
    {
        var fit = TicketListColumnFit.Fit(Defaults(), 300);

        Assert.Equal(TicketListColumnFit.TitleMinWidth, fit[TicketListColumn.Title]);
        Assert.All(fit.Where(p => p.Key != TicketListColumn.Title),
            p => Assert.True(Math.Abs(p.Value - TicketListColumnFit.Floor(p.Key)) <= 1, $"{p.Key} at {p.Value}"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    public void APaneNotLaidOutKeepsTheDesiredWidths(double pane)
    {
        var fit = TicketListColumnFit.Fit(Defaults(), pane);
        Assert.All(fit, p => Assert.Equal(TicketListColumns.DefaultWidth(p.Key), p.Value));
    }
}
