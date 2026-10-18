using System.Windows;
using System.Windows.Controls;
using FleetMate.GUI.Views.Development.Repos;
using FleetMate.GUI.Views.Shared.Widgets;

namespace FleetMate.GUI.Views.Development;

/// <summary>
/// The Development tab: a page shell around <see cref="DevelopmentView"/>,
/// which owns the pull requests, the inbox and the activity sidebar. The
/// Graphs strip above follows the open sub-tab: each has its own cards, and
/// Repos shows the selected repository's Insights there.
/// </summary>
public partial class DevelopmentPage : Page, IWidgetFilterHost
{
    /// <summary>Repos Insights never take more than this share of the page; past it they scroll.</summary>
    private const double InsightsShare = 0.45;

    private RepoInsightsView? _insights;
    private ScrollViewer? _insightsHost;

    /// <summary>
    /// Each Development widget opens the segment and filter it counts: a
    /// repository bar opens its sub-tab filtered to that repository, the pull
    /// request figures Pulls for their source, the pipeline figures Pipelines
    /// with that status.
    /// </summary>
    public void ApplyWidgetFilter(string category, string value)
    {
        switch (category)
        {
            case WidgetCatalog.Category.Repository:
                View.ShowRepository(value);
                break;
            case WidgetCatalog.Category.CommitRepository:
                View.ShowCommitsRepository(value);
                break;
            case WidgetCatalog.Category.PipelineRepository:
                View.ShowPipelinesRepository(value);
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
        Widgets.Builder = BuildWidgets;
        View.SegmentChanged += (_, _) => Widgets.Invalidate();
        View.ReposShownChanged += (_, _) => Widgets.Invalidate();
        Loaded += (_, _) => HookSkills();
        SizeChanged += (_, _) => SizeInsights();
    }

    private bool _skillsHooked;

    /// <summary>The Skills graphs follow the catalog as it syncs. The store exists once the app has started.</summary>
    private void HookSkills()
    {
        if (_skillsHooked || Application.Current is not App { Skills: { } skills }) return;
        _skillsHooked = true;
        skills.Changed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (View.CurrentSegment == DevelopmentSegment.Skills) Widgets.Invalidate();
        });
    }

    private List<UIElement> BuildWidgets()
    {
        if (Application.Current is not App app) return new();
        var segment = View.CurrentSegment;
        if (segment == DevelopmentSegment.Repos) return new() { InsightsHost() };
        return WidgetCatalog.Development(app, segment, (category, label) => ApplyWidgetFilter(category, WidgetCatalog.FilterValue(label)));
    }

    /// <summary>The Repos strip: the workspace's Insights, kept across redraws so its charts are not reread.</summary>
    private UIElement InsightsHost()
    {
        if (_insightsHost != null) return _insightsHost;
        _insights = new RepoInsightsView { OpenFile = View.OpenRepoFile };
        _insights.Attach(View.ReposModel);
        _insightsHost = new ScrollViewer
        {
            Content = _insights,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        WidgetFlowPanel.SetUnits(_insightsHost, 99);
        SizeInsights();
        return _insightsHost;
    }

    private void SizeInsights()
    {
        if (_insightsHost != null && ActualHeight > 0) _insightsHost.MaxHeight = Math.Max(240, ActualHeight * InsightsShare);
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (View.IsReposShown)
        {
            await View.RefreshReposAsync();
            return;
        }
        // Refresh means fresh: GitHub data is rebuilt in full, not incrementally.
        FleetMate.Core.Services.Projects.GitHubSync.RequestFullResync();
        await View.RefreshAsync();
    }
}
