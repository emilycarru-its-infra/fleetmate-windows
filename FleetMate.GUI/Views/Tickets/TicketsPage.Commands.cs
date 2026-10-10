using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Tickets;

public partial class TicketsPage : IPageCommands
{
    /// <summary>Clear Filters clears every filter selection; the search and Assigned to Me stay.</summary>
    bool IPageCommands.Execute(AppShortcut shortcut)
    {
        if (shortcut != AppShortcut.ClearFilters) return false;
        _filters.ClearAll();
        return true;
    }
}
