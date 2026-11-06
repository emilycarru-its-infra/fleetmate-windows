namespace FleetMate.Core.Models.Tickets;

/// <summary>Where the detail pane's ticket actions go, and whether they keep their labels.</summary>
public enum DetailActionsFit
{
    /// <summary>On the ticket number row, with labels, as on the Mac.</summary>
    Inline,
    /// <summary>On their own row under the number row, with labels.</summary>
    OwnRow,
    /// <summary>On their own row, icons only: even a whole row is too narrow for the labels.</summary>
    OwnRowIconsOnly,
}

public static class TicketDetailActionsFit
{
    /// <summary>
    /// Keep the labels wherever they fit. <paramref name="labelledWidth"/> is
    /// what the actions need with labels, <paramref name="inlineRoom"/> what
    /// the number row leaves beside the ticket number (and Save and Discard),
    /// and <paramref name="rowWidth"/> a whole row.
    /// </summary>
    public static DetailActionsFit Place(double labelledWidth, double inlineRoom, double rowWidth)
    {
        if (labelledWidth <= inlineRoom) return DetailActionsFit.Inline;
        return labelledWidth <= rowWidth ? DetailActionsFit.OwnRow : DetailActionsFit.OwnRowIconsOnly;
    }
}
