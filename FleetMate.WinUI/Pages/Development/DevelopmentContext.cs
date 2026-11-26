using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Terminal;

namespace FleetMate.WinUI.Pages.Development;

/// <summary>
/// The Terminal hook point: what Development shows, written to the
/// FLEETMATE_CONTEXT file every terminal session reads, as FleetMate.GUI's
/// <c>ContextPublisher</c> does. It writes to <c>App.Services.AgentContext</c>,
/// which stays null until the Terminal tab is ported and sets it; until then
/// these calls do nothing.
/// </summary>
public static class DevelopmentContext
{
    private static AppContextFile? File => App.Services?.AgentContext;

    /// <summary>The open sub-tab: Repos, Pulls, Commits, Pipelines or Skills.</summary>
    public static void Segment(string? segment) => File?.SetSegment("Development", segment);

    public static void PullRequest(UnifiedPullRequest? pr) =>
        File?.SetSelection("pullRequest", pr == null ? Array.Empty<ContextSelection>() : new[]
        {
            new ContextSelection("pullRequest", $"{pr.Repository}#{pr.Number}", new Dictionary<string, string?>
            {
                ["title"] = pr.Title,
                ["repository"] = string.IsNullOrEmpty(pr.Container) ? pr.Repository : $"{pr.Container}/{pr.Repository}",
                ["sourceBranch"] = pr.SourceBranch,
                ["targetBranch"] = pr.TargetBranch,
                ["author"] = pr.AuthorName,
                ["url"] = pr.WebUrl,
                ["source"] = pr.Source.ToString(),
            })
        });

    /// <summary>Any other selection (a commit, a run, a repository), by kind.</summary>
    public static void Select(string kind, IReadOnlyList<ContextSelection> items) => File?.SetSelection(kind, items);
}
