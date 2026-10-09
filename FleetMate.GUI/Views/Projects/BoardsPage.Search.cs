using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Projects;

/// <summary>
/// The filter for the view showing, driven by the toolbar search field: tasks on
/// the board and the flat lists, work items under Queries, items on a GitHub
/// Project board. The page's own boxes are hidden.
/// </summary>
public partial class BoardsPage : ITabSearch
{
    public TabSearchScope? SearchScope =>
        ProjectsFilters.Visibility == System.Windows.Visibility.Visible ? new(ProjectsSearchBox, "Search project items")
        : ListFilters.Visibility == System.Windows.Visibility.Visible ? new(ListSearchBox, "Search work items")
        : new(SearchBox, "Search tasks");

    public event EventHandler? SearchScopeChanged;

    private void RaiseSearchScopeChanged() => SearchScopeChanged?.Invoke(this, EventArgs.Empty);
}
