using FleetMate.Core.Links;
using FleetMate.GUI;
using FleetMate.GUI.Views.Development;
using FleetMate.GUI.Views.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// A link that launches the app arrives before sign-in finishes. These cover
/// what waits for sign-in, what the empty list says meanwhile, and the inbox
/// helper that keeps the saved session fresh by always running.
/// </summary>
public class ColdLaunchLinkTests
{
    // ── Tickets: why the list is empty ──────────────────────────────────

    [Fact]
    public void AnEmptyListBeforeSignInSaysNotSignedIn()
    {
        Assert.Equal(TicketsEmptyReason.NotSignedIn, TicketsPageSync.EmptyReason(
            loadingShown: false, ticketCount: 0, listedCount: 0,
            ssoEnabled: true, signedIn: false, loadError: null, searching: false, assignedToMe: false));
    }

    [Fact]
    public void TheLoadingProgressWinsOverAnyReason()
    {
        Assert.Equal(TicketsEmptyReason.None, TicketsPageSync.EmptyReason(
            loadingShown: true, ticketCount: 0, listedCount: 0,
            ssoEnabled: true, signedIn: false, loadError: "boom", searching: true, assignedToMe: true));
    }

    [Fact]
    public void ASignedInFailedLoadSaysItDidNotLoad()
    {
        Assert.Equal(TicketsEmptyReason.LoadFailed, TicketsPageSync.EmptyReason(
            loadingShown: false, ticketCount: 0, listedCount: 0,
            ssoEnabled: true, signedIn: true, loadError: "timed out", searching: false, assignedToMe: false));
        Assert.Equal(TicketsEmptyReason.NoTickets, TicketsPageSync.EmptyReason(
            loadingShown: false, ticketCount: 0, listedCount: 0,
            ssoEnabled: true, signedIn: true, loadError: null, searching: false, assignedToMe: false));
    }

    [Theory]
    [InlineData(true, true, "Search")]
    [InlineData(false, true, "AssignedToMe")]
    [InlineData(false, false, "Filters")]
    public void LoadedTicketsHiddenByTheViewSayWhich(bool searching, bool assignedToMe, string expected)
    {
        Assert.Equal(Enum.Parse<TicketsEmptyReason>(expected), TicketsPageSync.EmptyReason(
            loadingShown: false, ticketCount: 12, listedCount: 0,
            ssoEnabled: true, signedIn: true, loadError: null, searching: searching, assignedToMe: assignedToMe));
    }

    [Fact]
    public void AListWithTicketsHasNoEmptyState()
    {
        Assert.Equal(TicketsEmptyReason.None, TicketsPageSync.EmptyReason(
            loadingShown: false, ticketCount: 12, listedCount: 3,
            ssoEnabled: true, signedIn: false, loadError: null, searching: false, assignedToMe: false));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void ALinkedTicketWaitsOnlyForAPendingSignIn(bool ssoEnabled, bool signedIn, bool expected)
    {
        Assert.Equal(expected, TicketsPageSync.HoldLinkForSignIn(ssoEnabled, signedIn));
    }

    // ── Development: links held for Azure DevOps sign-in ────────────────

    private static readonly FleetMateLink AdoPull = new FleetMateLink.PullRequest(new FleetMateLink.AzureDevOps("Proj", "repo"), 7);
    private static readonly FleetMateLink GitHubPull = new FleetMateLink.PullRequest(new FleetMateLink.GitHub("owner", "repo"), 7);

    [Fact]
    public void AnAzureDevOpsLinkWaitsForSignIn()
    {
        Assert.True(DevelopmentLinkHold.ShouldHold(AdoPull, devOpsConfigured: true, devOpsSignedIn: false));
        Assert.True(DevelopmentLinkHold.ShouldHold(new FleetMateLink.Commit(new FleetMateLink.AzureDevOps("Proj", "repo"), "abc123"), true, false));
        Assert.True(DevelopmentLinkHold.ShouldHold(new FleetMateLink.AzureDevOpsRun("Proj", 5), true, false));
        Assert.True(DevelopmentLinkHold.ShouldHold(new FleetMateLink.AzureDevOpsPipeline("Proj", 5), true, false));
    }

    [Fact]
    public void NothingWaitsOnceSignedInOrWithoutAzureDevOps()
    {
        Assert.False(DevelopmentLinkHold.ShouldHold(AdoPull, devOpsConfigured: true, devOpsSignedIn: true));
        Assert.False(DevelopmentLinkHold.ShouldHold(AdoPull, devOpsConfigured: false, devOpsSignedIn: false));
        Assert.False(DevelopmentLinkHold.ShouldHold(GitHubPull, devOpsConfigured: true, devOpsSignedIn: false));
        Assert.False(DevelopmentLinkHold.ShouldHold(new FleetMateLink.GitHubRun("owner", "repo", 1), true, false));
    }

    // ── The inbox helper starts again after any exit ────────────────────

    [Theory]
    [InlineData(new[] { "--inbox-agent" }, true)]
    [InlineData(new[] { "--INBOX-AGENT" }, true)]
    [InlineData(new[] { "--inbox-agent-worker" }, false)]
    [InlineData(new[] { "--inbox-agent", "--inbox-agent-worker" }, false)]
    [InlineData(new string[0], false)]
    public void TheSupervisorRunsOnlyWithTheRunEntrySwitch(string[] args, bool expected)
    {
        Assert.Equal(expected, InboxAgentSupervisor.IsRequested(args));
    }

    [Fact]
    public void RestartsAreAtLeastAMinuteApart()
    {
        Assert.Equal(TimeSpan.FromSeconds(50), InboxAgentSupervisor.RestartDelay(TimeSpan.FromSeconds(10), 1));
        Assert.Equal(TimeSpan.Zero, InboxAgentSupervisor.RestartDelay(TimeSpan.FromHours(3), 0));
        Assert.Equal(InboxAgentSupervisor.AnotherRunningWait,
            InboxAgentSupervisor.RestartDelay(TimeSpan.FromSeconds(1), InboxAgent.AnotherRunningExitCode));
    }

    [Fact]
    public async Task TheHelperIsStartedAgainAfterEveryExit()
    {
        using var cts = new CancellationTokenSource();
        var codes = new Queue<int>(new[] { 0, 1, -1 });
        var starts = 0;
        var waits = new List<TimeSpan>();
        var clock = DateTime.UtcNow;

        var supervisor = new InboxAgentSupervisor(
            _ =>
            {
                starts++;
                if (starts == 2) throw new InvalidOperationException("crashed on start");
                if (codes.Count == 0) { cts.Cancel(); throw new OperationCanceledException(cts.Token); }
                return Task.FromResult(codes.Dequeue());
            },
            (wait, _) => { waits.Add(wait); clock += wait; return Task.CompletedTask; },
            () => clock);

        await supervisor.RunAsync(cts.Token);

        Assert.Equal(5, starts);
        Assert.Equal(4, waits.Count);
        Assert.All(waits, w => Assert.Equal(InboxAgentSupervisor.Throttle, w));
    }
}
