using System.Windows;
using FleetMate.Core.Models.Projects;

namespace FleetMate.GUI.Views.Projects;

/// <summary>
/// The Projects strip's tiles as filters. Picking one narrows Queries, Mine,
/// Recent, the board and the legacy list to the items that tile counts — the
/// signed-in user's open work items that match it, or GitHub items for the
/// GitHub Issues tile. Picking it again, or Clear in the bar under the strip,
/// shows everything.
/// </summary>
public partial class BoardsPage
{
    /// <summary>The ids the picked tile counts, kept in step with the user's open items as they load.</summary>
    private HashSet<int> _tileIds = new();

    private void HookTileFilter()
    {
        var filter = ProjectsTileFilter.Shared;
        filter.Changed += (_, _) => Dispatcher.BeginInvoke(OnTileFilterChanged);
        if (_app != null)
        {
            // The tile's set follows the user's items as they reload.
            _app.CacheChanged += key =>
            {
                if (key != App.MyWorkItemsKey || !filter.IsActive) return;
                Dispatcher.BeginInvoke(OnTileFilterChanged);
            };
        }
        UpdateTileFilterBar();
    }

    private bool TilePasses(UnifiedTask task) =>
        ProjectsTileFilter.Passes(task, ProjectsTileFilter.Shared.Selected, _tileIds);

    private void OnTileFilterChanged()
    {
        var tile = ProjectsTileFilter.Shared.Selected;
        _tileIds = tile is { } t
            ? ProjectsTileFilter.Ids(_app?.CachedMyWorkItems ?? new List<WorkItem>(), t, DateTime.UtcNow)
            : new HashSet<int>();

        UpdateTileFilterBar();
        Widgets.Invalidate();

        if (!IsInitialized) return;
        UpdateDisplay();
        RefreshQueriesDisplay();
        UpdateListDisplay();
    }

    private void UpdateTileFilterBar()
    {
        if (TileFilterBar is null) return;
        var tile = ProjectsTileFilter.Shared.Selected;
        TileFilterBar.Visibility = tile == null ? Visibility.Collapsed : Visibility.Visible;
        TileFilterLabel.Text = tile is { } t ? $"Showing only: {ProjectsTileFilter.Title(t)}" : "";
    }

    private void OnClearTileFilter(object sender, RoutedEventArgs e) => ProjectsTileFilter.Shared.Clear();
}
