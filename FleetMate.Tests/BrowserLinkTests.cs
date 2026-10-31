using FleetMate.Core.Links;
using Xunit;

namespace FleetMate.Tests;

/// <summary>"Open in Browser" marks Azure DevOps pages so a link extension leaves them in the browser.</summary>
public class BrowserLinkTests
{
    [Fact]
    public void AzureDevOpsPagesCarryTheOptOut()
    {
        const string url = "https://devops.example.invalid/example-org/Project/_git/Repo/pullrequest/12?_a=files";
        var marked = BrowserLink.ForBrowser(url);
        Assert.Equal(url + "&fleetmate=browser", marked);
        Assert.Equal(marked, BrowserLink.ForBrowser(marked));
        Assert.Equal("?fleetmate=browser",
            BrowserLink.ForBrowser(new Uri("https://example-org.devops.example.invalid/P/_workitems/edit/3")).Query);
        Assert.Equal("https://devops.example.invalid/o/P/_build/results?buildId=7&fleetmate=browser",
            BrowserLink.ForBrowser("https://devops.example.invalid/o/P/_build/results?buildId=7"));
        Assert.Equal("https://devops.example.invalid/o/P/_build?definitionId=4&fleetmate=browser",
            BrowserLink.ForBrowser("https://devops.example.invalid/o/P/_build?definitionId=4"));
        Assert.Equal("https://devops.example.invalid:8443/o/P/_git/R?fleetmate=browser",
            BrowserLink.ForBrowser("https://devops.example.invalid:8443/o/P/_git/R"));
    }

    [Fact]
    public void AMarkedPageStillParsesAsTheSameLink()
    {
        var marked = BrowserLink.ForBrowser(new Uri("https://devops.example.invalid/o/P/_git/R/pullrequest/12"));
        Assert.Equal(new FleetMateLink.PullRequest(new FleetMateLink.AzureDevOps("P", "R"), 12), FleetMateLink.ParseWeb(marked));
    }

    [Theory]
    [InlineData("https://github.com/owner/repo/pull/1")]
    [InlineData("http://devops.example.invalid/example-org/P/_workitems/edit/3")]
    [InlineData("https://example.com/docs/page")]
    [InlineData("not a url")]
    public void OtherPagesAreUntouched(string url) =>
        Assert.Equal(url, BrowserLink.ForBrowser(url));
}
