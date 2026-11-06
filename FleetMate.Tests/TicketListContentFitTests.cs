using FleetMate.Core.Models.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The columns other than Title are as wide as what they hold, as on the Mac,
/// and Title takes the rest; an indented child's ID always fits.
/// </summary>
public class TicketListContentFitTests
{
    private static Dictionary<TicketListColumn, double> Natural(IReadOnlyDictionary<TicketListColumn, double> content) =>
        TicketListColumns.All.ToDictionary(c => c,
            c => TicketListColumnFit.Natural(c, null, content.TryGetValue(c, out var w) ? w : null));

    /// <summary>Roughly what a queue's rows hold: long names, short statuses.</summary>
    private static Dictionary<TicketListColumn, double> QueueContent() => new()
    {
        [TicketListColumn.Id] = TicketListColumnFit.IdWidth(1, 62),
        [TicketListColumn.Status] = 96,
        [TicketListColumn.Priority] = 72,
        [TicketListColumn.Requestor] = 150,
        [TicketListColumn.Responsible] = 150,
        [TicketListColumn.Age] = 56,
        [TicketListColumn.Modified] = 128,
        [TicketListColumn.Created] = 128,
    };

    [Fact]
    public void ADraggedWidthWinsOverContent()
    {
        Assert.Equal(260, TicketListColumnFit.Natural(TicketListColumn.Requestor, 260, 150));
    }

    [Fact]
    public void ContentIsHeldBetweenTheFloorAndTheMaximum()
    {
        Assert.Equal(150, TicketListColumnFit.Natural(TicketListColumn.Requestor, null, 150));
        Assert.Equal(TicketListColumnFit.MaxContentWidth(TicketListColumn.Requestor),
            TicketListColumnFit.Natural(TicketListColumn.Requestor, null, 900));
        Assert.Equal(TicketListColumnFit.Floor(TicketListColumn.Priority),
            TicketListColumnFit.Natural(TicketListColumn.Priority, null, 20));
    }

    [Fact]
    public void WithNothingMeasuredAColumnKeepsTheMacDefault()
    {
        Assert.Equal(TicketListColumns.DefaultWidth(TicketListColumn.Status),
            TicketListColumnFit.Natural(TicketListColumn.Status, null, null));
        Assert.Equal(TicketListColumns.DefaultWidth(TicketListColumn.Title),
            TicketListColumnFit.Natural(TicketListColumn.Title, null, 300));
    }

    [Fact]
    public void EachOutlineLevelWidensTheIdColumnByItsIndent()
    {
        var top = TicketListColumnFit.IdWidth(0, 62);
        var child = TicketListColumnFit.IdWidth(1, 62);

        Assert.Equal(TicketListColumnFit.IndentPerLevel, child - top);
        Assert.True(top >= 62 + TicketListColumnFit.DisclosureWidth + TicketListColumnFit.CellPadding);
    }

    [Fact]
    public void TitleTakesWhatTheContentSizedColumnsLeave()
    {
        var desired = Natural(QueueContent());

        var fit = TicketListColumnFit.Fit(desired, 1300);

        Assert.Equal(150, fit[TicketListColumn.Requestor]);
        Assert.Equal(150, fit[TicketListColumn.Responsible]);
        Assert.Equal(1300, fit.Values.Sum());
        Assert.True(fit[TicketListColumn.Title] > TicketListColumnFit.TitleMinWidth);
    }

    [Fact]
    public void NamesKeepTheirWidthWhileTheDatesGiveWay()
    {
        var desired = Natural(QueueContent());
        var others = desired.Where(p => p.Key != TicketListColumn.Title).Sum(p => p.Value);
        // Ten pixels too narrow for every column at its content width.
        var pane = others + TicketListColumnFit.TitleMinWidth - 10;

        var fit = TicketListColumnFit.Fit(desired, pane);

        Assert.Equal(150, fit[TicketListColumn.Requestor]);
        Assert.Equal(150, fit[TicketListColumn.Responsible]);
        Assert.True(fit[TicketListColumn.Modified] < 128 || fit[TicketListColumn.Created] < 128 || fit[TicketListColumn.Status] < 96);
        Assert.True(fit.Values.Sum() <= pane);
    }

    [Fact]
    public void AnIndentedIdIsNeverCut()
    {
        var content = QueueContent();
        var idNeeds = TicketListColumnFit.IdWidth(2, 62);
        content[TicketListColumn.Id] = idNeeds;
        var minimums = new Dictionary<TicketListColumn, double> { [TicketListColumn.Id] = idNeeds };

        var fit = TicketListColumnFit.Fit(Natural(content), 300, minimums: minimums);

        Assert.Equal(idNeeds, fit[TicketListColumn.Id]);
        Assert.Equal(TicketListColumnFit.Floor(TicketListColumn.Requestor), fit[TicketListColumn.Requestor]);
    }

    [Fact]
    public void ASavedDefaultWidthIsNotADrag()
    {
        var layout = new TicketListColumnLayout
        {
            Widths = new Dictionary<string, double>
            {
                ["Requestor"] = TicketListColumns.DefaultWidth(TicketListColumn.Requestor),
                ["Responsible"] = 175,
            },
        };

        Assert.False(layout.IsDragged(TicketListColumn.Requestor));
        Assert.True(layout.IsDragged(TicketListColumn.Responsible));
        Assert.False(layout.IsDragged(TicketListColumn.Status));
    }
}
