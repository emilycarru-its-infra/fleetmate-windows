using FleetMate.Core.Knowledge;
using Xunit;

namespace FleetMate.Tests;

public class HandbookIndexTests
{
    private static HandbookPage P(string path, string text) =>
        HandbookIndex.Page(path, text) ?? throw new InvalidOperationException(path);

    private static HandbookIndex Sample() => new(new[]
    {
        P("devices/laptops/laptop-pro.md", "---\ntitle: \"Laptop Pro setup\"\n---\n# Imaging\nSteps for the Laptop Pro."),
        P("devices/platforms/windows/_index.md", "---\ntitle: Windows\n---\n## Enrollment\nHow Windows devices enrol."),
        P("devices/management/example-mdm.md", "---\ntitle: Example MDM\nslug: mdm\n---\n## Laptop Pro notes\nBody."),
        P("fleets/studio-loaners.md", "---\ntitle: Studio Loaners fleet\n---\nWho borrows what."),
        P("misc/long-page.md", "---\ntitle: Unrelated\n---\nA passing mention of Windows and Laptop Pro in the body only."),
    });

    [Fact]
    public void Page_ReadsFrontMatterSlugSitePathHeadingsAndShortcodes()
    {
        var page = P("devices/management/example-mdm.md",
            "---\r\ntitle: 'Example MDM'\r\nslug: mdm\r\nlastmod: 2026-01-02\r\n---\r\n# Top {{< note >}}\r\nText");

        Assert.Equal("Example MDM", page.Title);
        Assert.Equal("/devices/management/mdm/", page.SitePath);
        Assert.Equal(new[] { "devices", "management" }, page.Sections);
        Assert.Equal("Devices › Management", page.Breadcrumb);
        Assert.Equal("2026-01-02", page.LastModified);
        Assert.Equal(new[] { "Top" }, page.Headings);
        Assert.DoesNotContain("{{<", page.Body);
    }

    [Fact]
    public void Page_IndexFileUsesFolderPath_AndTitleLessStubsAreSkipped()
    {
        Assert.Equal("/devices/platforms/windows/", P("devices/platforms/windows/_index.md", "---\ntitle: W\n---\n").SitePath);
        Assert.Equal("/", P("_index.md", "---\ntitle: Home\n---\n").SitePath);
        Assert.Null(HandbookIndex.Page("devices/separator.md", "---\nweight: 3\n---\n"));
    }

    [Fact]
    public void Facets_UseModelFamilyAndDropShortOrEmptyValues()
    {
        var facets = HandbookAssetFacets.For("Laptop Pro (14-inch, 2023)", "Laptops", "Windows", "", "IT");
        Assert.Equal(new[] { "Model family", "Category", "Platform" }, facets.Select(f => f.Label));
        Assert.Equal("Laptop Pro", facets[0].Term);
    }

    [Fact]
    public void Related_NeedsATitleHeadingOrPathHit_AndRanksPagesTouchingMoreTerms()
    {
        var pages = Sample().Related(new[] { "Laptop Pro", "Windows", "Example MDM" });

        Assert.DoesNotContain(pages, p => p.Title == "Unrelated");
        // Example MDM matches two terms (its title and a "Laptop Pro" heading), so it leads.
        Assert.Equal("Example MDM", pages[0].Title);
    }

    [Fact]
    public void RelatedByFacet_GroupsEachPageUnderTheFirstFacetItMatches()
    {
        var facets = HandbookAssetFacets.For("Laptop Pro (2023)", null, "Windows", "Example MDM", "Studio Loaners");
        var groups = Sample().RelatedByFacet(facets);

        Assert.Equal(new[] { "Model family", "Platform", "Fleet" }, groups.Select(g => g.Label));
        var family = groups.Single(g => g.Label == "Model family").Pages.Select(p => p.Title).ToList();
        Assert.Contains("Laptop Pro setup", family);
        Assert.Contains("Example MDM", family); // matched the family first, so it isn't repeated under Management service
        Assert.Equal("Windows", groups.Single(g => g.Label == "Platform").Pages.Single().Title);
        Assert.Equal("Studio Loaners fleet", groups.Single(g => g.Label == "Fleet").Pages.Single().Title);
    }

    [Fact]
    public void RelatedByFacet_EmptyIndexOrNoFacetsGivesNothing()
    {
        Assert.Empty(HandbookIndex.Empty.RelatedByFacet(HandbookAssetFacets.For("Laptop Pro", null, null, null, null)));
        Assert.Empty(Sample().RelatedByFacet(Array.Empty<HandbookFacet>()));
    }

    [Theory]
    [InlineData("https://docs.example.edu", "/devices/management/mdm/", "https://docs.example.edu/devices/management/mdm/")]
    [InlineData("https://docs.example.edu/handbook/", "/fleets/", "https://docs.example.edu/handbook/fleets/")]
    public void SiteUrl_JoinsTheSiteAndThePagePath(string site, string sitePath, string expected)
    {
        var page = new HandbookPage("x.md", "X", Array.Empty<string>(), sitePath, Array.Empty<string>(), "", null, null);
        Assert.Equal(expected, HandbookSite.PageUrl(site, page)!.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file:///c:/x")]
    public void SiteUrl_IsNullWithoutAnHttpSite(string? site)
    {
        var page = new HandbookPage("x.md", "X", Array.Empty<string>(), "/x/", Array.Empty<string>(), "", null, null);
        Assert.Null(HandbookSite.PageUrl(site, page));
    }

    [Fact]
    public void Mirror_SendsTheTokenOnlyOverHttpsToItsOwnHost()
    {
        var token = "tok123";
        var same = new RepoMirror("h", "https://devops.example.edu/org/proj/_git/handbook", tokenHost: "devops.example.edu", root: Path.GetTempPath());
        var other = new RepoMirror("h", "https://elsewhere.example.org/handbook.git", tokenHost: "devops.example.edu", root: Path.GetTempPath());
        var plain = new RepoMirror("h", "http://devops.example.edu/org/proj/_git/handbook", tokenHost: "devops.example.edu", root: Path.GetTempPath());

        var env = same.GitEnvironment(token);
        Assert.Equal("http.https://devops.example.edu/.extraHeader", env["GIT_CONFIG_KEY_0"]);
        Assert.Equal("Authorization: Bearer tok123", env["GIT_CONFIG_VALUE_0"]);
        Assert.Equal("0", env["GIT_TERMINAL_PROMPT"]);

        Assert.False(other.GitEnvironment(token).ContainsKey("GIT_CONFIG_COUNT"));
        Assert.False(plain.GitEnvironment(token).ContainsKey("GIT_CONFIG_COUNT"));
        Assert.Equal("fatal: auth Bearer *** rejected", RepoMirror.Scrub("fatal: auth Bearer tok123 rejected"));
    }
}
