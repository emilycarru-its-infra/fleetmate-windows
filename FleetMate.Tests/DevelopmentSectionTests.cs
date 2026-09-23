using System.Text.Json;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.GUI.Views.Development;
using FleetMate.GUI.Views.Shared;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The Development tab: inbox parsing, check normalization across both providers,
/// the wider PR search, and the list's filters and action rules.
/// </summary>
public class DevelopmentSectionTests
{
    // MARK: - Inbox

    private const string NotificationsJson = """
        [
          {
            "id": "101", "unread": false, "reason": "subscribed", "updated_at": "2026-09-22T10:00:00Z",
            "subject": { "title": "Old read one", "type": "Issue", "url": "https://api.github.com/repos/acme/fleet/issues/7" },
            "repository": { "full_name": "acme/fleet", "html_url": "https://github.com/acme/fleet" }
          },
          {
            "id": "102", "unread": true, "reason": "review_requested", "updated_at": "2026-09-21T09:00:00Z",
            "subject": { "title": "Add the widget", "type": "PullRequest", "url": "https://api.github.com/repos/acme/fleet/pulls/42" },
            "repository": { "full_name": "acme/fleet", "html_url": "https://github.com/acme/fleet" }
          },
          {
            "id": "103", "unread": true, "reason": "ci_activity", "updated_at": "2026-09-22T11:00:00Z",
            "subject": { "title": "CI failed", "type": "CheckSuite", "url": null },
            "repository": { "full_name": "acme/tools", "html_url": "https://github.com/acme/tools" }
          }
        ]
        """;

    [Fact]
    public void Inbox_SortsUnreadFirstThenNewest()
    {
        using var doc = JsonDocument.Parse(NotificationsJson);
        var list = GitHubNotificationService.Parse(doc.RootElement);

        Assert.Equal(new[] { "103", "102", "101" }, list.Select(n => n.Id));
    }

    [Fact]
    public void Inbox_ParsesSubjectAndDerivesWebUrlAndNumber()
    {
        using var doc = JsonDocument.Parse(NotificationsJson);
        var pr = GitHubNotificationService.Parse(doc.RootElement).Single(n => n.Id == "102");

        Assert.True(pr.IsPullRequest);
        Assert.True(pr.Unread);
        Assert.Equal(42, pr.SubjectNumber);
        Assert.Equal("acme", pr.Owner);
        Assert.Equal("fleet", pr.RepositoryName);
        Assert.Equal("https://github.com/acme/fleet/pull/42", pr.WebUrl);
    }

    [Theory]
    [InlineData("https://api.github.com/repos/o/r/pulls/5", "https://github.com/o/r/pull/5")]
    [InlineData("https://api.github.com/repos/o/r/issues/9", "https://github.com/o/r/issues/9")]
    [InlineData("https://api.github.com/repos/o/r/commits/abc123", "https://github.com/o/r/commit/abc123")]
    [InlineData("https://api.github.com/repos/o/r/releases/77", "https://github.com/o/r")]
    [InlineData(null, "https://github.com/o/r")]
    public void Inbox_WebUrlFallsBackToRepositoryPage(string? api, string expected) =>
        Assert.Equal(expected, GitHubNotificationService.WebUrlFor(api, "https://github.com/o/r", "o/r"));

    [Fact]
    public void Inbox_SubjectWithoutUrlHasNoNumber()
    {
        var n = new GitHubNotification { SubjectType = "CheckSuite", SubjectApiUrl = null };
        Assert.Null(n.SubjectNumber);
    }

    [Theory]
    [InlineData("review_requested", "Review requested")]
    [InlineData("mention", "Mentioned")]
    [InlineData("something_new", "something new")]
    public void Inbox_ReasonLabels(string reason, string expected) =>
        Assert.Equal(expected, DevelopmentNotificationRowViewModel.ReasonLabel(reason));

    // MARK: - Search

    [Fact]
    public void Search_AddsInvolvesAndOneQueryPerDistinctOwner()
    {
        var searches = GitHubPullRequestService.DevelopmentSearches(new[] { "acme", "ACME", "", "widgets" });

        Assert.Contains(searches, s => s.Relation == PullRequestRelation.Involved && s.Query.Contains("involves:@me"));

        var owners = searches.Where(s => s.Relation == PullRequestRelation.Organization).ToList();
        Assert.Equal(2, owners.Count);
        Assert.Contains(owners, s => s.Query.Contains("user:acme"));
        Assert.Contains(owners, s => s.Query.Contains("user:widgets"));

        // Aliases must be valid GraphQL identifiers and unique.
        Assert.Equal(searches.Count, searches.Select(s => s.Alias).Distinct().Count());
        Assert.All(searches, s => Assert.Matches("^[A-Za-z_][A-Za-z0-9_]*$", s.Alias));
    }

    [Fact]
    public void Search_BatchesTwoPerQueryPersonalFirst()
    {
        var searches = GitHubPullRequestService.DevelopmentSearches(new[] { "a", "b", "c" });
        var batches = GitHubPullRequestService.Batch(searches);

        Assert.Equal(new[] { 2, 2, 2, 1 }, batches.Select(b => b.Count));
        Assert.All(batches.Take(2).SelectMany(b => b), s => Assert.NotEqual(PullRequestRelation.Organization, s.Relation));
        Assert.All(batches.Skip(2).SelectMany(b => b), s => Assert.Equal(PullRequestRelation.Organization, s.Relation));
        Assert.Equal(searches.Count, batches.Sum(b => b.Count));
    }

    // MARK: - Checks

    [Theory]
    [InlineData("IN_PROGRESS", null, PullRequestCheckState.Pending)]
    [InlineData("COMPLETED", "SUCCESS", PullRequestCheckState.Success)]
    [InlineData("COMPLETED", "TIMED_OUT", PullRequestCheckState.Failure)]
    [InlineData("COMPLETED", "SKIPPED", PullRequestCheckState.Skipped)]
    [InlineData("COMPLETED", "NEUTRAL", PullRequestCheckState.Neutral)]
    public void Checks_GitHubCheckRunState(string status, string? conclusion, PullRequestCheckState expected) =>
        Assert.Equal(expected, GitHubPullRequestService.CheckRunState(status, conclusion));

    [Fact]
    public void Checks_GitHubRollupMixesCheckRunsAndStatuses()
    {
        const string json = """
            { "repository": { "pullRequest": { "commits": { "nodes": [ { "commit": { "statusCheckRollup": { "contexts": { "nodes": [
              { "__typename": "CheckRun", "name": "build", "status": "COMPLETED", "conclusion": "FAILURE", "detailsUrl": "https://x/1", "isRequired": true },
              { "__typename": "StatusContext", "context": "ci/legacy", "state": "PENDING", "targetUrl": "https://x/2", "isRequired": false }
            ] } } } } ] } } } }
            """;
        using var doc = JsonDocument.Parse(json);
        var checks = GitHubPullRequestService.ParseChecks(doc.RootElement);

        Assert.Equal(2, checks.Count);
        Assert.Equal(PullRequestCheckState.Failure, checks[0].State);
        Assert.True(checks[0].IsRequired);
        Assert.Equal("ci/legacy", checks[1].Name);
        Assert.Equal(PullRequestCheckState.Pending, checks[1].State);
    }

    [Fact]
    public void Checks_GitHubRollupAbsentMeansNoChecks()
    {
        using var doc = JsonDocument.Parse("""{ "repository": { "pullRequest": { "commits": { "nodes": [ { "commit": { "statusCheckRollup": null } } ] } } } }""");
        Assert.Empty(GitHubPullRequestService.ParseChecks(doc.RootElement));
    }

    [Fact]
    public void Checks_DevOpsPoliciesSkipDisabledAndLinkBuilds()
    {
        const string json = """
            { "value": [
              { "status": "approved", "configuration": { "isEnabled": true, "isBlocking": true,
                  "type": { "displayName": "Minimum number of reviewers" }, "settings": {} } },
              { "status": "running", "context": { "buildId": 1234 }, "configuration": { "isEnabled": true, "isBlocking": false,
                  "type": { "displayName": "Build" }, "settings": { "displayName": "CI validation" } } },
              { "status": "rejected", "configuration": { "isEnabled": false, "isBlocking": true,
                  "type": { "displayName": "Work item linking" }, "settings": {} } }
            ] }
            """;
        using var doc = JsonDocument.Parse(json);
        var checks = AzureDevOpsService.ParsePolicyEvaluations(doc.RootElement, "https://devops.example.com/acme/", "Platform");

        Assert.Equal(2, checks.Count);
        Assert.Equal(PullRequestCheckState.Success, checks[0].State);
        Assert.True(checks[0].IsRequired);
        Assert.Equal("CI validation", checks[1].Name);
        Assert.Equal(PullRequestCheckState.Pending, checks[1].State);
        Assert.Equal("https://devops.example.com/acme/Platform/_build/results?buildId=1234", checks[1].DetailsUrl);
    }

    [Theory]
    [InlineData("approved", PullRequestCheckState.Success)]
    [InlineData("rejected", PullRequestCheckState.Failure)]
    [InlineData("broken", PullRequestCheckState.Failure)]
    [InlineData("notApplicable", PullRequestCheckState.Skipped)]
    [InlineData("queued", PullRequestCheckState.Pending)]
    public void Checks_DevOpsPolicyState(string status, PullRequestCheckState expected) =>
        Assert.Equal(expected, AzureDevOpsService.PolicyState(status));

    [Fact]
    public void Checks_SummaryNamesFailingAndPending()
    {
        var checks = new[]
        {
            new PullRequestCheck { State = PullRequestCheckState.Success },
            new PullRequestCheck { State = PullRequestCheckState.Failure },
            new PullRequestCheck { State = PullRequestCheckState.Pending },
        };
        Assert.Equal("1 of 3 passed · 1 failing · 1 pending", PullRequestCheckViewModel.Summary(checks));
        Assert.Equal("No checks", PullRequestCheckViewModel.Summary(Array.Empty<PullRequestCheck>()));
    }

    // MARK: - Action plan

    private static UnifiedPullRequest Pr(
        PullRequestSource source = PullRequestSource.GitHub,
        PullRequestState state = PullRequestState.Open,
        string nodeId = "PR_x",
        string repo = "fleet",
        string title = "Add the widget",
        params PullRequestRelation[] relations) => new()
    {
        Source = source,
        Number = 42,
        Title = title,
        AuthorName = "ada",
        Container = "acme",
        Repository = repo,
        SourceBranch = "feature/widget",
        TargetBranch = "main",
        State = state,
        NodeId = nodeId,
        Relations = new HashSet<PullRequestRelation>(relations),
    };

    [Fact]
    public void Plan_NamesDifferPerProvider()
    {
        var gh = new PullRequestActionPlan { PullRequest = Pr() };
        var ado = new PullRequestActionPlan { PullRequest = Pr(PullRequestSource.AzureDevOps, nodeId: "") };

        Assert.Equal("Merge", gh.MergeLabel);
        Assert.Equal("Close", gh.CloseLabel);
        Assert.True(gh.ShowMergeMethod);

        Assert.Equal("Complete", ado.MergeLabel);
        Assert.Equal("Abandon", ado.CloseLabel);
        Assert.False(ado.ShowMergeMethod);
        Assert.True(ado.CanToggleDraft);
    }

    [Fact]
    public void Plan_DraftCannotMergeButCanBeMarkedReady()
    {
        var plan = new PullRequestActionPlan { PullRequest = Pr(state: PullRequestState.Draft) };
        Assert.False(plan.CanMerge);
        Assert.True(plan.CanToggleDraft);
        Assert.Equal("Mark ready", plan.DraftLabel);
    }

    [Theory]
    [InlineData(PullRequestState.Merged)]
    [InlineData(PullRequestState.Closed)]
    public void Plan_FinishedPullRequestOffersNothing(PullRequestState state)
    {
        var plan = new PullRequestActionPlan { PullRequest = Pr(state: state) };
        Assert.False(plan.IsLive);
        Assert.False(plan.CanMerge || plan.CanClose || plan.CanReview || plan.CanComment || plan.CanToggleDraft);
    }

    [Fact]
    public void Plan_GitHubWithoutNodeIdCannotToggleDraft() =>
        Assert.False(new PullRequestActionPlan { PullRequest = Pr(nodeId: "") }.CanToggleDraft);

    // MARK: - Filters

    [Fact]
    public void Filter_MineExcludesOrganizationOnlyRows()
    {
        var mine = Pr(relations: new[] { PullRequestRelation.Organization, PullRequestRelation.Involved });
        var orgOnly = Pr(repo: "other", relations: PullRequestRelation.Organization);

        var result = DevelopmentFilter.Apply(new[] { mine, orgOnly }, DevelopmentSourceFilter.All, DevelopmentScope.Mine, null, null);
        Assert.Single(result);
        Assert.Same(mine, result[0]);

        Assert.Equal(2, DevelopmentFilter.Apply(new[] { mine, orgOnly }, DevelopmentSourceFilter.All, DevelopmentScope.Everything, null, null).Count);
    }

    [Fact]
    public void Filter_SourceRepositoryAndSearchCombine()
    {
        var a = Pr(repo: "fleet", title: "Alpha");
        var b = Pr(repo: "tools", title: "Beta");
        var c = Pr(PullRequestSource.AzureDevOps, repo: "fleet", title: "Alpha two");

        Assert.Equal(new[] { a }, DevelopmentFilter.Apply(new[] { a, b, c }, DevelopmentSourceFilter.GitHub, DevelopmentScope.Everything, "acme/fleet", "alp"));
        Assert.Equal(2, DevelopmentFilter.Apply(new[] { a, b, c }, DevelopmentSourceFilter.All, DevelopmentScope.Everything, null, "ALPHA").Count);
        Assert.Equal(3, DevelopmentFilter.Apply(new[] { a, b, c }, DevelopmentSourceFilter.All, DevelopmentScope.Everything, null, "feature/widget").Count);
        Assert.Equal(2, DevelopmentFilter.Apply(new[] { a, b, c }, DevelopmentSourceFilter.GitHub, DevelopmentScope.Everything, null, "#42").Count);
    }

    [Fact]
    public void Filter_RepositoryCountsBusiestFirst()
    {
        var counts = DevelopmentFilter.RepositoryCounts(new[] { Pr(repo: "b"), Pr(repo: "a"), Pr(repo: "b") });
        Assert.Equal(("acme/b", 2), counts[0]);
        Assert.Equal(("acme/a", 1), counts[1]);
    }

    [Fact]
    public void Row_RelationLabelPrefersReview()
    {
        var row = new DevelopmentPullRequestRowViewModel
        {
            PullRequest = Pr(relations: new[] { PullRequestRelation.Involved, PullRequestRelation.AssignedToMe }),
        };
        Assert.Equal("Review", row.RelationLabel);
        Assert.Equal("", new DevelopmentPullRequestRowViewModel { PullRequest = Pr(relations: PullRequestRelation.Organization) }.RelationLabel);
    }

    // MARK: - Activity

    [Fact]
    public void Activity_ParsesCommentsReviewsAndThreadsOldestFirst()
    {
        const string json = """
            {
              "recentComments": { "nodes": [
                { "author": { "login": "ada" }, "body": "Looks close", "createdAt": "2026-09-20T10:00:00Z", "url": "https://github.com/o/r/pull/1#c1" } ] },
              "recentReviews": { "nodes": [
                { "author": { "login": "bob" }, "body": "", "state": "APPROVED", "submittedAt": "2026-09-21T10:00:00Z", "url": "https://github.com/o/r/pull/1#r1" },
                { "author": { "login": "bob" }, "body": "", "state": "PENDING", "submittedAt": "2026-09-22T10:00:00Z", "url": "https://github.com/o/r/pull/1#r2" } ] },
              "recentThreads": { "nodes": [
                { "comments": { "nodes": [
                  { "author": { "login": "cy" }, "body": "Nit on line 4", "createdAt": "2026-09-19T10:00:00Z", "url": "https://github.com/o/r/pull/1#t1" } ] } } ] }
            }
            """;
        using var doc = JsonDocument.Parse(json);
        var activity = GitHubPullRequestService.ParseActivity(doc.RootElement);

        Assert.Equal(new[] { "cy", "ada", "bob" }, activity.Select(c => c.AuthorName));
        Assert.Equal("approved", activity[2].Body);
        Assert.True(activity[2].IsSystem);
        Assert.Equal("https://github.com/o/r/pull/1#c1", activity[1].Url);
    }

    [Fact]
    public void Activity_NewestFirstAndHideMine()
    {
        var older = Pr(repo: "a");
        older.RecentComments = new()
        {
            new PullRequestComment { AuthorName = "me", Body = "mine", Date = new DateTime(2026, 9, 20) },
        };
        var newer = Pr(repo: "b");
        newer.RecentComments = new()
        {
            new PullRequestComment { AuthorName = "ada", Body = "theirs", Date = new DateTime(2026, 9, 22) },
        };

        var viewer = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ME" };

        var all = DevelopmentFilter.Activity(new[] { older, newer }, viewer, hideMine: false);
        Assert.Equal(new[] { "ada", "me" }, all.Select(r => r.AuthorName));

        var hidden = DevelopmentFilter.Activity(new[] { older, newer }, viewer, hideMine: true);
        Assert.Equal(new[] { "ada" }, hidden.Select(r => r.AuthorName));
    }

    [Fact]
    public void Activity_DevOpsThreadsFillCountUpdatedAndLinks()
    {
        var pr = new UnifiedPullRequest
        {
            Source = PullRequestSource.AzureDevOps,
            Number = 7,
            Container = "Platform",
            Repository = "fleet",
            CreatedAt = new DateTime(2026, 9, 1),
            WebUrl = "https://devops.example.com/acme/Platform/_git/fleet/pullrequest/7",
        };

        AzureDevOpsService.ApplyThreads(pr, new List<PullRequestComment>
        {
            new() { Id = "3-1", AuthorName = "System", Body = "voted", Date = new DateTime(2026, 9, 5), IsSystem = true },
            new() { Id = "4-1", AuthorName = "Ada", Body = "Please rename", Date = new DateTime(2026, 9, 3) },
        });

        Assert.Equal(1, pr.CommentCount);
        Assert.Equal(new DateTime(2026, 9, 5), pr.UpdatedAt);
        Assert.Single(pr.RecentComments);
        Assert.EndsWith("pullrequest/7?discussionId=4", pr.RecentComments[0].Url);
    }

    [Fact]
    public void Queue_MergeKeepsViewerNamesAndActivity()
    {
        var a = new PullRequestQueue();
        a.ViewerNames.Add("ada");
        a.Insert(Pr(relations: PullRequestRelation.Organization));

        var withActivity = Pr(relations: PullRequestRelation.CreatedByMe);
        withActivity.RecentComments = new() { new PullRequestComment { AuthorName = "bob", Body = "hi" } };
        var b = new PullRequestQueue();
        b.ViewerNames.Add("Ada Lovelace");
        b.Insert(withActivity);

        a.Merge(b);

        Assert.Equal(2, a.ViewerNames.Count);
        Assert.Single(a.PullRequests[0].RecentComments);
    }

    [Fact]
    public void Merge_UnionsRelationsAcrossQueries()
    {
        var queue = new PullRequestQueue();
        queue.Insert(Pr(relations: PullRequestRelation.Organization));
        queue.Insert(Pr(relations: PullRequestRelation.CreatedByMe));

        Assert.Single(queue.PullRequests);
        Assert.True(DevelopmentFilter.MatchesScope(queue.PullRequests[0], DevelopmentScope.Mine));
    }
}
