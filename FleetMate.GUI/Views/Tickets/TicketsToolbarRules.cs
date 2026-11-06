namespace FleetMate.GUI.Views.Tickets;

/// <summary>Which of the three Tickets views is showing.</summary>
public enum TicketsViewMode
{
    List,
    Board,
    Inbox,
}

/// <summary>
/// What the Tickets toolbar shows in each view and at each width, kept apart
/// from the page so the rules are testable without a window. They follow the
/// Mac's TicketsView toolbar.
/// </summary>
internal static class TicketsToolbarRules
{
    /// <summary>
    /// Below these toolbar widths the button labels drop to icons, as the
    /// Mac's toolbar does in a narrow window. The board needs more room,
    /// since it adds the column picker and the sort controls.
    /// </summary>
    public const double CompactBelowOnBoard = 1200;
    public const double CompactBelowOnList = 960;

    public static TicketsViewMode ModeOf(bool board, bool inbox) =>
        inbox ? TicketsViewMode.Inbox : board ? TicketsViewMode.Board : TicketsViewMode.List;

    /// <summary>The column picker only means something on the board, as on Mac.</summary>
    public static bool ShowsBoardGrouping(TicketsViewMode mode) => mode == TicketsViewMode.Board;

    /// <summary>
    /// Sort field and direction show on the board only. In List view the
    /// column headers sort, as on Mac; both drive the same sort state.
    /// </summary>
    public static bool ShowsSortControls(TicketsViewMode mode) => mode == TicketsViewMode.Board;

    /// <summary>Expand All and Collapse All (row menu, Alt-click) fold the list and the board; the Inbox has no outline.</summary>
    public static bool ShowsFoldControls(TicketsViewMode mode) => mode != TicketsViewMode.Inbox;

    /// <summary>True when the toolbar is too narrow for the button labels.</summary>
    public static bool UsesCompactLabels(double toolbarWidth, TicketsViewMode mode) =>
        toolbarWidth > 0 && toolbarWidth < (mode == TicketsViewMode.Board ? CompactBelowOnBoard : CompactBelowOnList);

    /// <summary>
    /// Assigned to Me is a work queue, read as a list: turning it on from the
    /// board or the inbox switches to List, as on Mac. Turning it off leaves
    /// the view alone.
    /// </summary>
    public static TicketsViewMode ViewAfterAssignedToMe(TicketsViewMode current, bool assignedToMe) =>
        assignedToMe ? TicketsViewMode.List : current;

    public static string SortDirectionName(bool ascending) => ascending ? "Ascending" : "Descending";

    public static string SortDirectionToolTip(bool ascending) => ascending
        ? "Sorted ascending. Click to sort descending."
        : "Sorted descending. Click to sort ascending.";

    /// <summary>The three bars beside the arrow, top to bottom: growing when ascending, shrinking when descending.</summary>
    public static double[] SortBarWidths(bool ascending) =>
        ascending ? new[] { 4.0, 6.5, 9.0 } : new[] { 9.0, 6.5, 4.0 };

    /// <summary>
    /// A filter button's words, before its dropdown arrow. In a narrow toolbar
    /// an unfiltered category shows only its name and a count drops "selected",
    /// so the filters shrink before they scroll.
    /// </summary>
    public static string FilterButtonText(string title, IReadOnlyCollection<string> picked, bool compact) => picked.Count switch
    {
        0 => compact ? title : $"{title}: All",
        1 => $"{title}: {picked.First()}",
        _ => compact ? $"{title}: {picked.Count}" : $"{title}: {picked.Count} selected",
    };

    /// <summary>The filter button's accessible name: always the full words, never the arrow.</summary>
    public static string FilterButtonName(string title, IReadOnlyCollection<string> picked) =>
        FilterButtonText(title, picked, compact: false);

    /// <summary>The Inbox button's tooltip, with the unread count as the Mac's help text has it.</summary>
    public static string InboxToolTip(int unread) => unread > 0
        ? $"Inbox: {unread} unread (Ctrl+Alt+3)"
        : "Inbox: comments from the last 90 days that notified you (Ctrl+Alt+3)";
}
