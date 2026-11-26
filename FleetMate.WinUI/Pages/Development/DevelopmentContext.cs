using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Terminal;

namespace FleetMate.WinUI.Pages.Development;

/// <summary>
/// The Terminal hook point: what Development shows, written to the
/// FLEETMATE_CONTEXT file every terminal session reads, as FleetMate.GUI's
/// <c>ContextPublisher</c> does, in the shape <see cref="AppContextSelections"/>
/// gives both apps. It writes to <c>App.Services.AgentContext</c>, which is
/// null in demo mode, where these calls do nothing.
/// </summary>
public static class DevelopmentContext
{
    private static AppContextFile? File => App.Services?.AgentContext;

    /// <summary>The open sub-tab: Repos, Pulls, Commits, Pipelines or Skills.</summary>
    public static void Segment(string? segment) => File?.SetSegment("Development", segment);

    public static void PullRequest(UnifiedPullRequest? pr) => File?.SetSelection("pullRequest", AppContextSelections.PullRequest(pr));

    /// <summary>Any other selection (a commit, a run, a repository), by kind.</summary>
    public static void Select(string kind, IReadOnlyList<ContextSelection> items) => File?.SetSelection(kind, items);
}
