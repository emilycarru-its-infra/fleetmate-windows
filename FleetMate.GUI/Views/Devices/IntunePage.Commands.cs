using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Devices;

public partial class IntunePage : IPageCommands
{
    bool IPageCommands.Execute(AppShortcut shortcut)
    {
        if (shortcut != AppShortcut.ClearFilters) return false;
        ClearFacetSelection();
        ApplyFilters();
        return true;
    }
}
