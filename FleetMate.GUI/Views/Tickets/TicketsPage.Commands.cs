using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Tickets;

public partial class TicketsPage : IPageCommands
{
    /// <summary>Clear Filters puts Status, Group and Responsible back to All; the search stays.</summary>
    bool IPageCommands.Execute(AppShortcut shortcut)
    {
        if (shortcut != AppShortcut.ClearFilters) return false;
        StatusFilterComboBox.SelectedIndex = 0;
        GroupFilterComboBox.SelectedIndex = 0;
        ResponsibleFilterComboBox.SelectedIndex = 0;
        ApplyFiltersAndSort();
        return true;
    }
}
