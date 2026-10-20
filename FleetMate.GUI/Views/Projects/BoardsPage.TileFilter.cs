using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Models.Projects;
using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Projects;

/// <summary>
/// The Projects strip's tiles as filters. A tile counts the signed-in user's
/// items, so picking one shows Mine narrowed to that tile's set (the strip's
/// open issues for GitHub Issues). Picking it again, Clear Filter in the bar
/// under the strip, Clear Filters (Ctrl+Shift+K) or leaving Mine shows
/// everything. The Filters row's "My items" menu offers the same sets.
/// </summary>
public partial class BoardsPage : IPageCommands
{
    /// <summary>The ids the picked tile counts, kept in step with the user's open items as they load.</summary>
    private HashSet<int> _tileIds = new();
    private bool _syncingMyItems;

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
        FillMyItems();
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
        SyncMyItems();
        Widgets.Invalidate();

        if (!IsInitialized) return;
        // Picking a tile shows Mine; the view change redraws the list.
        if (tile != null && CurrentView != ProjectsView.Mine)
        {
            MineModeRadio.IsChecked = true;
            return;
        }
        UpdateDisplay();
        RefreshQueriesDisplay();
        UpdateListDisplay();
    }

    private void UpdateTileFilterBar()
    {
        if (TileFilterBar is null) return;
        var tile = ProjectsTileFilter.Shared.Selected;
        TileFilterBar.Visibility = tile == null ? Visibility.Collapsed : Visibility.Visible;
        TileFilterLabel.Text = tile is { } t ? $"Showing {ProjectsTileFilter.Title(t)}" : "";
    }

    private void OnClearTileFilter(object sender, RoutedEventArgs e) => ProjectsTileFilter.Shared.Clear();

    // ── The "My items" menu ──

    private void FillMyItems()
    {
        if (MyItemsCombo is null) return;
        _syncingMyItems = true;
        MyItemsCombo.Items.Clear();
        MyItemsCombo.Items.Add(new ComboBoxItem { Content = "All my items", Tag = null });
        foreach (var tile in Enum.GetValues<ProjectsTile>())
            MyItemsCombo.Items.Add(new ComboBoxItem { Content = ProjectsTileFilter.Title(tile), Tag = tile });
        _syncingMyItems = false;
        SyncMyItems();
    }

    private void SyncMyItems()
    {
        if (MyItemsCombo is null) return;
        _syncingMyItems = true;
        var tile = ProjectsTileFilter.Shared.Selected;
        MyItemsCombo.SelectedItem = MyItemsCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, tile));
        _syncingMyItems = false;
    }

    private void OnMyItemsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingMyItems || MyItemsCombo.SelectedItem is not ComboBoxItem item) return;
        ProjectsTileFilter.Shared.Select(item.Tag as ProjectsTile?);
    }

    // ── Clear Filters ──

    bool IPageCommands.Execute(AppShortcut shortcut)
    {
        if (shortcut != AppShortcut.ClearFilters) return false;
        ProjectsTileFilter.Shared.Clear();
        ShowClosedCheckbox.IsChecked = false;
        if (BucketFilter.Items.Count > 0) BucketFilter.SelectedIndex = 0;
        if (SearchBox.Text.Length > 0) SearchBox.Clear();
        return true;
    }
}
