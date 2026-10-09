using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Development;

/// <summary>The Development tab's filter is the open segment's, which the view owns.</summary>
public partial class DevelopmentPage : ITabSearch
{
    public TabSearchScope? SearchScope => View.SearchScope;

    public event EventHandler? SearchScopeChanged
    {
        add => View.SearchScopeChanged += value;
        remove => View.SearchScopeChanged -= value;
    }
}
