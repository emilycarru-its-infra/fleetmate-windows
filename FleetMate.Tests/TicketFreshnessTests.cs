using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using FleetMate.GUI;
using Xunit;

namespace FleetMate.Tests;

public sealed class TicketFreshnessTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ticket-freshness-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public void SnapshotScopesDoNotGuessLegacyIdentityOrMixServicePersonAndRange()
    {
        var legacy = new TicketSnapshot(_directory);
        legacy.Save(new[] { new TdxTicket { Id = 1, Description = "LEGACY_PRIVATE_BODY" } });
        var person = Guid.NewGuid();
        const string service = "https://tickets.example.invalid/tdwebapi/api/1/tickets";
        var scoped = TicketSnapshot.ForScope(service, person, TicketDateRangePreset.CurrentTerm, _directory);
        Assert.Empty(scoped.Load());
        Assert.Contains("LEGACY_PRIVATE_BODY", File.ReadAllText(legacy.FilePath));
        scoped.Save(new[] { new TdxTicket { Id = 2 } });
        Assert.Equal(2, Assert.Single(scoped.Load()).Id);
        Assert.Empty(TicketSnapshot.ForScope(service, Guid.NewGuid(), TicketDateRangePreset.CurrentTerm, _directory).Load());
        Assert.Empty(TicketSnapshot.ForScope("https://other.example.invalid/tdwebapi/api/1/tickets", person, TicketDateRangePreset.CurrentTerm, _directory).Load());
        Assert.Empty(TicketSnapshot.ForScope(service, person, Enum.GetValues<TicketDateRangePreset>().First(p => p != TicketDateRangePreset.CurrentTerm), _directory).Load());
        File.SetLastWriteTimeUtc(scoped.FilePath, DateTime.UtcNow.AddMinutes(-20));
        Assert.Empty(scoped.Load(TimeSpan.FromMinutes(15)));
        Assert.Single(scoped.Load());
    }

    [Fact]
    public async Task CoalescedBoardResultsCarryTheSameGenerationAndLaterFetchesAdvanceIt()
    {
        var answer = new TaskCompletionSource<List<TdxTicket>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var loader = new TicketBoardLoader(() => Task.CompletedTask, _ => answer.Task);
        var first = loader.LoadRequest(TicketDateRangePreset.CurrentTerm);
        var joined = loader.LoadRequest(TicketDateRangePreset.CurrentTerm);
        Assert.Equal(first.Generation, joined.Generation);
        Assert.Same(first.Task, joined.Task);
        answer.SetResult(new());
        await first.Task;
        var later = loader.LoadRequest(TicketDateRangePreset.CurrentTerm);
        Assert.True(later.Generation > first.Generation);
    }

    [Fact]
    public async Task OneCancelledBoardCallerDoesNotCancelTheOtherCaller()
    {
        var answer = new TaskCompletionSource<List<TdxTicket>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken shared = default;
        using var loader = new TicketBoardLoader(() => Task.CompletedTask, (_, ct) => { shared = ct; return answer.Task; });
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var first = loader.LoadAsync(TicketDateRangePreset.CurrentTerm, firstCancellation.Token);
        var second = loader.LoadAsync(TicketDateRangePreset.CurrentTerm, secondCancellation.Token);
        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(shared.IsCancellationRequested);
        answer.SetResult(new() { new() { Id = 42 } });
        Assert.Equal(42, Assert.Single(await second).Id);
    }

    [Fact]
    public async Task LeavingTheLastBoardCallerCancelsItsUnderlyingRequest()
    {
        CancellationToken shared = default;
        using var loader = new TicketBoardLoader(() => Task.CompletedTask, async (_, ct) =>
        {
            shared = ct;
            await Task.Delay(Timeout.Infinite, ct);
            return new List<TdxTicket>();
        });
        using var cancellation = new CancellationTokenSource();
        var load = loader.LoadAsync(TicketDateRangePreset.CurrentTerm, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.True(shared.IsCancellationRequested);
    }

    [Fact]
    public async Task ReactivationWaitsForThePreviousCancelledTickToDrain()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0; var active = 0; var maximum = 0;
        using var loop = new ActiveTicketRefreshLoop(async (_, ct) =>
        {
            var count = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, count);
            try
            {
                if (Interlocked.Increment(ref calls) == 1) { firstStarted.SetResult(); await firstReleased.Task; }
                else { secondStarted.SetResult(); await Task.Delay(Timeout.Infinite, ct); }
            }
            finally { Interlocked.Decrement(ref active); }
        });
        loop.SetActive(true);
        await firstStarted.Task;
        loop.SetActive(false); loop.SetActive(true);
        await Task.Delay(30);
        Assert.Equal(1, calls);
        firstReleased.SetResult();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        loop.Dispose();
        await loop.Completion;
        Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task UnexpectedRefreshFailureBacksOffWithoutAnotherTimer()
    {
        var observed = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var loop = new ActiveTicketRefreshLoop((_, _) => Task.FromException(new IOException("synthetic")),
            async (delay, ct) => { observed.SetResult(delay); await Task.Delay(Timeout.Infinite, ct); });
        loop.SetActive(true);
        Assert.Equal(TimeSpan.FromMinutes(5), await observed.Task);
        loop.Dispose(); await loop.Completion;
    }

    [Fact]
    public void FeedRefreshKeepsKnownRepliesPendingNewBodiesAndClearsDeletedReplies()
    {
        var old = new[] { new TdxFeedEntry { Id = 1, Body = "old", Replies = new() { new() { Id = 11 } } } };
        var fresh = new[] { new TdxFeedEntry { Id = 1, Body = "fresh", RepliesCount = 2 } };
        var merged = Assert.Single(TicketFeedRefresh.Merge(fresh, old, new Dictionary<int, List<TdxFeedEntry>>()));
        Assert.Equal("fresh", merged.Body);
        Assert.Equal(11, Assert.Single(merged.ReplyList).Id);
        fresh[0].RepliesCount = 0;
        Assert.Empty(Assert.Single(TicketFeedRefresh.Merge(fresh, old, new Dictionary<int, List<TdxFeedEntry>>())).ReplyList);
    }

    [Theory]
    [InlineData("fleetmate-inbox.exe", true)]
    [InlineData("TicketsMate-inbox.exe", true)]
    [InlineData("fleetmate-gui.exe", false)]
    [InlineData("TicketsMate.exe", false)]
    public void DedicatedAliasesAlwaysStayInSupervisorMode(string executable, bool expected)
    {
        Assert.Equal(expected, InboxAgentSupervisor.IsRequested(Array.Empty<string>(), executable));
        Assert.False(InboxAgentSupervisor.IsRequested(new[] { InboxAgent.Argument }, executable));
    }
}
