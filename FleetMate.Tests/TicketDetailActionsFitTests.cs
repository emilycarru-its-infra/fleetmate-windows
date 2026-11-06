using FleetMate.Core.Models.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The detail pane's ticket actions keep their labels wherever they fit:
/// on the number row as on the Mac, else on their own row, and icons only
/// when even a whole row is too narrow.
/// </summary>
public class TicketDetailActionsFitTests
{
    [Theory]
    [InlineData(560, 600, 900, DetailActionsFit.Inline)]
    [InlineData(560, 560, 900, DetailActionsFit.Inline)]
    [InlineData(560, 400, 700, DetailActionsFit.OwnRow)]
    [InlineData(560, 0, 560, DetailActionsFit.OwnRow)]
    [InlineData(560, 200, 480, DetailActionsFit.OwnRowIconsOnly)]
    public void LabelsStayWhereverTheyFit(double labelled, double inlineRoom, double row, DetailActionsFit expected)
    {
        Assert.Equal(expected, TicketDetailActionsFit.Place(labelled, inlineRoom, row));
    }
}
