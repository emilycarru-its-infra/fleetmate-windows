using FleetMate.Core.Knowledge;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Shared;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The Development Graphs cards both apps draw: which cards each sub-tab
/// shows, what every click opens, and the loading, empty and all-clear states.
/// </summary>
public class DevelopmentGraphCardsTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Local);

    private static UnifiedPullRequest Pr(string repo, PullRequestSource source = PullRequestSource.GitHub, params PullRequestRelation[] relations) => new()
    {
        Number = 1, Container = "example", Repository = repo, Source = source, Title = $"Change {repo}",
        AuthorName = "Ada", CreatedAt = Now.AddDays(-2), Relations = relations.ToHashSet(),
    };

    private static GraphCard Card(IEnumerable<GraphCard> cards, string title) => cards.Single(c => c.Title == title);

    [Fact]
    public void PullsShowEveryCardInOrder()
    {
        var cards = DevelopmentGraphCards.Pulls(new PullRequestQueue { PullRequests = { Pr("alpha") } }, loading: false, Now);
        Assert.Equal(new[] { "", "", "Opened per Week", "Review Status", "By Age", "By Repository", "By Author", "Most Discussed" },
            cards.Select(c => c.Title));
        Assert.Equal(2, Card(cards, "Opened per Week").Units);
        Assert.Equal(12, Card(cards, "Opened per Week").Columns.Count);
    }

    [Fact]
    public void PullKpisCountAndOpenTheirSource()
    {
        var queue = new PullRequestQueue
        {
            PullRequests =
            {
                Pr("alpha", PullRequestSource.GitHub, PullRequestRelation.AssignedToMe),
                Pr("beta", PullRequestSource.AzureDevOps, PullRequestRelation.CreatedByMe),
                Pr("gamma", PullRequestSource.AzureDevOps),
            },
        };
        var kpis = DevelopmentGraphCards.Pulls(queue, false, Now).Where(c => c.Kind == GraphCardKind.Kpis).SelectMany(c => c.Kpis).ToList();

        Assert.Equal(new[] { "1", "1", "2", "1" }, kpis.Select(k => k.Value));
        Assert.Equal(new GraphFilter(GraphCategory.Source, "DevOps"), kpis[2].Open);
        Assert.Equal(new GraphFilter(GraphCategory.Source, "GitHub"), kpis[3].Open);
    }

    [Fact]
    public void RepositoryBarsOpenThatRepositoryAndNameTheirCount()
    {
        var queue = new PullRequestQueue { PullRequests = { Pr("alpha"), Pr("alpha"), Pr("beta") } };
        var bar = Card(DevelopmentGraphCards.Pulls(queue, false, Now), "By Repository").Bars[0];

        Assert.Equal("example/alpha", bar.Label);
        Assert.Null(bar.Tone);
        Assert.Equal(new GraphFilter(GraphCategory.Repository, "example/alpha"), bar.Open);
        Assert.Equal("example/alpha, 2 open pull requests", bar.Description);
    }

    [Fact]
    public void MostDiscussedOpensThePullRequestById()
    {
        var pr = Pr("alpha");
        pr.CommentCount = 3;
        var bar = Assert.Single(Card(DevelopmentGraphCards.Pulls(new PullRequestQueue { PullRequests = { pr } }, false, Now), "Most Discussed").Bars);
        Assert.Equal(new GraphFilter(GraphCategory.PullRequest, pr.Id), bar.Open);
        Assert.Equal("Change alpha, 3 comments", bar.Description);
    }

    [Fact]
    public void ChartsLoadThenSayTheyAreEmpty()
    {
        var loading = Card(DevelopmentGraphCards.Pulls(null, loading: true, Now), "By Repository");
        Assert.Equal(GraphCardBody.Loading, loading.Body);
        Assert.True(DevelopmentGraphCards.Pulls(null, true, Now)[0].Kpis[0].Loading);

        var empty = Card(DevelopmentGraphCards.Pulls(null, loading: false, Now), "By Repository");
        Assert.Equal(GraphCardBody.Empty, empty.Body);
        Assert.Equal("No open pull requests", empty.Message);
        Assert.Equal("By Repository: No open pull requests", empty.Description);
    }

    [Fact]
    public void AFailedProviderReadsUnknown()
    {
        var queue = new PullRequestQueue { Errors = { new PullRequestQueueError { Source = PullRequestSource.GitHub, Message = "API rate limit exceeded" } } };
        var kpis = DevelopmentGraphCards.Pulls(queue, false, Now).SelectMany(c => c.Kpis).ToList();
        Assert.Equal("--", kpis.Single(k => k.Title.StartsWith(PullRequestSource.GitHub.ShortName())).Value);
        Assert.Equal("0", kpis.Single(k => k.Title.StartsWith(PullRequestSource.AzureDevOps.ShortName())).Value);
    }

    [Fact]
    public void CommitCardsOpenCommitsForTheRepository()
    {
        var repo = new RepositoryCommits
        {
            Container = "example", Repository = "alpha", Source = PullRequestSource.GitHub,
            Commits = new() { new PullRequestCommit { Date = Now.AddHours(-3), AuthorName = "Ada" }, new PullRequestCommit { Date = Now, AuthorName = "ada" } },
        };
        var cards = DevelopmentGraphCards.Commits(new[] { repo }, false, Now);

        Assert.Equal(new[] { "2", "1", "1" }, cards[0].Kpis.Select(k => k.Value));
        Assert.Equal(GraphCategory.CommitRepository, Card(cards, "By Repository").Bars[0].Open!.Category);
        Assert.Null(Card(cards, "By Weekday").Bars[0].Open);
        var row = Assert.Single(Card(cards, "Recently Active").Rows);
        Assert.Equal("just now", row.Detail);
        Assert.Equal(new GraphFilter(GraphCategory.CommitRepository, repo.DisplayName), row.Open);
        Assert.Equal(14, Card(cards, "Commits per Day").Columns.Count);
        Assert.Contains("2 commits", Card(cards, "Commits per Day").Columns[^1].Description);
    }

    [Fact]
    public void PipelineOutcomesOpenOnlyTheStatusesThatHaveAFilter()
    {
        PipelineRun Run(PipelineRunStatus s) => new() { PipelineName = "build", Status = s, StartedAt = Now, FinishedAt = Now.AddMinutes(5) };
        var cards = DevelopmentGraphCards.Pipelines(new[] { Run(PipelineRunStatus.Succeeded), Run(PipelineRunStatus.Cancelled) }, false, Now);
        var outcomes = Card(cards, "Outcomes").Bars;

        Assert.Equal(new GraphFilter(GraphCategory.PipelineStatus, "Succeeded"), outcomes.Single(b => b.Label == "Succeeded").Open);
        Assert.Null(outcomes.Single(b => b.Label == "Cancelled").Open);
        Assert.Equal("100%", cards[0].Kpis[2].Value);

        var failures = Card(cards, "Failures by Pipeline");
        Assert.Equal(GraphCardBody.AllClear, failures.Body);
        Assert.Equal("2 runs in 7 days", failures.Detail);
        Assert.True(Card(cards, "Runs per Day").Legend);
    }

    [Fact]
    public void SkillsCountEachKind()
    {
        SkillEntry Entry(SkillKind kind, string name) => new(kind, SkillOrigin.Shared, name, "", name, new[] { "run.ps1" }, "one\ntwo");
        var cards = DevelopmentGraphCards.Skills(new[] { Entry(SkillKind.Skill, "a"), Entry(SkillKind.Hook, "b") }, syncing: false);

        Assert.Equal(new[] { "1", "1", "0" }, cards[0].Kpis.Select(k => k.Value));
        Assert.Equal(".ps1, 2 files", Card(cards, "Bundled Scripts").Bars[0].Description);
        Assert.Equal(GraphCardBody.Chart, Card(cards, "Hooks · lines").Body);
    }

    [Theory]
    [InlineData(DevelopmentSegment.Pulls, "PullRequests", true)]
    [InlineData(DevelopmentSegment.Pulls, "Runs", false)]
    [InlineData(DevelopmentSegment.Commits, "Commits", true)]
    [InlineData(DevelopmentSegment.Pipelines, "Runs", true)]
    [InlineData(DevelopmentSegment.Repos, "Commits", false)]
    public void EachSegmentFollowsItsOwnCache(DevelopmentSegment segment, string key, bool follows) =>
        Assert.Equal(follows, DevelopmentGraphCards.Follows(segment, key));

    [Fact]
    public void FilterValuesDropATrailingCount() =>
        Assert.Equal(new GraphFilter(GraphCategory.Search, "Active"), GraphFilter.For(GraphCategory.Search, "Active (4)"));

    [Fact]
    public void RowPackingFitsUnitsOfTheMinimumWidth()
    {
        Assert.Equal(1, WidgetRowPacking.UnitsPerRow(100));
        Assert.Equal(3, WidgetRowPacking.UnitsPerRow(3 * WidgetRowPacking.MinUnitWidth + 2 * WidgetRowPacking.Gap));
    }
}
