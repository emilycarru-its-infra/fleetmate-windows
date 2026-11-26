using FleetMate.Core.Links;
using FleetMate.Core.Models.Projects;
using FleetMate.WinUI.Shell.Widgets;

namespace FleetMate.WinUI.Pages.Projects;

/// <summary>
/// <c>fleetmate:</c> links to a work item or GitHub issue, and the QA
/// capture's walk through every view: each one with and without the strip,
/// a board grouped two ways, the GitHub Project board and an open detail.
/// </summary>
public sealed partial class ProjectsPage
{
    private bool _linkHooked;

    private void HookLinks()
    {
        if (_linkHooked) return;
        _linkHooked = true;
        _services.ProjectsLinkRequested += () => DispatcherQueue.TryEnqueue(async () => await TakePendingLinkAsync());
    }

    /// <summary>A lightbox's "Open in Projects", or a link, handed us a work item or GitHub issue to show.</summary>
    private async Task TakePendingLinkAsync()
    {
        if (_services.PendingProjectsLink is not { } link) return;
        _services.PendingProjectsLink = null;
        switch (link)
        {
            case FleetMateLink.GitHubIssue issue:
                ShowTask(new UnifiedTask
                {
                    Id = issue.Number.ToString(),
                    Provider = "github",
                    Title = $"{issue.Owner}/{issue.Repo}#{issue.Number}",
                    ExternalUrl = $"https://github.com/{issue.Owner}/{issue.Repo}/issues/{issue.Number}",
                });
                break;
            case FleetMateLink.WorkItem workItem:
                var loaded = _services.ProjectTasks.FirstOrDefault(t => t.Provider == "azdevops" && t.Id == workItem.Id.ToString());
                if (loaded == null && _services.DevOpsService is { } devOps && await devOps.GetWorkItemAsync(workItem.Id) is { } item)
                    loaded = item.AsUnifiedTask();
                ShowTask(loaded ?? new UnifiedTask
                {
                    Id = workItem.Id.ToString(),
                    Provider = "azdevops",
                    Title = $"#{workItem.Id}",
                    ExternalUrl = _services.Config.AzureDevOps is { } config ? $"{config.BaseUrl}/{config.Project}/_workitems/edit/{workItem.Id}" : null,
                });
                break;
        }
    }

    // MARK: - QA capture

    public IReadOnlyList<string> CaptureViews { get; } = new[]
    {
        "queries", "queries-expanded", "queries-detail", "mine", "mine-graphs", "mine-tile", "recent",
        "board", "board-detail", "board-priority", "board-assigned", "board-github", "queries-graphs",
    };

    public async Task ShowCaptureViewAsync(string view)
    {
        WidgetStrip.SetShown(Tab, view.EndsWith("-graphs", StringComparison.Ordinal) || view == "mine-tile");
        if (view != "mine-tile") ProjectsTileFilter.Shared.Clear();
        CloseDetail();
        ViewModel.GroupBy = "State";
        ViewModel.BoardSource = "Azure DevOps";
        switch (view.Split('-')[0])
        {
            case "mine": SelectView(ProjectsView.Mine); break;
            case "recent": SelectView(ProjectsView.Recent); break;
            case "board": SelectView(ProjectsView.Board); break;
            default: SelectView(ProjectsView.Queries); break;
        }
        await ApplyViewAsync();
        switch (view)
        {
            case "queries-expanded":
                Queries.ExpandAll();
                break;
            case "queries-detail":
                Queries.ExpandAll();
                if (_services.QueryRuns.FirstOrDefault()?.Rows.FirstOrDefault() is { } row) ShowTask(row.Task);
                break;
            case "mine-tile":
                ProjectsTileFilter.Shared.Select(ProjectsTile.InProgress);
                break;
            case "board-detail":
                if (ViewModel.Columns.FirstOrDefault(c => c.Cards.Count > 0)?.Cards[0] is { } card) ShowTask(card.Task);
                break;
            case "board-priority":
                ViewModel.GroupBy = "Priority";
                break;
            case "board-assigned":
                ViewModel.GroupBy = "Assigned To";
                break;
            case "board-github":
                ViewModel.BoardSource = "GitHub Project";
                await ApplyViewAsync();
                break;
        }
        RenderGraphs();
    }
}
