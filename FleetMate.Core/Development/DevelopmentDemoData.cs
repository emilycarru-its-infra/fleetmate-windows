using FleetMate.Core.Knowledge;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Shared;

namespace FleetMate.Core.Development;

/// <summary>
/// Fictional Development data for demo mode and screenshots: made-up
/// organizations, repositories, people, pipelines and notifications. Nothing
/// here names a real project, account or person, so captures made from it can
/// be published. Dates are relative to <paramref name="now"/> so ages read the
/// same whenever it runs.
/// </summary>
public static class DevelopmentDemoData
{
    private const string Org = "northwind-labs";
    private const string Project = "Fabrikam";

    public static PullRequestQueue PullRequests(DateTime now)
    {
        var queue = new PullRequestQueue
        {
            PullRequests =
            {
                Pr(PullRequestSource.GitHub, Org, "widget-api", 142, "Add pagination to the widgets endpoint", "Avery Quinn",
                    "feature/widget-paging", now.AddHours(-2), now.AddDays(-1), PullRequestRelation.AssignedToMe),
                Pr(PullRequestSource.GitHub, Org, "widget-api", 139, "Retry transient storage errors with backoff", "Jordan Ellis",
                    "fix/storage-retry", now.AddHours(-20), now.AddDays(-3), PullRequestRelation.Involved),
                Pr(PullRequestSource.GitHub, Org, "design-tokens", 58, "Darken secondary text for contrast", "Riley Chen",
                    "fix/secondary-contrast", now.AddMinutes(-35), now.AddHours(-5), PullRequestRelation.CreatedByMe),
                Pr(PullRequestSource.GitHub, Org, "design-tokens", 57, "Draft: spacing scale for dense lists", "Sam Okafor",
                    "feature/dense-spacing", now.AddDays(-4), now.AddDays(-6), PullRequestRelation.Organization, PullRequestState.Draft),
                Pr(PullRequestSource.AzureDevOps, Project, "Inventory.Service", 2210, "Cache warehouse lookups for an hour", "Morgan Patel",
                    "users/morgan/cache-lookups", now.AddHours(-6), now.AddDays(-2), PullRequestRelation.AssignedToMe),
                Pr(PullRequestSource.AzureDevOps, Project, "Inventory.Service", 2204, "Move the nightly import to a queue", "Taylor Brooks",
                    "feature/import-queue", now.AddDays(-2), now.AddDays(-8), PullRequestRelation.Organization, conflicts: true),
                Pr(PullRequestSource.AzureDevOps, Project, "Portal.Web", 981, "Show the order status timeline", "Casey Romero",
                    "feature/status-timeline", now.AddDays(-1), now.AddDays(-1), PullRequestRelation.Involved),
            },
        };
        queue.ViewerNames.Add("Riley Chen");
        foreach (var pr in queue.PullRequests.Take(3))
            pr.RecentComments.Add(new PullRequestComment
            {
                Id = $"c{pr.Number}",
                AuthorName = "Jordan Ellis",
                Body = "Looks good once the test covers the empty page.",
                Date = (pr.UpdatedAt ?? now).AddMinutes(-10),
            });
        return queue;
    }

    /// <summary>What the detail pane shows for a fictional pull request.</summary>
    public static PullRequestDetail Detail(UnifiedPullRequest pr) => new()
    {
        Body = $"## Why\n\n{pr.Title}. The current behaviour makes large lists slow to load.\n\n## What changed\n\n- New query options with sensible defaults\n- Tests for the first, middle and last page\n\n- [x] Unit tests\n- [ ] Docs updated",
        Commits =
        {
            new PullRequestCommit { Id = "4f3c2a1b9d8e7f60", Message = pr.Title, AuthorName = pr.AuthorName, Date = pr.UpdatedAt },
            new PullRequestCommit { Id = "a1b2c3d4e5f60718", Message = "Cover the empty page", AuthorName = pr.AuthorName, Date = pr.CreatedAt },
        },
        Comments =
        {
            new PullRequestComment { Id = "d1", AuthorName = "Jordan Ellis", Body = "Could the page size come from settings?", Date = pr.CreatedAt },
            new PullRequestComment { Id = "d2", AuthorName = pr.AuthorName, Body = "Done in the second commit.", Date = pr.UpdatedAt },
            new PullRequestComment { Id = "d3", AuthorName = "Jordan Ellis", Body = "approved", Date = pr.UpdatedAt, IsSystem = true },
        },
        Files = DiffParser.Parse(DemoPatch).Files,
    };

    public static List<PullRequestCheck> Checks() => new()
    {
        new PullRequestCheck { Name = "build", State = PullRequestCheckState.Success, IsRequired = true },
        new PullRequestCheck { Name = "unit tests", State = PullRequestCheckState.Success, IsRequired = true },
        new PullRequestCheck { Name = "lint", State = PullRequestCheckState.Pending },
    };

    public static List<RepositoryCommits> Commits(DateTime now) => new()
    {
        Repo(PullRequestSource.GitHub, Org, "widget-api", now,
            ("Add pagination to the widgets endpoint", "Avery Quinn", 2), ("Retry transient storage errors", "Jordan Ellis", 20),
            ("Bump the test runner", "Avery Quinn", 30), ("Document the rate limits", "Riley Chen", 52)),
        Repo(PullRequestSource.GitHub, Org, "design-tokens", now,
            ("Darken secondary text for contrast", "Riley Chen", 1), ("Add focus ring tokens", "Sam Okafor", 70)),
        Repo(PullRequestSource.AzureDevOps, Project, "Inventory.Service", now,
            ("Cache warehouse lookups", "Morgan Patel", 6), ("Split the import job", "Taylor Brooks", 40),
            ("Log slow queries", "Morgan Patel", 90)),
    };

    public static List<PipelineRun> Runs(DateTime now) => new()
    {
        Run(PullRequestSource.GitHub, Org, "widget-api", "CI", 1, 812, PipelineRunStatus.Running, "feature/widget-paging", "Avery Quinn", now.AddMinutes(-4), null),
        Run(PullRequestSource.GitHub, Org, "widget-api", "CI", 1, 811, PipelineRunStatus.Succeeded, "main", "Jordan Ellis", now.AddHours(-3), now.AddHours(-3).AddMinutes(6)),
        Run(PullRequestSource.GitHub, Org, "design-tokens", "Publish", 2, 77, PipelineRunStatus.Failed, "main", "Riley Chen", now.AddHours(-1), now.AddHours(-1).AddMinutes(2)),
        Run(PullRequestSource.AzureDevOps, Project, "Inventory.Service", "Inventory-Deploy", 14, 3301, PipelineRunStatus.Running, "main", "Morgan Patel", now.AddMinutes(-12), null),
        Run(PullRequestSource.AzureDevOps, Project, "Inventory.Service", "Inventory-CI", 13, 3300, PipelineRunStatus.Succeeded, "main", "Morgan Patel", now.AddMinutes(-40), now.AddMinutes(-31)),
        Run(PullRequestSource.AzureDevOps, Project, "Portal.Web", "Portal-CI", 21, 990, PipelineRunStatus.Partial, "feature/status-timeline", "Casey Romero", now.AddHours(-5), now.AddHours(-5).AddMinutes(11)),
        Run(PullRequestSource.AzureDevOps, Project, "Portal.Web", "Portal-CI", 21, 989, PipelineRunStatus.Cancelled, "feature/status-timeline", "Casey Romero", now.AddHours(-6), now.AddHours(-6).AddMinutes(1)),
    };

    public static List<GitHubNotification> Notifications(DateTime now) => new()
    {
        new GitHubNotification
        {
            Id = "n1", Reason = "review_requested", Unread = true, UpdatedAt = now.AddHours(-2), SubjectTitle = "Add pagination to the widgets endpoint",
            SubjectType = "PullRequest", SubjectApiUrl = $"https://api.example.com/repos/{Org}/widget-api/pulls/142",
            WebUrl = $"https://example.com/{Org}/widget-api/pull/142", Repository = $"{Org}/widget-api",
        },
        new GitHubNotification
        {
            Id = "n2", Reason = "mention", Unread = true, UpdatedAt = now.AddHours(-9), SubjectTitle = "Tokens render too light on dark surfaces",
            SubjectType = "Issue", SubjectApiUrl = $"https://api.example.com/repos/{Org}/design-tokens/issues/61",
            WebUrl = $"https://example.com/{Org}/design-tokens/issues/61", Repository = $"{Org}/design-tokens",
        },
        new GitHubNotification
        {
            Id = "n3", Reason = "ci_activity", Unread = false, UpdatedAt = now.AddDays(-1), SubjectTitle = "Publish failed on main",
            SubjectType = "CheckSuite", WebUrl = $"https://example.com/{Org}/design-tokens/actions", Repository = $"{Org}/design-tokens",
        },
    };

    public static List<SkillEntry> Skills() => new()
    {
        new SkillEntry(SkillKind.Skill, SkillOrigin.Shared, "ship-change", "Commit, push and open a pull request for the current branch.",
            "agents/skills/ship-change/SKILL.md", new[] { "checklist.md" },
            "# Ship a change\n\n1. Run the tests.\n2. Commit with a plain subject.\n3. Open or update the pull request.\n\n```\ngit push -u origin HEAD\n```"),
        new SkillEntry(SkillKind.Hook, SkillOrigin.Shared, "guard-main", "Blocks pushes straight to main.",
            "agents/hooks/guard-main.sh", Array.Empty<string>(), "```sh\n# refuses git push to main\n```"),
        new SkillEntry(SkillKind.Standard, SkillOrigin.Shared, "public-repo", "What may and may not be published.",
            "agents/scopes/public-repo.md", Array.Empty<string>(), "Everything committed is public. **Never** publish internal names."),
        new SkillEntry(SkillKind.Skill, SkillOrigin.Local, "tidy-notes", "Sort this week's notes into folders.",
            "skills/tidy-notes/SKILL.md", Array.Empty<string>(), "Sort notes by *project* and date."),
    };

    /// <summary>What the commit detail shows for a fictional commit: a diff on GitHub, a change list on Azure DevOps.</summary>
    public static CommitDetail CommitDetail(RepositoryCommits repository, PullRequestCommit commit)
    {
        var message = $"{(commit.Subject.Length > 0 ? commit.Subject : "Update the widgets query")}\n\nKeeps the first page fast and covers the edge cases in tests.";
        if (repository.Source == PullRequestSource.AzureDevOps)
            return new CommitDetail
            {
                Message = message,
                Changes =
                {
                    new CommitChange { Path = "/src/Inventory/WarehouseCache.cs", ChangeType = "edit" },
                    new CommitChange { Path = "/src/Inventory/WarehouseCacheOptions.cs", ChangeType = "add" },
                    new CommitChange { Path = "/tests/WarehouseCacheTests.cs", ChangeType = "add" },
                },
            };
        var files = DiffParser.Parse(DemoPatch).Files;
        return new CommitDetail
        {
            Message = message,
            Files = files,
            Additions = files.Sum(f => f.Insertions),
            Deletions = files.Sum(f => f.Deletions),
        };
    }

    /// <summary>A fictional run's log: jobs that match its status, the failing one last.</summary>
    public static PipelineRunLog RunLog(PipelineRun run)
    {
        var last = run.Status switch
        {
            PipelineRunStatus.Failed => PipelineRunStatus.Failed,
            PipelineRunStatus.Partial => PipelineRunStatus.Partial,
            PipelineRunStatus.Running or PipelineRunStatus.Queued => PipelineRunStatus.Running,
            PipelineRunStatus.Cancelled => PipelineRunStatus.Cancelled,
            _ => PipelineRunStatus.Succeeded,
        };
        return new PipelineRunLog
        {
            RunId = run.RunId,
            Sections =
            {
                Section("checkout", "Checkout", PipelineRunStatus.Succeeded,
                    "Syncing repository: example.com/" + run.Container + "/" + run.Repository + "\nHEAD is now at 4f3c2a1b"),
                Section("restore", "Restore packages", PipelineRunStatus.Succeeded,
                    "Restoring packages for Widgets.sln...\n  Restored 14 projects in 6.2 s."),
                Section("test", "Build and test", last, last switch
                {
                    PipelineRunStatus.Failed => "Build succeeded.\nRunning 212 tests...\n  Failed WidgetQueryTests.SecondPageSkipsTheFirst\n    Expected: 50\n    Actual:   0\nTotal: 212, Failed: 1",
                    PipelineRunStatus.Partial => "Build succeeded with 3 warnings.\nRunning 212 tests...\nTotal: 212, Passed: 212\nPublishing coverage skipped: no report found.",
                    PipelineRunStatus.Running => "Build succeeded.\nRunning 212 tests...",
                    PipelineRunStatus.Cancelled => "Build started.\nThe run was cancelled.",
                    _ => "Build succeeded.\nRunning 212 tests...\nTotal: 212, Passed: 212",
                }),
            },
        };
    }

    /// <summary>The fictional Azure DevOps project's pending approval, holding its running deploy.</summary>
    public static IPipelineApprovalSource Approvals(DateTime now) => new DemoApprovalSource(now);

    private sealed class DemoApprovalSource(DateTime now) : IPipelineApprovalSource
    {
        public Task<List<PipelineApproval>> GetPendingApprovalsAsync(string project) => Task.FromResult(project == Project
            ? new List<PipelineApproval>
            {
                new()
                {
                    Id = "6f1d2c3b-0000-4000-8000-000000003301",
                    Project = Project,
                    BuildId = 3301,
                    Instructions = "Check the staging smoke tests on the run summary, then approve the production deploy.",
                    CreatedOn = now.AddMinutes(-9),
                    Approvers = new[] { "Release approvers" },
                },
            }
            : new List<PipelineApproval>());

        public Task<HashSet<string>> GetOpenTimelineIdentifiersAsync(string project, long buildId) => Task.FromResult(new HashSet<string>());
    }

    private static PipelineRunLog.Section Section(string id, string name, PipelineRunStatus status, string text) =>
        new() { Id = id, Name = name, Status = status, Text = text };

    private static UnifiedPullRequest Pr(PullRequestSource source, string container, string repo, int number, string title, string author,
        string branch, DateTime updated, DateTime created, PullRequestRelation relation,
        PullRequestState state = PullRequestState.Open, bool conflicts = false) => new()
    {
        Source = source,
        Number = number,
        Title = title,
        AuthorName = author,
        Container = container,
        Repository = repo,
        SourceBranch = branch,
        TargetBranch = "main",
        CreatedAt = created,
        UpdatedAt = updated,
        State = state,
        HasConflicts = conflicts,
        CommentCount = number % 4,
        WebUrl = $"https://example.com/{container}/{repo}/pull/{number}",
        Relations = new HashSet<PullRequestRelation> { relation },
        Reviewers = new[] { new PullRequestReviewer { Id = "r1", DisplayName = "Jordan Ellis" } },
    };

    private static RepositoryCommits Repo(PullRequestSource source, string container, string repo, DateTime now,
        params (string Message, string Author, int HoursAgo)[] commits) => new()
    {
        Source = source,
        Container = container,
        Repository = repo,
        RepositoryId = repo,
        DefaultBranch = "main",
        WebUrl = $"https://example.com/{container}/{repo}",
        Commits = commits.Select((c, i) => new PullRequestCommit
        {
            Id = $"{(uint)HashCode.Combine(repo, i):x8}{i:x8}",
            Message = c.Message,
            AuthorName = c.Author,
            Date = now.AddHours(-c.HoursAgo),
            Url = $"https://example.com/{container}/{repo}/commit/{i}",
        }).ToList(),
    };

    private static PipelineRun Run(PullRequestSource source, string container, string repo, string pipeline, int pipelineId, long runId,
        PipelineRunStatus status, string branch, string by, DateTime started, DateTime? finished) => new()
    {
        Source = source,
        Container = container,
        Repository = repo,
        PipelineName = pipeline,
        PipelineId = pipelineId,
        RunId = runId,
        RunNumber = runId.ToString(),
        Status = status,
        Branch = branch,
        TriggeredBy = by,
        StartedAt = started,
        FinishedAt = finished,
        WebUrl = $"https://example.com/{container}/{repo}/runs/{runId}",
    };

    private const string DemoPatch = """
        diff --git a/src/Widgets/WidgetQuery.cs b/src/Widgets/WidgetQuery.cs
        --- a/src/Widgets/WidgetQuery.cs
        +++ b/src/Widgets/WidgetQuery.cs
        @@ -10,7 +10,9 @@ public sealed class WidgetQuery
             public string? Search { get; init; }
        -    public int Take { get; init; } = 500;
        +    public int Page { get; init; } = 1;
        +    public int PageSize { get; init; } = 50;
        +    public int Skip => (Page - 1) * PageSize;
             public bool IncludeArchived { get; init; }
         }
        diff --git a/tests/WidgetQueryTests.cs b/tests/WidgetQueryTests.cs
        --- /dev/null
        +++ b/tests/WidgetQueryTests.cs
        @@ -0,0 +1,6 @@
        +public class WidgetQueryTests
        +{
        +    [Fact]
        +    public void SecondPageSkipsTheFirst() =>
        +        Assert.Equal(50, new WidgetQuery { Page = 2 }.Skip);
        +}
        """;
}
