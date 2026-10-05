using FleetMate.Core.Links;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Identity;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Search;
using Xunit;

namespace FleetMate.Tests;

public class SearchEverythingTests
{
    private const string Sha = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";

    private static SearchSources Sources() => new()
    {
        Devices = new[] { new IntuneDevice { Id = "dev-1", DeviceName = "DEVICE-ONE", SerialNumber = "SN1" } },
        Assets = new[] { new SnipeAsset { Id = 42, Name = "Sample Laptop", AssetTag = "T0042" } },
        Tickets = new[] { new TdxTicket { Id = 5150, Title = "Printer jam" } },
        WorkItems = new[] { new WorkItem { Id = 5558, Fields = new WorkItemFields { Title = "Rotate keys" } } },
        Users = new[] { new EntraUser { Id = "u1", DisplayName = "Pat Doe", UserPrincipalName = "pdoe@example.edu" } },
        Groups = new[] { new EntraGroup { Id = "g1", DisplayName = "Lab Admins" } },
        PullRequests = new[]
        {
            new UnifiedPullRequest
            {
                Source = PullRequestSource.AzureDevOps, Number = 27391, Title = "Remove the old report",
                Container = "Proj", Repository = "Repo", AuthorName = "Pat Doe", SourceBranch = "fix/old-report"
            },
            new UnifiedPullRequest
            {
                Source = PullRequestSource.GitHub, Number = 7, Title = "Tidy the build",
                Container = "octo", Repository = "tools", AuthorName = "Sam Roe", SourceBranch = "chore/build"
            },
        },
        Notifications = new[]
        {
            new GitHubNotification
            {
                Id = "n1", SubjectType = "Issue", SubjectTitle = "Crash on launch",
                WebUrl = "https://github.com/octo/tools/issues/31", Repository = "octo/tools"
            },
            new GitHubNotification
            {
                Id = "n2", SubjectType = "PullRequest", SubjectTitle = "Crash fix PR",
                WebUrl = "https://github.com/octo/tools/pull/32", Repository = "octo/tools"
            },
        },
        Commits = new[]
        {
            new RepositoryCommits
            {
                Source = PullRequestSource.AzureDevOps, Container = "Proj", Repository = "Repo",
                Commits = { new PullRequestCommit { Id = Sha, Message = "Pin the agent version\n\nBody", AuthorName = "Pat Doe" } }
            },
        },
        Runs = new[]
        {
            new PipelineRun
            {
                Source = PullRequestSource.AzureDevOps, Container = "Proj", PipelineName = "nightly",
                RunId = 23249, RunNumber = "20261004.3", Branch = "main"
            },
            new PipelineRun
            {
                Source = PullRequestSource.GitHub, Container = "octo", Repository = "tools", PipelineName = "ci",
                RunId = 900001, RunNumber = "88", Branch = "feature/x"
            },
        },
    };

    private static SearchGroup Group(List<SearchGroup> groups, SearchCategory category) =>
        Assert.Single(groups, g => g.Category == category);

    [Theory]
    [InlineData("27391")]
    [InlineData("!27391")]
    [InlineData("#27391")]
    public void PullRequests_MatchByNumberInEveryForm(string query)
    {
        var hit = Assert.Single(Group(GlobalSearch.Search(query, Sources()), SearchCategory.PullRequests).Hits);
        Assert.Equal("Number: 27391", hit.MatchLabel);
        Assert.Equal("fleetmate://pull/Proj/Repo/27391", hit.Link);
    }

    [Theory]
    [InlineData("old report", "Title")]
    [InlineData("Proj/Repo", "Repo")]
    [InlineData("Sam Roe", "Author")]
    [InlineData("chore/build", "Branch")]
    public void PullRequests_MatchByTitleRepoAuthorOrBranch(string query, string label)
    {
        var hit = Assert.Single(Group(GlobalSearch.Search(query, Sources()), SearchCategory.PullRequests).Hits);
        Assert.StartsWith(label + ":", hit.MatchLabel);
    }

    [Fact]
    public void Issues_MatchByNumberOrTitle_AndSkipOtherNotifications()
    {
        var byNumber = Assert.Single(Group(GlobalSearch.Search("#31", Sources()), SearchCategory.Issues).Hits);
        Assert.Equal("fleetmate://issue/github/octo/tools/31", byNumber.Link);

        var byTitle = Group(GlobalSearch.Search("crash", Sources()), SearchCategory.Issues);
        Assert.Equal(1, byTitle.Total);
    }

    [Fact]
    public void Commits_MatchBySevenCharacterShaPrefix()
    {
        var hit = Assert.Single(Group(GlobalSearch.Search(Sha[..7], Sources()), SearchCategory.Commits).Hits);
        Assert.Equal($"fleetmate://commit/Proj/Repo/{Sha}", hit.Link);
        Assert.StartsWith("SHA:", hit.MatchLabel);
    }

    [Fact]
    public void Commits_ShaPrefixShorterThanSevenIsNotASha()
    {
        var groups = GlobalSearch.Search(Sha[..6], Sources());
        Assert.DoesNotContain(groups, g => g.Category == SearchCategory.Commits);
    }

    [Fact]
    public void Commits_MatchByMessageOrAuthor()
    {
        Assert.Single(Group(GlobalSearch.Search("agent version", Sources()), SearchCategory.Commits).Hits);
        var byAuthor = GlobalSearch.Search("Pat Doe", Sources());
        Assert.Single(Group(byAuthor, SearchCategory.Commits).Hits);
    }

    [Fact]
    public void PipelineRuns_MatchRunIdExactly_AndRunNumberAsText()
    {
        var byId = Assert.Single(Group(GlobalSearch.Search("23249", Sources()), SearchCategory.PipelineRuns).Hits);
        Assert.Equal("Run: 23249", byId.MatchLabel);
        Assert.Equal("fleetmate://pipeline/Proj/23249", byId.Link);

        var byNumber = Assert.Single(Group(GlobalSearch.Search("20261004.3", Sources()), SearchCategory.PipelineRuns).Hits);
        Assert.StartsWith("Run number:", byNumber.MatchLabel);

        var github = Assert.Single(Group(GlobalSearch.Search("feature/x", Sources()), SearchCategory.PipelineRuns).Hits);
        Assert.Equal("fleetmate://pipeline/github/octo/tools/900001", github.Link);
    }

    [Theory]
    [InlineData("DEVICE-ONE", SearchCategory.Devices, "fleetmate://device/dev-1")]
    [InlineData("T0042", SearchCategory.Inventory, "fleetmate://asset/42")]
    [InlineData("Printer jam", SearchCategory.Tickets, "fleetmate://ticket/5150")]
    [InlineData("Rotate keys", SearchCategory.WorkItems, "fleetmate://workitem/5558")]
    [InlineData("pdoe@example.edu", SearchCategory.Users, "fleetmate://user/u1")]
    [InlineData("Lab Admins", SearchCategory.Groups, "fleetmate://group/g1")]
    public void ExistingCategories_CarryLinks(string query, SearchCategory category, string link) =>
        Assert.Equal(link, Group(GlobalSearch.Search(query, Sources()), category).Hits[0].Link);

    [Theory]
    [InlineData("pat")]
    [InlineData("27391")]
    [InlineData("tools")]
    [InlineData("a1b2c3d")]
    [InlineData("main")]
    public void EveryHitsLink_RoundTripsThroughTheRouter(string query)
    {
        foreach (var hit in GlobalSearch.Search(query, Sources()).SelectMany(g => g.Hits))
        {
            Assert.False(string.IsNullOrEmpty(hit.Link), $"{hit.Category} {hit.Title} has no link");
            var parsed = FleetMateLink.TryParse(hit.Link, out var error);
            Assert.True(parsed != null, error?.Message);
            Assert.Equal(hit.Link, parsed!.ToLink());
        }
    }

    [Fact]
    public void PrependedWorkItem_CarriesItsLink()
    {
        var groups = GlobalSearch.Search("5558", new SearchSources());
        GlobalSearch.PrependWorkItem(groups, new WorkItem { Id = 999, Fields = new WorkItemFields { Title = "Fetched" } });
        Assert.Equal("fleetmate://workitem/999", groups[0].Hits[0].Link);
    }

    [Theory]
    [InlineData("fleetmate://device/abc-123")]
    [InlineData("fleetmate://asset/42")]
    [InlineData("fleetmate://ticket/5150")]
    [InlineData("fleetmate://user/u1")]
    [InlineData("fleetmate://group/g1")]
    public void NewRoutes_RoundTrip(string link) =>
        Assert.Equal(link, FleetMateLink.Parse(link).ToLink());

    [Theory]
    [InlineData("fleetmate://asset/x")]
    [InlineData("fleetmate://ticket/")]
    [InlineData("fleetmate://device")]
    public void NewRoutes_RejectMalformed(string link) =>
        Assert.Throws<FleetMateLinkException>(() => FleetMateLink.Parse(link));
    [Theory]
    [InlineData("27391")]
    [InlineData("#27391")]
    [InlineData("!27391")]
    public void ExactNumberMatchLeadsAheadOfSubstringMatchesInEarlierCategories(string query)
    {
        var sources = Sources();
        var withSerial = new SearchSources
        {
            Devices = new[] { new IntuneDevice { Id = "dev-9", DeviceName = "LAB-9", SerialNumber = "X273919Z" } },
            PullRequests = sources.PullRequests,
        };

        var groups = GlobalSearch.Search(query, withSerial);

        Assert.Equal(SearchCategory.PullRequests, groups[0].Category);
        Assert.Equal("27391", groups[0].Hits[0].Key);
    }
}

public class UserLinkTests
{
    [Theory]
    [InlineData("fleetmate://user/pdoe@example.edu", "pdoe@example.edu")]
    [InlineData("fleetmate://user/0f1e2d3c-0000-4000-8000-000000000001", "0f1e2d3c-0000-4000-8000-000000000001")]
    public void UserLinkAcceptsAnIdOrAUpnAndRoundTrips(string link, string id)
    {
        var parsed = Assert.IsType<FleetMate.Core.Links.FleetMateLink.User>(FleetMate.Core.Links.FleetMateLink.TryParse(link, out _));
        Xunit.Assert.Equal(id, parsed.Id);
        var again = Assert.IsType<FleetMate.Core.Links.FleetMateLink.User>(FleetMate.Core.Links.FleetMateLink.TryParse(parsed.ToLink(), out _));
        Xunit.Assert.Equal(id, again.Id);
    }
}
