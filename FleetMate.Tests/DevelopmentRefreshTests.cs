using System.Text;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Xunit;

namespace FleetMate.Tests;

public sealed class DevelopmentRefreshTests
{
    [Theory]
    [InlineData(true, true, 0, 15)]
    [InlineData(false, true, 0, 60)]
    [InlineData(true, false, 0, 60)]
    [InlineData(true, true, 1, 30)]
    [InlineData(true, true, 3, 120)]
    [InlineData(false, false, 5, 300)]
    [InlineData(false, false, 20, 300)]
    public void CadenceStaysTargetedAndFailureBackoffIsBounded(bool visible, bool running, int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), DevelopmentRefreshCoordinator.PipelineDelay(visible, running, failures));

    [Fact]
    public async Task RequestsCoalesceAndNeverOverlapWhileHistoryUpgradeWaitsForTheCurrentCycle()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<DevelopmentRefreshRequest>();
        var concurrent = 0;
        var maximum = 0;
        await using var coordinator = new DevelopmentRefreshCoordinator(async (request, ct) =>
        {
            var active = Interlocked.Increment(ref concurrent);
            maximum = Math.Max(maximum, active);
            lock (calls) calls.Add(request);
            try
            {
                if (calls.Count == 1) { started.TrySetResult(); await release.Task.WaitAsync(ct); }
                return true;
            }
            finally { Interlocked.Decrement(ref concurrent); }
        }, () => true);
        coordinator.SetActivity(true, true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var first = coordinator.RequestAsync(DevelopmentRefreshArea.Pipelines);
        Assert.Same(first, coordinator.RequestAsync(DevelopmentRefreshArea.Pipelines));
        var history = coordinator.RequestAsync(DevelopmentRefreshArea.Pipelines, history: true);
        Assert.Same(history, coordinator.RequestAsync(DevelopmentRefreshArea.Pipelines, history: true));
        release.TrySetResult();
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(await history.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, maximum);
        Assert.Equal(2, calls.Count);
        Assert.False(calls[0].History);
        Assert.True(calls[1].History);
    }

    [Fact]
    public async Task InactiveSessionCancelsWorkAndQueuedHistoryWithoutStartingAnotherTask()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var coordinator = new DevelopmentRefreshCoordinator(async (_, ct) =>
        {
            Interlocked.Increment(ref count);
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); return true; }
            finally { cancelled.TrySetResult(); }
        }, () => true);
        coordinator.SetActivity(true, true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var pending = coordinator.RequestAsync(DevelopmentRefreshArea.Commits, history: true);
        coordinator.SetActivity(false, false);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, Volatile.Read(ref count));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RequestAsync(DevelopmentRefreshArea.Pipelines));
    }

    [Fact]
    public async Task LeavingAHistoryViewCancelsItsFanoutButKeepsPipelineRefreshAvailable()
    {
        var historyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new DevelopmentRefreshCoordinator(async (request, ct) =>
        {
            if (request.Area == DevelopmentRefreshArea.Commits)
            {
                historyStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return true;
        }, () => false);
        coordinator.SetActivity(true, false);
        coordinator.SetVisibleArea(DevelopmentRefreshArea.Commits);
        var history = coordinator.RequestAsync(DevelopmentRefreshArea.Commits, history: true);
        await historyStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        coordinator.SetVisibleArea(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => history);
        Assert.True(await coordinator.RequestAsync(DevelopmentRefreshArea.Pipelines).WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private static PipelineRun Run(PullRequestSource source, long id, string container, PipelineRunStatus status = PipelineRunStatus.Running, DateTime? date = null) => new()
    {
        Source = source, RunId = id, Container = container, Repository = "example", PipelineName = "Example workflow",
        Status = status, StartedAt = date ?? new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void BusySelectedRunsCannotStarveRotatingGithubRepositoryDiscovery()
    {
        var plan = new PipelineRefreshPlan();
        var runs = Enumerable.Range(1, 9).Select(id => Run(PullRequestSource.GitHub, id, "owner")).ToList();
        var scopes = Enumerable.Range(0, 12).Select(id => new PipelineRepositoryScope("owner", "repo-" + id)).ToList();
        var repositories = new HashSet<string>();
        var activeIds = new HashSet<long>();
        for (var tick = 0; tick < 30; tick++)
        {
            var targets = plan.GitHub(runs, scopes, runs[0], null);
            Assert.InRange(targets.Count, 1, 4);
            Assert.Equal(1L, targets[0].RunId);
            foreach (var target in targets)
                if (target.RunId is { } id) activeIds.Add(id); else repositories.Add(target.Repository!);
        }
        Assert.Equal(scopes.Count, repositories.Count);
        Assert.Equal(runs.Count, activeIds.Count);
    }

    [Fact]
    public void AzureKnownIdsBatchByProjectAndQuietProjectsStillGetDiscovered()
    {
        var plan = new PipelineRefreshPlan();
        var runs = Enumerable.Range(0, 8).Select(id => Run(PullRequestSource.AzureDevOps, id + 1, "Project-" + id)).ToList();
        var projects = Enumerable.Range(0, 12).Select(id => "Project-" + id).ToList();
        var seen = new HashSet<string>();
        for (var tick = 0; tick < 30; tick++)
        {
            var targets = plan.AzureDevOps(runs, projects, runs[0], null);
            Assert.InRange(targets.Count, 1, 3);
            Assert.Contains(targets, target => target.Container == "Project-0" && target.BuildIds?.Contains(1) == true);
            foreach (var target in targets.Where(target => target.BuildIds == null)) seen.Add(target.Container);
        }
        Assert.Equal(projects.Count, seen.Count);
    }

    [Fact]
    public void AzureSummaryIncludesQueuedRunsAndKnownIdsAreNotRestrictedToTheHistoryWindow()
    {
        var path = AzureDevOpsService.BuildPipelineSummaryPath("Example Project", DateTime.UtcNow, new long[] { 12, 24, 12 });
        Assert.Contains("Example%20Project/", path);
        Assert.Contains("statusFilter=all", path);
        Assert.Contains("queryOrder=queueTimeDescending", path);
        Assert.Contains("buildIds=12,24", path);
        Assert.DoesNotContain("minTime", path);
        Assert.Throws<ArgumentException>(() => AzureDevOpsService.BuildPipelineSummaryPath("example", DateTime.UtcNow, new long[] { 0 }));
        Assert.Throws<ArgumentException>(() => AzureDevOpsService.BuildPipelineSummaryPath("example", DateTime.UtcNow, Enumerable.Range(1, 51).Select(id => (long)id).ToArray()));
    }

    [Fact]
    public void SingleGithubRunParsesTheActualByteResponseObject()
    {
        var raw = Encoding.UTF8.GetBytes("""{"id":42,"status":"completed","conclusion":"success","name":"Example","created_at":"2026-11-09T12:00:00Z"}""");
        var run = GitHubActionsService.ParseRunResponse(raw, "example", "repository");
        Assert.Equal(42, run.RunId);
        Assert.Equal(PipelineRunStatus.Succeeded, run.Status);
    }

    [Fact]
    public void SuccessfulScopesMergeWithoutRemovingFailedProviderRowsAndHistoryStaysBounded()
    {
        var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        var failedProvider = Run(PullRequestSource.AzureDevOps, 999, "Example project");
        var oldGithub = Run(PullRequestSource.GitHub, 1, "example");
        var updated = Run(PullRequestSource.GitHub, 1, "example", PipelineRunStatus.Succeeded);
        var merged = PipelineRunCache.Merge(new[] { failedProvider, oldGithub }, new[] { updated }, now);
        Assert.Same(failedProvider, merged.Single(run => run.Source == PullRequestSource.AzureDevOps));
        Assert.Equal(PipelineRunStatus.Succeeded, merged.Single(run => run.Source == PullRequestSource.GitHub).Status);
        Assert.True(PipelineRunCache.SameContent(merged, merged.ToList()));
        Assert.False(PipelineRunCache.SameContent(new[] { oldGithub }, new[] { updated }));
        var selected = Run(PullRequestSource.GitHub, 10000, "example", PipelineRunStatus.Succeeded, now.AddDays(-20));
        var oldActive = Run(PullRequestSource.GitHub, 10001, "example", date: now.AddDays(-20));
        var history = Enumerable.Range(1, 700).Select(id => Run(PullRequestSource.GitHub, id, "example", PipelineRunStatus.Succeeded, now.AddMinutes(-id)));
        var bounded = PipelineRunCache.Merge(history, new[] { selected, oldActive }, now, selected);
        Assert.Equal(PipelineRunCache.MaximumRuns, bounded.Count);
        Assert.Contains(selected, bounded);
        Assert.Contains(oldActive, bounded);
    }
}
