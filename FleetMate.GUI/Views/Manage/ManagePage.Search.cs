using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Manage;

/// <summary>The list filter, driven by the toolbar search field; the page's own box is hidden.</summary>
public partial class ManagePage : ITabSearch
{
    public TabSearchScope? SearchScope => new(SearchBox, "Search machines or rooms");

    // The page has one list, so its filter never changes.
    public event EventHandler? SearchScopeChanged { add { } remove { } }
}
