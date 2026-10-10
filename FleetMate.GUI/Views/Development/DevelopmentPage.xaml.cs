using System.Windows;
using System.Windows.Controls;
using FleetMate.GUI.Views.Shared.Widgets;

namespace FleetMate.GUI.Views.Development;

/// <summary>
/// The Development tab: a page shell around <see cref="DevelopmentView"/>,
/// which owns the pull requests, the inbox and the activity sidebar.
/// </summary>
public partial class DevelopmentPage : Page, IWidgetFilterHost
{
    /// <summary>
    /// Each Development widget opens the segment and filter it counts: a
    /// repository bar opens Pulls for that repository, the Inbox and pull
    /// request figures their segment and source, the pipeline figures
    /// Pipelines with that status.
    /// </summary>
    public void ApplyWidgetFilter(string category, string value)
    {
        switch (category)
        {
            case WidgetCatalog.Category.Repository:
                View.ShowRepository(value);
                break;
            case WidgetCatalog.Category.Segment when value == WidgetCatalog.InboxSegment:
                View.ShowInbox();
                break;
            case WidgetCatalog.Category.Source when Enum.TryParse<DevelopmentSourceFilter>(value, out var source):
                View.ShowPullRequests(source);
                break;
            case WidgetCatalog.Category.PipelineStatus when Enum.TryParse<PipelineStatusFilter>(value, out var status):
                View.ShowPipelines(status);
                break;
        }
    }

    public DevelopmentPage()
    {
        InitializeComponent();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        // Refresh means fresh: GitHub data is rebuilt in full, not incrementally.
        FleetMate.Core.Services.Projects.GitHubSync.RequestFullResync();
        await View.RefreshAsync();
    }
}
