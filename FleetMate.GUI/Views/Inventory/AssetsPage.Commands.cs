using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Inventory;

public partial class AssetsPage : IPageCommands
{
    bool IPageCommands.Execute(AppShortcut shortcut)
    {
        if (shortcut != AppShortcut.ClearFilters) return false;
        ClearFilters();
        return true;
    }
}
