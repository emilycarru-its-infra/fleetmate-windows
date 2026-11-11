using System.Text.Json;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.GUI.Views.Development;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Pipeline approvals: parsing the Approvals API and the build timeline,
/// pinning each approval to its run, and the unchanged-status refusal.
/// </summary>
public class PipelineApprovalTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private const string EnvironmentId = "11111111-aaaa-4bbb-8ccc-000000000001";
    private const string ManualId = "22222222-aaaa-4bbb-8ccc-000000000002";

    private const string PendingJson = """
        {"count":2,"value":[
          {"id":"11111111-aaaa-4bbb-8ccc-000000000001","status":"pending","instructions":"Check the staging site first.",
           "createdOn":"2026-11-03T14:05:00Z",
           "pipeline":{"owner":{"id":101,"name":"20261103.1"}},
           "steps":[{"assignedApprover":{"displayName":"Release approvers"}},{"assignedApprover":{"displayName":"Example Person"}}]},
          {"id":"22222222-AAAA-4bbb-8ccc-000000000002","status":"pending","instructions":"",
           "createdOn":"2026-11-03T15:00:00Z","steps":[]},
          {"status":"pending"}
        ]}
        """;

    private static PipelineRun Run(string project, long id, PipelineRunStatus status = PipelineRunStatus.Running) => new()
    {
        Source = PullRequestSource.AzureDevOps,
        Container = project,
        PipelineName = "deploy",
        RunId = id,
        RunNumber = id.ToString(),
        Status = status,
    };

    [Fact]
    public void PendingApprovalsCarryTheirBuildWhenTheApiNamesOne()
    {
        var approvals = AzureDevOpsService.ParsePendingApprovals(Json(PendingJson), "Example");

        Assert.Equal(2, approvals.Count);
        var environment = approvals[0];
        Assert.Equal(EnvironmentId, environment.Id);
        Assert.Equal("Example", environment.Project);
        Assert.Equal(101, environment.BuildId);
        Assert.Equal("Check the staging site first.", environment.Instructions);
        Assert.Equal(new[] { "Release approvers", "Example Person" }, environment.Approvers);
        Assert.Equal(new DateTime(2026, 11, 3, 14, 5, 0, DateTimeKind.Utc), environment.CreatedOn?.ToUniversalTime());

        Assert.Null(approvals[1].BuildId);
        Assert.Empty(approvals[1].Approvers);
    }

    [Fact]
    public void OpenTimelineIdentifiersSkipCompletedRecordsAndAreLowercased()
    {
        var open = AzureDevOpsService.ParseOpenTimelineIdentifiers(Json("""
            {"records":[
              {"type":"Task","state":"inProgress","identifier":"22222222-AAAA-4bbb-8ccc-000000000002"},
              {"type":"Task","state":"completed","identifier":"33333333-aaaa-4bbb-8ccc-000000000003"},
              {"type":"Job","state":"pending"},
              {"type":"Stage","state":"pending","identifier":"Deploy"}
            ]}
            """));

        Assert.Equal(new[] { "22222222-aaaa-4bbb-8ccc-000000000002", "deploy" }.OrderBy(x => x), open.OrderBy(x => x));
    }

    [Theory]
    [InlineData("approved", true, false)]
    [InlineData("rejected", false, false)]
    [InlineData("pending", true, true)]
    [InlineData("pending", false, true)]
    [InlineData("rejected", true, true)]
    public void AnUnchangedStatusMeansYouAreNotAnApprover(string returned, bool approve, bool refused)
    {
        var json = Json($$"""{"value":[{"id":"{{EnvironmentId.ToUpperInvariant()}}","status":"{{returned}}"}]}""");

        var error = AzureDevOpsService.ApprovalResponseError(json, EnvironmentId, approve);

        if (refused)
        {
            Assert.NotNull(error);
            Assert.Contains("not be one of its approvers", error);
        }
        else Assert.Null(error);
    }

    [Fact]
    public void ResponseBodyIsOneApprovalWithStatusAndComment()
    {
        var body = Json(AzureDevOpsService.ApprovalResponseBody(ManualId, approve: false, comment: "Not today"));

        var item = Assert.Single(body.EnumerateArray());
        Assert.Equal(ManualId, item.GetProperty("approvalId").GetString());
        Assert.Equal("rejected", item.GetProperty("status").GetString());
        Assert.Equal("Not today", item.GetProperty("comment").GetString());
    }

    private sealed class FakeSource : IPipelineApprovalSource
    {
        public Dictionary<string, List<PipelineApproval>> Pending { get; } = new();
        public Dictionary<long, HashSet<string>> Timelines { get; } = new();
        public HashSet<string> Failing { get; } = new();
        public List<string> PendingReads { get; } = new();
        public List<long> TimelineReads { get; } = new();

        public Task<List<PipelineApproval>> GetPendingApprovalsAsync(string project)
        {
            PendingReads.Add(project);
            if (Failing.Contains(project)) throw new HttpRequestException("unavailable");
            return Task.FromResult(Pending.GetValueOrDefault(project, new()).ToList());
        }

        public Task<HashSet<string>> GetOpenTimelineIdentifiersAsync(string project, long buildId)
        {
            TimelineReads.Add(buildId);
            return Task.FromResult(Timelines.GetValueOrDefault(buildId, new HashSet<string>()));
        }
    }

    private static FakeSource SourceWithBoth()
    {
        var source = new FakeSource();
        source.Pending["Example"] = AzureDevOpsService.ParsePendingApprovals(Json(PendingJson), "Example");
        source.Timelines[102] = new HashSet<string> { ManualId };
        return source;
    }

    [Fact]
    public async Task EnvironmentApprovalsPinByOwnerAndManualValidationByTimeline()
    {
        var tracker = new PipelineApprovalTracker();
        var source = SourceWithBoth();
        var runs = new[] { Run("Example", 101), Run("Example", 102), Run("Example", 103) };

        Assert.True(await tracker.RefreshAsync(runs, source));

        Assert.Equal(EnvironmentId, Assert.Single(tracker.For(runs[0])).Id);
        var manual = Assert.Single(tracker.For(runs[1]));
        Assert.Equal(102, manual.BuildId);
        Assert.False(tracker.IsAwaiting(runs[2]));
    }

    [Fact]
    public async Task TheTimelineMatchIsCached()
    {
        var tracker = new PipelineApprovalTracker();
        var source = SourceWithBoth();
        var runs = new[] { Run("Example", 101), Run("Example", 102) };

        await tracker.RefreshAsync(runs, source);
        var reads = source.TimelineReads.Count;
        source.Timelines.Clear();
        await tracker.RefreshAsync(runs, source);

        Assert.Equal(reads, source.TimelineReads.Count);
        Assert.True(tracker.IsAwaiting(runs[1]));
    }

    [Fact]
    public async Task OnlyProjectsWithActiveAzureRunsAreRead()
    {
        var tracker = new PipelineApprovalTracker();
        var source = SourceWithBoth();
        var runs = new[]
        {
            Run("Example", 101),
            Run("Finished", 5, PipelineRunStatus.Succeeded),
            new PipelineRun { Source = PullRequestSource.GitHub, Container = "someone", RunId = 9, Status = PipelineRunStatus.Running },
        };

        await tracker.RefreshAsync(runs, source);

        Assert.Equal(new[] { "Example" }, source.PendingReads);
    }

    [Fact]
    public async Task AFailedReadKeepsWhatWasKnown()
    {
        var tracker = new PipelineApprovalTracker();
        var source = SourceWithBoth();
        var runs = new[] { Run("Example", 101) };
        await tracker.RefreshAsync(runs, source);

        source.Failing.Add("Example");
        Assert.False(await tracker.RefreshAsync(runs, source));

        Assert.True(tracker.IsAwaiting(runs[0]));
    }

    [Fact]
    public async Task AFinishedRunLosesItsApproval()
    {
        var tracker = new PipelineApprovalTracker();
        var source = SourceWithBoth();
        await tracker.RefreshAsync(new[] { Run("Example", 101) }, source);

        var finished = Run("Example", 101, PipelineRunStatus.Succeeded);
        Assert.False(tracker.IsAwaiting(finished));
        source.Pending.Clear();
        await tracker.RefreshAsync(new[] { finished }, source);
        Assert.False(tracker.IsAwaiting(Run("Example", 101)));
    }

    [Fact]
    public async Task RemovingAnAnsweredApprovalClearsTheRunAndRaisesChanged()
    {
        var tracker = new PipelineApprovalTracker();
        var run = Run("Example", 101);
        await tracker.RefreshAsync(new[] { run }, SourceWithBoth());
        var raised = 0;
        tracker.Changed += (_, _) => raised++;

        tracker.Remove(Assert.Single(tracker.For(run)));

        Assert.False(tracker.IsAwaiting(run));
        Assert.Equal(1, raised);
    }

    [Theory]
    [InlineData(1, "1 approval needs your review before this run can continue")]
    [InlineData(2, "2 approvals need your review before this run can continue")]
    public void BannerCountsApprovals(int count, string text) =>
        Assert.Equal(text, PipelineApprovalTracker.BannerText(count));

    [Fact]
    public void AWaitingRowSaysSoInsteadOfItsStatus()
    {
        var row = new PipelineRunRowViewModel { Run = Run("Example", 101), AwaitingApproval = true };

        Assert.Equal("Waiting for approval", row.StatusLabel);
        Assert.Equal(System.Windows.Visibility.Visible, row.ApprovalVisibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, new PipelineRunRowViewModel { Run = Run("Example", 101) }.ApprovalVisibility);
    }

    [Fact]
    public void ReviewMetaNamesApproversAndWhenItStartedWaiting()
    {
        var approval = new PipelineApproval
        {
            Id = EnvironmentId,
            CreatedOn = new DateTime(2026, 11, 3, 14, 5, 0, DateTimeKind.Local),
            Approvers = new[] { "Release approvers" },
        };

        Assert.Equal("Waiting since 3 Nov, 14:05 · Approvers: Release approvers", ApprovalReviewText.Meta(approval));
        Assert.Equal("", ApprovalReviewText.Meta(new PipelineApproval { Id = ManualId }));
    }
}
