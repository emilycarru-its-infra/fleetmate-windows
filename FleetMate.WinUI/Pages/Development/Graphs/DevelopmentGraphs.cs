using FleetMate.Core.Development;
using FleetMate.Core.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace FleetMate.WinUI.Pages.Development.Graphs;

/// <summary>
/// Development's Graphs strip: one set of cards per sub-tab, built from what
/// the tab has already loaded, and Repos' Insights. The cards, their figures
/// and what each click opens come from FleetMate.Core's
/// <see cref="DevelopmentGraphCards"/>, the same ones FleetMate.GUI draws;
/// this draws them (<see cref="GraphCardView"/>) and routes the clicks to the sub-tabs.
/// </summary>
public static class DevelopmentGraphs
{
    private const string Tab = "Development";
    private static bool _skillsHooked;

    /// <summary>
    /// Whether the strip shows, kept with FleetMate.GUI's choice in the
    /// edition's HKCU key. Development opens on the work itself, so it starts hidden.
    /// </summary>
    public static bool IsShown
    {
        get => WidgetVisibility.IsShown(Tab);
        set => WidgetVisibility.SetShown(Tab, value);
    }

    /// <summary>Whether a change to the <paramref name="cacheKey"/> cache redraws <paramref name="segment"/>'s strip.</summary>
    public static bool Follows(DevelopmentSegment segment, string cacheKey) => DevelopmentGraphCards.Follows(segment, cacheKey);

    /// <summary>The strip for <paramref name="segment"/>, or null when it has none.</summary>
    public static UIElement? Build(DevelopmentPage page, DevelopmentSegment segment)
    {
        if (segment == DevelopmentSegment.Repos) return page.Repos.Insights;
        HookSkills(page);

        var services = App.Services;
        var now = DateTime.Now;
        var cards = segment switch
        {
            DevelopmentSegment.Pulls => DevelopmentGraphCards.Pulls(services.DevelopmentPullRequests, services.IsCacheLoading("PullRequests"), now),
            DevelopmentSegment.Commits => DevelopmentGraphCards.Commits(services.DevelopmentCommits, services.IsCacheLoading("Commits"), now),
            DevelopmentSegment.Pipelines => DevelopmentGraphCards.Pipelines(services.DevelopmentRuns, services.IsCacheLoading("Runs"), now),
            _ => DevelopmentGraphCards.Skills(services.Skills?.Entries, services.Skills?.IsSyncing == true),
        };

        var panel = new GraphFlowPanel { Margin = new Thickness(0, 0, 0, 4) };
        AutomationProperties.SetName(panel, $"{segment} graphs");
        AutomationProperties.SetAutomationId(panel, "DevelopmentGraphs");
        foreach (var card in cards)
        {
            try
            {
                panel.Children.Add(GraphCardView.Build(card, filter => Open(page, filter)));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[development] Could not draw the {Card} graph", card.Title);
            }
        }
        // Brushes are read when the cards are drawn, so a theme change draws them again.
        panel.ActualThemeChanged += (_, _) => page.RenderGraphs();
        return panel;
    }

    /// <summary>The Skills catalog raises Changed on the thread pool: redraw on the UI thread while Skills shows.</summary>
    private static void HookSkills(DevelopmentPage page)
    {
        if (_skillsHooked || App.Services.Skills is not { } skills) return;
        _skillsHooked = true;
        skills.Changed += (_, _) => page.DispatcherQueue.TryEnqueue(() =>
        {
            if (page.CurrentSegment == DevelopmentSegment.Skills && IsShown) page.RenderGraphs();
        });
    }

    /// <summary>
    /// A figure's click: open the sub-tab it counts with that filter, as
    /// FleetMate.GUI's Development page applies a widget filter.
    /// </summary>
    internal static void Open(DevelopmentPage page, GraphFilter filter)
    {
        switch (filter.Category)
        {
            case GraphCategory.Repository:
                page.ShowSegment(DevelopmentSegment.Pulls);
                page.Pulls.ShowRepository(filter.Value);
                break;
            case GraphCategory.Source when Enum.TryParse<DevelopmentSourceFilter>(filter.Value, out var source):
                page.ShowSegment(DevelopmentSegment.Pulls);
                page.Pulls.ShowSource(source);
                break;
            case GraphCategory.PullRequest
                when App.Services.DevelopmentPullRequests?.PullRequests.FirstOrDefault(p => p.Id == filter.Value) is { } pr:
                page.ShowSegment(DevelopmentSegment.Pulls);
                page.Pulls.ShowPullRequest(pr);
                break;
            case GraphCategory.CommitRepository:
                page.ShowSegment(DevelopmentSegment.Commits);
                page.Commits.ShowRepository(filter.Value);
                break;
            case GraphCategory.PipelineRepository:
                page.ShowSegment(DevelopmentSegment.Pipelines);
                page.Pipelines.ShowRepository(filter.Value);
                break;
            case GraphCategory.PipelineStatus when Enum.TryParse<PipelineStatusFilter>(filter.Value, out var status):
                page.ShowSegment(DevelopmentSegment.Pipelines);
                page.Pipelines.ShowStatus(status);
                break;
            case GraphCategory.Segment when filter.Value == GraphCategory.InboxSegment:
                page.ShowInbox();
                break;
            case GraphCategory.Search:
                Search(filter.Value);
                break;
        }
    }

    /// <summary>
    /// Type the value into the toolbar search, which filters the open sub-tab
    /// (an author, a pipeline) through <see cref="MainWindow.SearchChanged"/>.
    /// </summary>
    private static void Search(string value)
    {
        if (App.Current.Window?.Content is FrameworkElement root && root.FindName("SearchBox") is AutoSuggestBox box)
            box.Text = value;
        else
            Log.Debug("[development] No toolbar search to apply {Value} to", value);
    }
}
