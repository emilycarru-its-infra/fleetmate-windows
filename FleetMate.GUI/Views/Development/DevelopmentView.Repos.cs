using System.Windows;
using FleetMate.GUI.Views.Development.Repos;

namespace FleetMate.GUI.Views.Development;

/// <summary>
/// The Repos segment, first and the default as in FleetMate for Mac: tracked
/// checkouts in the sidebar, the selected one's git pane, files, editor and
/// Insights in the detail column. The other segments keep their order after it,
/// and every link and widget that opens Pulls, Commits or Pipelines still picks
/// its segment explicitly.
/// </summary>
public partial class DevelopmentView
{
    private RepoWorkspaceModel? _repos;

    /// <summary>Raised when the Repos segment is shown or left, so the page can hide what does not apply.</summary>
    public event EventHandler? ReposShownChanged;

    public bool IsReposShown => ReposSegment.IsChecked == true;

    private RepoWorkspaceModel Repos
    {
        get
        {
            if (_repos != null) return _repos;
            _repos = new RepoWorkspaceModel();
            ReposSidebar.Attach(_repos);
            ReposWorkspace.Attach(_repos);
            // Settings links, clones or (un)tracks a repository: the list follows at once.
            RepoWorkspaceModel.RegistryChanged += () => Dispatcher.InvokeAsync(async () =>
            {
                _repos.ReloadRecords();
                await _repos.RefreshStatusesAsync();
            });
            return _repos;
        }
    }

    private void OnReposSegmentChecked(object sender, RoutedEventArgs e) => ApplyReposSegment();

    private void OnReposSegmentUnchecked(object sender, RoutedEventArgs e) => ApplyReposSegment();

    /// <summary>The segment is checked in XAML before the view is ready, so it is applied once loaded.</summary>
    private void OnReposSegmentLoaded(object sender, RoutedEventArgs e) => ApplyReposSegment();

    private async void ApplyReposSegment()
    {
        if (!IsInitialized) return;
        var shown = IsReposShown;
        if (shown)
        {
            // With every other segment unchecked, the shared switch hides their
            // lists and filters and tells the toolbar the search scope changed.
            OnSegmentChanged(this, new RoutedEventArgs());
        }
        ReposSidebar.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        ReposWorkspace.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (shown) SearchScopeChanged?.Invoke(this, EventArgs.Empty);
        ReposShownChanged?.Invoke(this, EventArgs.Empty);
        if (!shown) return;

        // Selecting the first (or remembered) repository loads it; after that,
        // coming back only refreshes what changed while the segment was away.
        Repos.ReloadRecords();
        await Repos.RefreshStatusesAsync();
    }

    /// <summary>The page's Refresh while Repos shows: the registry, every status, and the selected checkout.</summary>
    public async Task RefreshReposAsync()
    {
        Repos.ReloadRecords();
        await Repos.RefreshStatusesAsync();
        await Repos.LoadSelectedAsync();
    }
}
