using System.Text.Json;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Xunit;

namespace FleetMate.Tests;

public class PullRequestQueueMergeTests
{
    private static UnifiedPullRequest Pr(
        PullRequestSource source = PullRequestSource.AzureDevOps,
        int number = 1,
        string container = "Infra",
        string repository = "fleet",
        PullRequestRelation? relation = null,
        DateTime? created = null,
        DateTime? updated = null) => new()
    {
        Source = source,
        Number = number,
        Container = container,
        Repository = repository,
        CreatedAt = created,
        UpdatedAt = updated,
        Relations = relation is { } r ? new HashSet<PullRequestRelation> { r } : new HashSet<PullRequestRelation>(),
    };

    [Fact]
    public void IdentityCombinesSourceContainerRepoAndNumber()
    {
        // Two providers can both have a #1, and so can two repos in one org.
        var ado = Pr(PullRequestSource.AzureDevOps, 1);
        var gh = Pr(PullRequestSource.GitHub, 1);
        var otherRepo = Pr(PullRequestSource.AzureDevOps, 1, repository: "other");

        Assert.NotEqual(ado.Id, gh.Id);
        Assert.NotEqual(ado.Id, otherRepo.Id);
    }

    [Fact]
    public void InsertingTheSamePrTwiceUnionsItsRelations()
    {
        // A PR you opened *and* were asked to review comes back from both
        // queries; it must appear in both sections but only once in the list.
        var queue = new PullRequestQueue();
        queue.Insert(Pr(relation: PullRequestRelation.CreatedByMe));
        queue.Insert(Pr(relation: PullRequestRelation.AssignedToMe));

        Assert.Single(queue.PullRequests);
        Assert.Equal(2, queue.PullRequests[0].Relations.Count);
        Assert.Single(queue.Section(PullRequestRelation.CreatedByMe));
        Assert.Single(queue.Section(PullRequestRelation.AssignedToMe));
    }

    [Fact]
    public void MergeUnionsAcrossProviders()
    {
        var a = new PullRequestQueue();
        a.Insert(Pr(number: 1, relation: PullRequestRelation.CreatedByMe));

        var b = new PullRequestQueue();
        b.Insert(Pr(number: 1, relation: PullRequestRelation.AssignedToMe));
        b.Insert(Pr(number: 2, relation: PullRequestRelation.CreatedByMe));

        a.Merge(b);

        Assert.Equal(2, a.PullRequests.Count);
        Assert.Equal(2, a.PullRequests.Single(p => p.Number == 1).Relations.Count);
    }

    [Fact]
    public void MergeCarriesErrorsThrough()
    {
        // A provider that failed must stay visible — silently dropping it would
        // pass off a partial queue as the whole picture.
        var a = new PullRequestQueue();
        var b = new PullRequestQueue();
        b.Errors.Add(new PullRequestQueueError
        {
            Source = PullRequestSource.GitHub,
            Message = "token expired",
        });

        a.Merge(b);

        Assert.Single(a.Errors);
        Assert.Equal(PullRequestSource.GitHub, a.Errors[0].Source);
    }

    [Fact]
    public void SectionsSortByMostRecentActivity()
    {
        var now = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
        var queue = new PullRequestQueue();

        queue.Insert(Pr(number: 1, relation: PullRequestRelation.CreatedByMe, created: now.AddDays(-5)));
        queue.Insert(Pr(number: 2, relation: PullRequestRelation.CreatedByMe, created: now.AddDays(-1)));
        queue.Insert(Pr(number: 3, relation: PullRequestRelation.CreatedByMe, created: now.AddDays(-9), updated: now));

        var order = queue.Section(PullRequestRelation.CreatedByMe).Select(p => p.Number).ToArray();

        // #3 is oldest but most recently touched, so it leads.
        Assert.Equal(new[] { 3, 2, 1 }, order);
    }

    [Fact]
    public void LastActivityPrefersWhicheverTimestampIsNewer()
    {
        var created = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var updated = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(updated, Pr(created: created, updated: updated).LastActivity);
        Assert.Equal(created, Pr(created: created).LastActivity);
    }

    [Theory]
    // Both providers stamp created and updated within the same second when a PR
    // is opened, so without a floor every new PR claims to be already updated.
    [InlineData(0, false)]
    [InlineData(30, false)]
    [InlineData(61, true)]
    [InlineData(86_400, true)]
    public void UpdatedAfterCreationNeedsMoreThanAMinute(int deltaSeconds, bool expected)
    {
        var created = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var pr = Pr(created: created, updated: created.AddSeconds(deltaSeconds));

        Assert.Equal(expected, pr.WasUpdatedAfterCreation);
    }

    [Fact]
    public void UpdatedAfterCreationIsFalseWhenEitherTimestampIsMissing()
    {
        Assert.False(Pr(created: DateTime.UtcNow).WasUpdatedAfterCreation);
        Assert.False(Pr(updated: DateTime.UtcNow).WasUpdatedAfterCreation);
        Assert.False(Pr().WasUpdatedAfterCreation);
    }

    [Theory]
    [InlineData(PullRequestSource.AzureDevOps, 10716, "!10716")]
    [InlineData(PullRequestSource.GitHub, 42, "#42")]
    public void ReferenceUsesEachProvidersConvention(
        PullRequestSource source, int number, string expected)
    {
        Assert.Equal(expected, Pr(source, number).Reference);
    }
}

public class PullRequestReviewerTests
{
    [Theory]
    [InlineData("Ada Lovelace", "AL")]
    [InlineData("ada.lovelace", "AL")]
    [InlineData("ada-lovelace", "AL")]
    [InlineData("ada_lovelace", "AL")]
    [InlineData("Ada", "A")]
    [InlineData("Ada Byron Lovelace", "AB")]
    [InlineData("", "?")]
    [InlineData("   ", "?")]
    public void InitialsTakeUpToTwoLetters(string displayName, string expected)
    {
        Assert.Equal(expected, new PullRequestReviewer { DisplayName = displayName }.Initials);
    }

    [Theory]
    [InlineData(10, PullRequestReviewVote.Approved)]
    [InlineData(5, PullRequestReviewVote.ApprovedWithSuggestions)]
    [InlineData(0, PullRequestReviewVote.NoVote)]
    [InlineData(-5, PullRequestReviewVote.WaitingForAuthor)]
    [InlineData(-10, PullRequestReviewVote.Rejected)]
    // An unrecognized vote must degrade to NoVote rather than crash the queue.
    [InlineData(7, PullRequestReviewVote.NoVote)]
    [InlineData(null, PullRequestReviewVote.NoVote)]
    public void MapsAzureDevOpsVotes(int? raw, PullRequestReviewVote expected)
    {
        Assert.Equal(expected, PullRequestReviewVoteExtensions.FromAzureDevOps(raw));
    }

    [Theory]
    [InlineData("APPROVED", PullRequestReviewVote.Approved)]
    [InlineData("approved", PullRequestReviewVote.Approved)]
    [InlineData("CHANGES_REQUESTED", PullRequestReviewVote.Rejected)]
    [InlineData("COMMENTED", PullRequestReviewVote.ApprovedWithSuggestions)]
    [InlineData("PENDING", PullRequestReviewVote.NoVote)]
    [InlineData(null, PullRequestReviewVote.NoVote)]
    public void MapsGitHubReviewStates(string? raw, PullRequestReviewVote expected)
    {
        Assert.Equal(expected, PullRequestReviewVoteExtensions.FromGitHub(raw));
    }
}

public class AzureDevOpsPullRequestMappingTests
{
    private static GitPullRequest Wire(
        string? status = "active",
        bool? isDraft = null,
        string? mergeStatus = null,
        string repo = "fleet") => new()
    {
        PullRequestId = 10716,
        Title = "Fix the thing",
        Status = status,
        IsDraft = isDraft,
        MergeStatus = mergeStatus,
        CreatedBy = new IdentityRef { DisplayName = "Ada Lovelace", UniqueName = "ada@example.edu" },
        CreationDate = "2026-07-01T10:00:00Z",
        SourceRefName = "refs/heads/feature/x",
        TargetRefName = "refs/heads/main",
        Repository = new GitRepositoryRef { Name = repo, Project = new DevOpsProjectRef { Name = "Infra" } },
    };

    private static UnifiedPullRequest Map(GitPullRequest pr) =>
        AzureDevOpsService.MapPullRequest(pr, "Infra", PullRequestRelation.CreatedByMe)!;

    [Fact]
    public void MapsTheCoreFields()
    {
        var pr = Map(Wire());

        Assert.Equal(PullRequestSource.AzureDevOps, pr.Source);
        Assert.Equal(10716, pr.Number);
        Assert.Equal("Fix the thing", pr.Title);
        Assert.Equal("Ada Lovelace", pr.AuthorName);
        Assert.Equal("Infra", pr.Container);
        Assert.Equal("fleet", pr.Repository);
    }

    [Theory]
    [InlineData("refs/heads/main", "main")]
    [InlineData("refs/heads/feature/nested/branch", "feature/nested/branch")]
    [InlineData("main", "main")]
    [InlineData(null, "")]
    public void StripsTheRefsHeadsPrefix(string? refName, string expected)
    {
        Assert.Equal(expected, AzureDevOpsService.ShortBranchName(refName));
    }

    [Fact]
    public void DraftWinsOverStatus()
    {
        // Azure DevOps reports a draft PR as "active"; taking status at face
        // value would render every draft as open.
        Assert.Equal(PullRequestState.Draft, Map(Wire(status: "active", isDraft: true)).State);
    }

    [Theory]
    [InlineData("completed", PullRequestState.Merged)]
    [InlineData("abandoned", PullRequestState.Closed)]
    [InlineData("active", PullRequestState.Open)]
    [InlineData("COMPLETED", PullRequestState.Merged)]
    [InlineData(null, PullRequestState.Open)]
    public void MapsStatusToState(string? status, PullRequestState expected)
    {
        Assert.Equal(expected, Map(Wire(status: status)).State);
    }

    [Fact]
    public void DerivesConflictsFromMergeStatus()
    {
        // Azure DevOps has no boolean here — conflicts are a mergeStatus value.
        Assert.True(Map(Wire(mergeStatus: "conflicts")).HasConflicts);
        Assert.False(Map(Wire(mergeStatus: "succeeded")).HasConflicts);
        Assert.False(Map(Wire()).HasConflicts);
    }

    [Fact]
    public void SkipsAPrWithNoRepositoryContext()
    {
        // Without a repo there is no URL to build, and a row nobody can open is
        // worse than no row.
        var orphan = Wire();
        orphan.Repository = null;

        Assert.Null(AzureDevOpsService.MapPullRequest(orphan, "Infra", PullRequestRelation.CreatedByMe));
    }

    [Fact]
    public void FiltersOutGroupReviewers()
    {
        var wire = Wire();
        wire.Reviewers = new List<GitPullRequestReviewer>
        {
            new() { DisplayName = "Ada Lovelace", Vote = 10 },
            new() { DisplayName = "Infra Team", IsContainer = true },
        };

        var reviewers = Map(wire).Reviewers;

        Assert.Single(reviewers);
        Assert.Equal("Ada Lovelace", reviewers[0].DisplayName);
        Assert.Equal(PullRequestReviewVote.Approved, reviewers[0].Vote);
    }

    [Fact]
    public void BuildsAWebUrlUnderTheOrganization()
    {
        var url = AzureDevOpsService.PullRequestWebUrl(
            "https://dev.azure.com/contoso", "Infra", "fleet", 10716);

        Assert.Equal("https://dev.azure.com/contoso/Infra/_git/fleet/pullrequest/10716", url);
    }

    [Fact]
    public void EscapesProjectAndRepositoryInUrls()
    {
        var url = AzureDevOpsService.PullRequestWebUrl(
            "https://dev.azure.com/contoso", "My Project", "my repo", 1);

        Assert.Contains("My%20Project", url);
        Assert.Contains("my%20repo", url);
    }

    [Theory]
    [InlineData("fleet", null, "_apis/git/repositories/fleet/pullrequests/7?api-version=7.0")]
    [InlineData("fleet", "Infra", "Infra/_apis/git/repositories/fleet/pullrequests/7?api-version=7.0")]
    public void BuildsThePullRequestApiPath(string repo, string? project, string expected)
    {
        Assert.Equal(expected, AzureDevOpsService.PullRequestPath(repo, 7, project));
    }
}

public class GitHubPullRequestMappingTests
{
    private static JsonElement Node(string json) => JsonDocument.Parse(json).RootElement;

    private const string FullNode = """
        {
          "number": 42,
          "title": "Add the widget",
          "url": "https://github.com/acme/fleet/pull/42",
          "isDraft": false,
          "state": "OPEN",
          "createdAt": "2026-07-01T10:00:00Z",
          "updatedAt": "2026-07-20T12:00:00Z",
          "mergeable": "MERGEABLE",
          "baseRefName": "main",
          "headRefName": "feature/widget",
          "author": { "login": "ada" },
          "repository": { "name": "fleet", "owner": { "login": "acme" } },
          "comments": { "totalCount": 3 },
          "reviewThreads": { "totalCount": 2 },
          "reviewRequests": { "nodes": [ { "requestedReviewer": { "login": "grace" } } ] },
          "latestReviews": { "nodes": [ { "state": "APPROVED", "author": { "login": "linus" } } ] }
        }
        """;

    private static UnifiedPullRequest Map(string json) =>
        GitHubPullRequestService.Map(Node(json), PullRequestRelation.CreatedByMe)!;

    [Fact]
    public void MapsTheCoreFields()
    {
        var pr = Map(FullNode);

        Assert.Equal(PullRequestSource.GitHub, pr.Source);
        Assert.Equal(42, pr.Number);
        Assert.Equal("Add the widget", pr.Title);
        Assert.Equal("ada", pr.AuthorName);
        Assert.Equal("acme", pr.Container);
        Assert.Equal("fleet", pr.Repository);
        Assert.Equal("main", pr.TargetBranch);
        Assert.Equal("feature/widget", pr.SourceBranch);
    }

    [Fact]
    public void SumsIssueCommentsAndReviewThreads()
    {
        // Neither count alone reflects how much conversation a PR has had.
        Assert.Equal(5, Map(FullNode).CommentCount);
    }

    [Fact]
    public void OverlaysActualReviewsOntoRequestedReviewers()
    {
        var reviewers = Map(FullNode).Reviewers;

        Assert.Equal(2, reviewers.Count);

        var grace = reviewers.Single(r => r.DisplayName == "grace");
        Assert.Equal(PullRequestReviewVote.NoVote, grace.Vote);
        Assert.True(grace.IsRequired);

        var linus = reviewers.Single(r => r.DisplayName == "linus");
        Assert.Equal(PullRequestReviewVote.Approved, linus.Vote);
    }

    [Fact]
    public void AReviewFromARequestedReviewerKeepsRequiredAndGainsTheVote()
    {
        const string json = """
            {
              "number": 1, "url": "https://github.com/acme/fleet/pull/1",
              "repository": { "name": "fleet", "owner": { "login": "acme" } },
              "reviewRequests": { "nodes": [ { "requestedReviewer": { "login": "grace" } } ] },
              "latestReviews": { "nodes": [ { "state": "CHANGES_REQUESTED", "author": { "login": "grace" } } ] }
            }
            """;

        var reviewer = Map(json).Reviewers.Single();

        Assert.Equal("grace", reviewer.DisplayName);
        Assert.Equal(PullRequestReviewVote.Rejected, reviewer.Vote);
        Assert.True(reviewer.IsRequired);
    }

    [Fact]
    public void PrefersATeamsNameOverALogin()
    {
        const string json = """
            {
              "number": 1, "url": "https://github.com/acme/fleet/pull/1",
              "repository": { "name": "fleet", "owner": { "login": "acme" } },
              "reviewRequests": { "nodes": [ { "requestedReviewer": { "name": "Infra Team" } } ] }
            }
            """;

        Assert.Equal("Infra Team", Map(json).Reviewers.Single().DisplayName);
    }

    [Theory]
    [InlineData("\"MERGED\"", false, PullRequestState.Merged)]
    [InlineData("\"CLOSED\"", false, PullRequestState.Closed)]
    [InlineData("\"OPEN\"", false, PullRequestState.Open)]
    [InlineData("\"OPEN\"", true, PullRequestState.Draft)]
    public void MapsState(string state, bool isDraft, PullRequestState expected)
    {
        var json = $$"""
            {
              "number": 1, "url": "https://github.com/acme/fleet/pull/1",
              "state": {{state}}, "isDraft": {{(isDraft ? "true" : "false")}},
              "repository": { "name": "fleet", "owner": { "login": "acme" } }
            }
            """;

        Assert.Equal(expected, Map(json).State);
    }

    [Fact]
    public void DetectsConflicts()
    {
        const string json = """
            {
              "number": 1, "url": "https://github.com/acme/fleet/pull/1",
              "mergeable": "CONFLICTING",
              "repository": { "name": "fleet", "owner": { "login": "acme" } }
            }
            """;

        Assert.True(Map(json).HasConflicts);
    }

    [Theory]
    // search(type: ISSUE) returns issues too; anything without the number and
    // repository pair must be skipped rather than rendered as a dead row.
    [InlineData("""{ "title": "an issue" }""")]
    [InlineData("""{ "number": 1 }""")]
    [InlineData("""{ "number": 1, "repository": { "name": "fleet" } }""")]
    [InlineData("""{ "number": 1, "repository": { "owner": { "login": "acme" } } }""")]
    [InlineData("""{ "number": 1, "url": "u", "repository": {} }""")]
    public void SkipsIncompleteNodes(string json)
    {
        Assert.Null(GitHubPullRequestService.Map(Node(json), PullRequestRelation.CreatedByMe));
    }

    [Fact]
    public void SurvivesMissingOptionalCollections()
    {
        const string json = """
            {
              "number": 1, "url": "https://github.com/acme/fleet/pull/1",
              "repository": { "name": "fleet", "owner": { "login": "acme" } }
            }
            """;

        var pr = Map(json);

        Assert.Empty(pr.Reviewers);
        Assert.Equal(0, pr.CommentCount);
        Assert.Equal("(untitled)", pr.Title);
        Assert.Equal("Unknown", pr.AuthorName);
    }
}

public class GitHubSearchQueryTests
{
    [Fact]
    public void ScopesEveryQueryToOpenUnarchivedPullRequests()
    {
        var (created, assigned, review) = GitHubPullRequestService.SearchQueries(includeDrafts: true);

        foreach (var q in new[] { created, assigned, review })
        {
            Assert.Contains("is:pr", q);
            Assert.Contains("is:open", q);
            Assert.Contains("archived:false", q);
        }
    }

    [Fact]
    public void UsesTheServerSideViewerAlias()
    {
        // @me resolves to the token's owner, so no viewer lookup round trip.
        var (created, assigned, review) = GitHubPullRequestService.SearchQueries(includeDrafts: true);

        Assert.Contains("author:@me", created);
        Assert.Contains("assignee:@me", assigned);
        Assert.Contains("review-requested:@me", review);
    }

    [Fact]
    public void ExcludingDraftsOnlyAppliesToYourOwnPullRequests()
    {
        // A draft someone assigned to you is still your problem; a draft you
        // opened yourself is noise in a review queue.
        var (created, assigned, review) = GitHubPullRequestService.SearchQueries(includeDrafts: false);

        Assert.Contains("-is:draft", created);
        Assert.DoesNotContain("-is:draft", assigned);
        Assert.DoesNotContain("-is:draft", review);
    }
}

public class PullRequestDateParsingTests
{
    [Theory]
    // Azure DevOps and GitHub both emit timestamps with and without fractional
    // seconds depending on the endpoint.
    [InlineData("2026-07-01T10:00:00Z")]
    [InlineData("2026-07-01T10:00:00.123Z")]
    [InlineData("2026-07-01T10:00:00.1234567Z")]
    [InlineData("2026-07-01T10:00:00+00:00")]
    public void ParsesBothIso8601Shapes(string value)
    {
        var parsed = PullRequestDateParser.Parse(value);

        Assert.NotNull(parsed);
        Assert.Equal(2026, parsed!.Value.Year);
        Assert.Equal(7, parsed.Value.Month);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    public void ReturnsNullRatherThanThrowing(string? value)
    {
        Assert.Null(PullRequestDateParser.Parse(value));
    }

    [Fact]
    public void NormalizesToUtc()
    {
        var parsed = PullRequestDateParser.Parse("2026-07-01T12:00:00+02:00");

        Assert.NotNull(parsed);
        Assert.Equal(10, parsed!.Value.Hour);
    }
}
