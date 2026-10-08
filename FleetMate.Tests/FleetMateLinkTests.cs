using FleetMate.Core.Links;
using Xunit;
using L = FleetMate.Core.Links.FleetMateLink;

namespace FleetMate.Tests;

public class FleetMateLinkTests
{
    private const string Sha = "a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6a7b8c9d0";

    [Fact]
    public void ParsesAzureDevOpsPullRequest() =>
        Assert.Equal(new L.PullRequest(new L.AzureDevOps("Proj", "Repo"), 27391), L.Parse("fleetmate://pull/Proj/Repo/27391"));

    [Fact]
    public void ParsesWorkItem() =>
        Assert.Equal(new L.WorkItem(1234), L.Parse("fleetmate://workitem/1234"));

    [Fact]
    public void ParsesAzureDevOpsRun() =>
        Assert.Equal(new L.AzureDevOpsRun("Proj", 4242), L.Parse("fleetmate://pipeline/Proj/4242"));

    [Fact]
    public void ParsesWithoutSlashesAndInQuotes()
    {
        Assert.Equal(new L.WorkItem(1234), L.Parse("fleetmate:workitem/1234"));
        Assert.Equal(new L.WorkItem(1234), L.Parse("\"FLEETMATE://workitem/1234/\""));
    }

    [Theory]
    [InlineData("fleetmate://pull/github/octo/app/42")]
    [InlineData("fleetmate://issue/github/octo/app/7")]
    [InlineData("fleetmate://pipeline/github/octo/app/123456789")]
    [InlineData("fleetmate://pipeline/Proj/definition/12")]
    [InlineData("fleetmate://commit/Proj/Repo/abc1234")]
    [InlineData("fleetmate://commit/github/octo/app/" + Sha)]
    [InlineData("fleetmate://pull/My%20Project/My%20Repo/9")]
    public void RoundTrips(string link) =>
        Assert.Equal(L.Parse(link), L.Parse(L.Parse(link).ToLink()));

    [Fact]
    public void ParsesGitHubAndDefinitionRoutes()
    {
        Assert.Equal(new L.PullRequest(new L.GitHub("octo", "app"), 42), L.Parse("fleetmate://pull/github/octo/app/42"));
        Assert.Equal(new L.GitHubIssue("octo", "app", 7), L.Parse("fleetmate://issue/github/octo/app/7"));
        Assert.Equal(new L.GitHubRun("octo", "app", 99), L.Parse("fleetmate://pipeline/github/octo/app/99"));
        Assert.Equal(new L.AzureDevOpsPipeline("Proj", 12), L.Parse("fleetmate://pipeline/Proj/definition/12"));
        Assert.Equal("fleetmate://pull/My%20Project/Repo/9", new L.PullRequest(new L.AzureDevOps("My Project", "Repo"), 9).ToLink());
    }

    [Theory]
    [InlineData("abc123", false)]
    [InlineData("abc1234", true)]
    [InlineData(Sha, true)]
    [InlineData(Sha + "a", false)]
    [InlineData("abc123g", false)]
    public void ShaIsSevenToFortyHex(string sha, bool ok) => Assert.Equal(ok, L.IsSha(sha));

    [Fact]
    public void RejectsShortAndLongShasInLinks()
    {
        Assert.Throws<FleetMateLinkException>(() => L.Parse("fleetmate://commit/Proj/Repo/abc123"));
        Assert.Throws<FleetMateLinkException>(() => L.Parse("fleetmate://commit/Proj/Repo/" + Sha + "a"));
    }

    [Theory]
    [InlineData("https://dev.example.com/org/Proj/_git/Repo/pullrequest/27391")]
    [InlineData("https://org.example.net/Proj/_git/Repo/pullrequest/27391")]
    [InlineData("https://tfs.example.org/tfs/Collection/Proj/_git/Repo/pullrequest/27391?_a=overview")]
    public void OpenMatchesAzureDevOpsPullRequestsByPathShapeOnAnyHost(string web) =>
        Assert.Equal(new L.PullRequest(new L.AzureDevOps("Proj", "Repo"), 27391),
            L.Parse("fleetmate://open?url=" + Uri.EscapeDataString(web)));

    [Fact]
    public void OpenMatchesOtherAzureDevOpsShapes()
    {
        Assert.Equal(new L.AzureDevOpsRun("Proj", 4242),
            L.ParseWeb(new Uri("https://dev.example.com/org/Proj/_build/results?buildId=4242&view=logs")));
        Assert.Equal(new L.AzureDevOpsPipeline("Proj", 12),
            L.ParseWeb(new Uri("https://dev.example.com/org/Proj/_build?definitionId=12")));
        Assert.Equal(new L.WorkItem(1234),
            L.ParseWeb(new Uri("https://dev.example.com/org/Proj/_workitems/edit/1234/")));
        Assert.Equal(new L.Commit(new L.AzureDevOps("Proj", "Repo"), "abc1234"),
            L.ParseWeb(new Uri("https://dev.example.com/org/Proj/_git/Repo/commit/abc1234")));
    }

    [Fact]
    public void OpenMatchesGitHubShapes()
    {
        Assert.Equal(new L.PullRequest(new L.GitHub("octo", "app"), 42), L.ParseWeb(new Uri("https://github.com/octo/app/pull/42/files")));
        Assert.Equal(new L.GitHubIssue("octo", "app", 7), L.ParseWeb(new Uri("https://github.com/octo/app/issues/7")));
        Assert.Equal(new L.GitHubRun("octo", "app", 99), L.ParseWeb(new Uri("https://github.com/octo/app/actions/runs/99")));
        Assert.Equal(new L.Commit(new L.GitHub("octo", "app"), Sha), L.ParseWeb(new Uri($"https://github.com/octo/app/commit/{Sha}")));
    }

    [Theory]
    [InlineData("fleetmate://pull/Proj/Repo", FleetMateLinkError.Malformed)]
    [InlineData("fleetmate://pull/Proj/Repo/abc", FleetMateLinkError.Malformed)]
    [InlineData("fleetmate://workitem/", FleetMateLinkError.Malformed)]
    [InlineData("fleetmate://issue/Proj/Repo/1", FleetMateLinkError.Malformed)]
    [InlineData("fleetmate://open?url=", FleetMateLinkError.Malformed)]
    [InlineData("fleetmate://open?url=https%3A%2F%2Fexample.com%2Fnothing", FleetMateLinkError.UnsupportedWebUrl)]
    [InlineData("fleetmate://teleport/1", FleetMateLinkError.UnknownRoute)]
    [InlineData("https://example.com/x", FleetMateLinkError.NotFleetMate)]
    public void MalformedLinksSayWhy(string link, FleetMateLinkError kind)
    {
        var error = Assert.Throws<FleetMateLinkException>(() => L.Parse(link));
        Assert.Equal(kind, error.Kind);
        if (kind == FleetMateLinkError.Malformed) Assert.StartsWith("fleetmate://", error.Expected);
    }

    [Fact]
    public void MalformedMessageNamesTheExpectedForm()
    {
        Assert.Null(L.TryParse("fleetmate://workitem/x", out var error));
        Assert.Contains("fleetmate://workitem/<id>", error!.Message);
    }
}
