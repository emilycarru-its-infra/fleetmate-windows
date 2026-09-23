using System.Text.Json;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.GUI.Views.Projects.Code;
using FleetMate.GUI.Views.Shared;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The Code section: inbox parsing, check normalization across both providers,
/// the wider PR search, and the list's filters and action rules.
/// </summary>
public class CodeSectionTests
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
        Assert.Equal(expected, CodeNotificationRowViewModel.ReasonLabel(reason));

    // MARK: - Search

    [Fact]
    public void Search_AddsInvolvesAndOneQueryPerDistinctOwner()
    {
        var searches = GitHubPullRequestService.CodeSearches(new[] { "acme", "ACME", "", "widgets" });

        Assert.Contains(searches, s => s.Relation == PullRequestRelation.Involved && s.Query.Contains("involves:@me"));

        var owners = searches.Where(s => s.Relation == PullRequestRelation.Organization).ToList();
        Assert.Equal(2, owners.Count);
        Assert.Contains(owners, s => s.Query.Contains("user:acme"));
        Assert.Contains(owners, s => s.Query.Contains("user:widgets"));

        // Aliases must be valid GraphQL identifiers and unique.
        Assert.Equal(searches.Count, searches.Select(s => s.Alias).Distinct().Count());
        Assert.All(searches, s => Assert.Matches("^[A-Za-z_][A-Za-z0-9_]*$", s.Alias));
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

        var result = CodeFilter.Apply(new[] { mine, orgOnly }, CodeSourceFilter.All, CodeScope.Mine, null, null);
        Assert.Single(result);
        Assert.Same(mine, result[0]);

        Assert.Equal(2, CodeFilter.Apply(new[] { mine, orgOnly }, CodeSourceFilter.All, CodeScope.Everything, null, null).Count);
    }

    [Fact]
    public void Filter_SourceRepositoryAndSearchCombine()
    {
        var a = Pr(repo: "fleet", title: "Alpha");
        var b = Pr(repo: "tools", title: "Beta");
        var c = Pr(PullRequestSource.AzureDevOps, repo: "fleet", title: "Alpha two");

        Assert.Equal(new[] { a }, CodeFilter.Apply(new[] { a, b, c }, CodeSourceFilter.GitHub, CodeScope.Everything, "acme/fleet", "alp"));
        Assert.Equal(2, CodeFilter.Apply(new[] { a, b, c }, CodeSourceFilter.All, CodeScope.Everything, null, "ALPHA").Count);
        Assert.Equal(3, CodeFilter.Apply(new[] { a, b, c }, CodeSourceFilter.All, CodeScope.Everything, null, "feature/widget").Count);
        Assert.Equal(2, CodeFilter.Apply(new[] { a, b, c }, CodeSourceFilter.GitHub, CodeScope.Everything, null, "#42").Count);
    }

    [Fact]
    public void Filter_RepositoryCountsBusiestFirst()
    {
        var counts = CodeFilter.RepositoryCounts(new[] { Pr(repo: "b"), Pr(repo: "a"), Pr(repo: "b") });
        Assert.Equal(("acme/b", 2), counts[0]);
        Assert.Equal(("acme/a", 1), counts[1]);
    }

    [Fact]
    public void Row_RelationLabelPrefersReview()
    {
        var row = new CodePullRequestRowViewModel
        {
            PullRequest = Pr(relations: new[] { PullRequestRelation.Involved, PullRequestRelation.AssignedToMe }),
        };
        Assert.Equal("Review", row.RelationLabel);
        Assert.Equal("", new CodePullRequestRowViewModel { PullRequest = Pr(relations: PullRequestRelation.Organization) }.RelationLabel);
    }

    [Fact]
    public void Merge_UnionsRelationsAcrossQueries()
    {
        var queue = new PullRequestQueue();
        queue.Insert(Pr(relations: PullRequestRelation.Organization));
        queue.Insert(Pr(relations: PullRequestRelation.CreatedByMe));

        Assert.Single(queue.PullRequests);
        Assert.True(CodeFilter.MatchesScope(queue.PullRequests[0], CodeScope.Mine));
    }
}
