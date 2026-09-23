using System.Text.Json;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.GUI.Views.Development;
using Xunit;

namespace FleetMate.Tests;

/// <summary>Development › Commits and Pipelines: parsing for both providers, status mapping, filters and log sections.</summary>
public class CommitsAndPipelinesTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // MARK: - Commits

    [Fact]
    public void GitHubCommits_ParseReposAndSkipEmptyHistory()
    {
        var section = Json("""
            { "nodes": [
              { "name": "fleet", "url": "https://github.com/acme/fleet", "owner": { "login": "acme" },
                "defaultBranchRef": { "name": "main", "target": { "history": { "nodes": [
                  { "oid": "abc123456789", "message": "Add widget\n\nBody", "committedDate": "2026-09-22T10:00:00Z",
                    "url": "https://github.com/acme/fleet/commit/abc123456789", "author": { "name": "Ada L", "user": { "login": "ada" } } },
                  { "oid": "def987654321", "message": "Fix", "committedDate": "2026-09-21T10:00:00Z",
                    "url": "u", "author": { "name": "Bob", "user": null } } ] } } } },
              { "name": "quiet", "url": "https://github.com/acme/quiet", "owner": { "login": "acme" },
                "defaultBranchRef": { "name": "main", "target": { "history": { "nodes": [] } } } }
            ] }
            """);

        var repos = GitHubPullRequestService.ParseRepositoryCommits(section);

        var repo = Assert.Single(repos);
        Assert.Equal("acme/fleet", repo.DisplayName);
        Assert.Equal("main", repo.DefaultBranch);
        Assert.Equal("ada", repo.Commits[0].AuthorName);
        Assert.Equal("Bob", repo.Commits[1].AuthorName);
        Assert.Equal("Add widget", repo.Commits[0].Subject);
        Assert.Equal(new DateTime(2026, 9, 22, 10, 0, 0, DateTimeKind.Utc), repo.LatestDate);
    }

    [Fact]
    public void GitHubCommitDetail_ParsesFilesChangesAndStats()
    {
        var detail = GitHubPullRequestService.ParseCommitDetail(Json("""
            { "commit": { "message": "Add widget" }, "stats": { "additions": 3, "deletions": 1 },
              "files": [
                { "filename": "a.cs", "status": "modified", "patch": "@@ -1,2 +1,3 @@\n line\n-old\n+new\n+more" },
                { "filename": "logo.png", "status": "added" } ] }
            """));

        Assert.Equal("Add widget", detail.Message);
        Assert.Equal(2, detail.Files.Count);
        Assert.Equal(3, detail.Additions);
        Assert.Equal(new[] { "modified", "added" }, detail.Changes.Select(c => c.ChangeType));
        Assert.False(detail.Truncated);
    }

    [Fact]
    public void DevOpsCommits_NewestFirstWithLinks()
    {
        var commits = AzureDevOpsService.ParseCommitRefs(Json("""
            { "value": [
              { "commitId": "1111111111", "comment": "older", "author": { "name": "Ada", "date": "2026-09-20T10:00:00Z" } },
              { "commitId": "2222222222", "comment": "newer", "author": { "name": "Bob", "date": "2026-09-21T10:00:00Z" } } ] }
            """), "https://devops.example.com/acme/Platform/_git/fleet/commit/");

        Assert.Equal(new[] { "newer", "older" }, commits.Select(c => c.Message));
        Assert.Equal("https://devops.example.com/acme/Platform/_git/fleet/commit/2222222222", commits[0].Url);
    }

    [Fact]
    public void DevOpsCommitChanges_SkipFoldersAndTrimSlash()
    {
        var changes = AzureDevOpsService.ParseCommitChanges(Json("""
            { "changes": [
              { "item": { "path": "/src", "isFolder": true }, "changeType": "add" },
              { "item": { "path": "/src/a.cs" }, "changeType": "Edit" },
              { "item": { "path": "/b.md" }, "changeType": "delete" } ] }
            """));

        Assert.Equal(new[] { "src/a.cs", "b.md" }, changes.Select(c => c.Path));
        Assert.Equal(new[] { "edit", "delete" }, changes.Select(c => c.ChangeType));
    }

    // MARK: - Runs

    [Theory]
    [InlineData("queued", null, PipelineRunStatus.Queued)]
    [InlineData("in_progress", null, PipelineRunStatus.Running)]
    [InlineData("completed", "success", PipelineRunStatus.Succeeded)]
    [InlineData("completed", "failure", PipelineRunStatus.Failed)]
    [InlineData("completed", "cancelled", PipelineRunStatus.Cancelled)]
    [InlineData("completed", "skipped", PipelineRunStatus.Skipped)]
    public void Status_GitHub(string status, string? conclusion, PipelineRunStatus expected) =>
        Assert.Equal(expected, PipelineRunStatusExtensions.FromGitHub(status, conclusion));

    [Theory]
    [InlineData("notStarted", null, PipelineRunStatus.Queued)]
    [InlineData("inProgress", null, PipelineRunStatus.Running)]
    [InlineData("completed", "succeeded", PipelineRunStatus.Succeeded)]
    [InlineData("completed", "partiallySucceeded", PipelineRunStatus.Partial)]
    [InlineData("completed", "failed", PipelineRunStatus.Failed)]
    [InlineData("completed", "canceled", PipelineRunStatus.Cancelled)]
    [InlineData("completed", "skipped", PipelineRunStatus.Skipped)]
    public void Status_DevOps(string status, string? result, PipelineRunStatus expected) =>
        Assert.Equal(expected, PipelineRunStatusExtensions.FromAzureDevOps(status, result));

    [Fact]
    public void GitHubRuns_Parse()
    {
        var runs = GitHubActionsService.ParseRuns(Json("""
            { "workflow_runs": [
              { "id": 9001, "name": "CI", "workflow_id": 55, "run_number": 42, "status": "completed", "conclusion": "failure",
                "head_branch": "main", "head_sha": "abc", "run_started_at": "2026-09-22T10:00:00Z", "updated_at": "2026-09-22T10:05:00Z",
                "html_url": "https://github.com/acme/fleet/actions/runs/9001", "triggering_actor": { "login": "ada" } },
              { "id": 9002, "name": "CI", "workflow_id": 55, "run_number": 43, "status": "in_progress",
                "head_branch": "feat", "run_started_at": "2026-09-22T11:00:00Z", "updated_at": "2026-09-22T11:01:00Z",
                "actor": { "login": "bob" } } ] }
            """), "acme", "fleet");

        Assert.Equal(2, runs.Count);
        Assert.Equal(PipelineRunStatus.Failed, runs[0].Status);
        Assert.Equal("42", runs[0].RunNumber);
        Assert.Equal(55, runs[0].PipelineId);
        Assert.Equal("ada", runs[0].TriggeredBy);
        Assert.Equal(TimeSpan.FromMinutes(5), runs[0].Duration);
        Assert.Null(runs[1].FinishedAt);
        Assert.Equal("bob", runs[1].TriggeredBy);
    }

    [Fact]
    public void DevOpsBuilds_Parse()
    {
        var runs = AzureDevOpsService.ParseBuilds(Json("""
            { "value": [
              { "id": 777, "buildNumber": "20260922.3", "status": "completed", "result": "partiallySucceeded",
                "sourceBranch": "refs/heads/main", "sourceVersion": "abcdef", "startTime": "2026-09-22T10:00:00Z",
                "finishTime": "2026-09-22T10:02:30Z", "definition": { "id": 90, "name": "Build + Import" },
                "repository": { "name": "Fleet" }, "requestedFor": { "displayName": "Ada Lovelace" },
                "_links": { "web": { "href": "https://devops.example.com/acme/Platform/_build/results?buildId=777" } } } ] }
            """), "Platform", "https://devops.example.com/acme");

        var run = Assert.Single(runs);
        Assert.Equal("Build + Import", run.PipelineName);
        Assert.Equal(90, run.PipelineId);
        Assert.Equal("main", run.Branch);
        Assert.Equal(PipelineRunStatus.Partial, run.Status);
        Assert.Equal("Ada Lovelace", run.TriggeredBy);
        Assert.Equal("2m 30s", PipelineRunRowViewModel.FormatDuration(run.Duration));
    }

    [Fact]
    public void DevOpsTimeline_TasksOnlyInExecutionOrder()
    {
        var tasks = AzureDevOpsService.ParseTimelineTasks(Json("""
            { "records": [
              { "id": "s", "type": "Stage", "name": "Build", "state": "completed", "result": "failed" },
              { "id": "t2", "type": "Task", "name": "Test", "state": "completed", "result": "failed",
                "startTime": "2026-09-22T10:02:00Z", "log": { "id": 7 } },
              { "id": "t1", "type": "Task", "name": "Checkout", "state": "completed", "result": "succeeded",
                "startTime": "2026-09-22T10:00:00Z", "log": { "id": 3 } },
              { "id": "t3", "type": "Task", "name": "Publish", "state": "pending" } ] }
            """));

        Assert.Equal(new[] { "Checkout", "Test", "Publish" }, tasks.Select(t => t.Name));
        Assert.Equal(PipelineRunStatus.Failed, tasks[1].Status);
        Assert.Null(tasks[2].LogId);
    }

    // MARK: - Log sections

    [Fact]
    public void Log_FailedSectionsOpenOtherwiseTheLast()
    {
        var failing = new PipelineRunLog
        {
            Sections = new()
            {
                new() { Name = "a", Status = PipelineRunStatus.Succeeded },
                new() { Name = "b", Status = PipelineRunStatus.Failed },
                new() { Name = "c", Status = PipelineRunStatus.Succeeded },
            },
        };
        Assert.Equal(new[] { false, true, false }, PipelineLogSectionViewModel.From(failing).Select(s => s.IsExpanded));

        var green = new PipelineRunLog
        {
            Sections = new()
            {
                new() { Name = "a", Status = PipelineRunStatus.Succeeded },
                new() { Name = "b", Status = PipelineRunStatus.Succeeded },
            },
        };
        Assert.Equal(new[] { false, true }, PipelineLogSectionViewModel.From(green).Select(s => s.IsExpanded));
    }

    [Fact]
    public void Log_TailKeepsTheEnd()
    {
        var text = new string('a', PipelineRunLog.SectionCap) + "END";
        var (tail, cut) = PipelineRunLog.Tail(text);
        Assert.True(cut);
        Assert.EndsWith("END", tail);
        Assert.False(PipelineRunLog.Tail("short").Truncated);
    }

    // MARK: - Filters

    private static PipelineRun Run(PullRequestSource source, PipelineRunStatus status, string name, int minutesAgo) => new()
    {
        Source = source,
        Container = "acme",
        Repository = "fleet",
        PipelineName = name,
        RunId = minutesAgo,
        RunNumber = minutesAgo.ToString(),
        Status = status,
        Branch = "main",
        StartedAt = DateTime.UtcNow.AddMinutes(-minutesAgo),
    };

    [Fact]
    public void Runs_FilterBySourceStatusAndSearch()
    {
        var runs = new[]
        {
            Run(PullRequestSource.GitHub, PipelineRunStatus.Failed, "CI", 5),
            Run(PullRequestSource.AzureDevOps, PipelineRunStatus.Partial, "Build + Import", 10),
            Run(PullRequestSource.AzureDevOps, PipelineRunStatus.Running, "Publish", 1),
            Run(PullRequestSource.GitHub, PipelineRunStatus.Succeeded, "Release", 20),
        };

        Assert.Equal(new[] { "Publish", "CI", "Build + Import", "Release" },
            CommitsAndPipelinesFilter.Runs(runs, DevelopmentSourceFilter.All, PipelineStatusFilter.All, null).Select(r => r.PipelineName));
        Assert.Equal(new[] { "CI", "Build + Import" },
            CommitsAndPipelinesFilter.Runs(runs, DevelopmentSourceFilter.All, PipelineStatusFilter.Failed, null).Select(r => r.PipelineName));
        Assert.Equal(new[] { "Publish" },
            CommitsAndPipelinesFilter.Runs(runs, DevelopmentSourceFilter.DevOps, PipelineStatusFilter.Running, null).Select(r => r.PipelineName));
        Assert.Equal(new[] { "Release" },
            CommitsAndPipelinesFilter.Runs(runs, DevelopmentSourceFilter.All, PipelineStatusFilter.All, "rel").Select(r => r.PipelineName));
    }

    [Fact]
    public void Runs_FailedMeansLatestRunFailed()
    {
        var oldRed = Run(PullRequestSource.GitHub, PipelineRunStatus.Failed, "CI", 60);
        var newGreen = Run(PullRequestSource.GitHub, PipelineRunStatus.Succeeded, "CI", 5);
        var stillRed = Run(PullRequestSource.AzureDevOps, PipelineRunStatus.Failed, "Build", 10);
        var runs = new[] { oldRed, newGreen, stillRed };

        Assert.Equal(new[] { "Build" },
            CommitsAndPipelinesFilter.Runs(runs, DevelopmentSourceFilter.All, PipelineStatusFilter.Failed, null).Select(r => r.PipelineName));
        Assert.Equal(1, CommitsAndPipelinesFilter.FailingCount(runs));

        // Succeeded still matches every green run, not just the latest.
        Assert.Single(CommitsAndPipelinesFilter.Runs(runs, DevelopmentSourceFilter.All, PipelineStatusFilter.Succeeded, null));
    }

    [Fact]
    public void Commits_FilterMatchesRepoOrCommit()
    {
        var repos = new[]
        {
            new RepositoryCommits
            {
                Source = PullRequestSource.GitHub, Container = "acme", Repository = "fleet",
                Commits = new() { new PullRequestCommit { Id = "abc123", Message = "Add widget", AuthorName = "ada", Date = DateTime.UtcNow } },
            },
            new RepositoryCommits
            {
                Source = PullRequestSource.AzureDevOps, Container = "Platform", Repository = "tools",
                Commits = new() { new PullRequestCommit { Id = "def456", Message = "Fix pipeline", AuthorName = "bob", Date = DateTime.UtcNow.AddDays(-1) } },
            },
        };

        Assert.Equal(2, CommitsAndPipelinesFilter.Commits(repos, DevelopmentSourceFilter.All, null).Count);
        Assert.Equal("acme/fleet", Assert.Single(CommitsAndPipelinesFilter.Commits(repos, DevelopmentSourceFilter.All, "widget")).DisplayName);
        Assert.Equal("Platform/tools", Assert.Single(CommitsAndPipelinesFilter.Commits(repos, DevelopmentSourceFilter.All, "def4")).DisplayName);
        Assert.Equal("Platform/tools", Assert.Single(CommitsAndPipelinesFilter.Commits(repos, DevelopmentSourceFilter.DevOps, null)).DisplayName);
    }

    [Fact]
    public void RepositoryCard_ShowsThreeThenAll()
    {
        var repo = new RepositoryCommits
        {
            Container = "acme", Repository = "fleet",
            Commits = Enumerable.Range(0, 5).Select(i => new PullRequestCommit { Id = $"c{i}", Message = $"m{i}" }).ToList(),
        };
        var card = new RepositoryCommitsViewModel { Repository = repo };

        Assert.Equal(3, card.VisibleCommits.Count());
        Assert.Equal("Show all 5", card.ShowAllLabel);
        card.ToggleShowAll();
        Assert.Equal(5, card.VisibleCommits.Count());
        Assert.Equal("Show fewer", card.ShowAllLabel);
    }

    [Fact]
    public void ResourceLimitIsRecognized()
    {
        Assert.True(GitHubPullRequestService.IsResourceLimit(new InvalidOperationException("GitHub GraphQL error: Resource limits for this query exceeded.")));
        Assert.False(GitHubPullRequestService.IsResourceLimit(new InvalidOperationException("Bad credentials")));
    }
}
