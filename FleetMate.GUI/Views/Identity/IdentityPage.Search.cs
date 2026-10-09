using System.Windows;
using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Identity;

/// <summary>
/// The toolbar search field filters groups as you type, and looks users up in
/// Entra on Enter. The page's own boxes and its Search button are hidden.
/// </summary>
public partial class IdentityPage : ITabSearch
{
    public TabSearchScope? SearchScope => UsersPanel.Visibility == Visibility.Visible
        ? new(UsersSearchBox, "Find users, then Enter", () => _ = SearchUsersAsync())
        : new(GroupsSearchBox, "Filter groups");

    public event EventHandler? SearchScopeChanged;

    private void RaiseSearchScopeChanged() => SearchScopeChanged?.Invoke(this, EventArgs.Empty);
}
