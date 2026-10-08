using FleetMate.Core.Knowledge;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The Handbook catalog, reader and search, and the Skills catalog (macOS parity).</summary>
public class KnowledgeParityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-knowledge-" + Guid.NewGuid().ToString("N"));

    public KnowledgeParityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void CatalogLoadsSummariesAndSkipsLegacy()
    {
        var catalog = Write("catalog.json", """
            {"pages":[
              {"path":"devices/enrollment.md","title":"Enrollment","url":"/devices/enrollment/","section":"devices",
               "headings":["Steps"],"summary":"How a device enrolls","keywords":["autopilot"],"lastmod":"2026-10-01","lastmod_by":""},
              {"path":"legacy/old.md","title":"Old system","url":"/legacy/old/","section":"legacy"}
            ]}
            """);
        var index = HandbookIndex.LoadCatalog(catalog)!;
        var page = Assert.Single(index.Pages);
        Assert.Equal("Enrollment", page.Title);
        Assert.True(page.IsSummary);
        Assert.Equal("/devices/enrollment/", page.SitePath);
        Assert.Contains("autopilot", page.Body);
        Assert.Null(page.LastModifiedBy);
    }

    [Fact]
    public void MissingOrBrokenCatalogFallsBack()
    {
        Assert.Null(HandbookIndex.LoadCatalog(Path.Combine(_root, "none.json")));
        Assert.Null(HandbookIndex.LoadCatalog(Write("bad.json", "{ not json")));
        Assert.Null(HandbookIndex.LoadCatalog(Write("empty.json", """{"pages":[]}""")));
    }

    [Fact]
    public void FullContentLoadSkipsLegacy()
    {
        Write("content/devices/enrollment.md", "---\ntitle: Enrollment\n---\n# Steps\nbody");
        Write("content/legacy/old.md", "---\ntitle: Old\n---\nold");
        var index = HandbookIndex.Load(Path.Combine(_root, "content"));
        Assert.Equal(new[] { "Enrollment" }, index.Pages.Select(p => p.Title));
    }

    [Fact]
    public void FullPageReadsTheFileAndNeverLeavesTheContentFolder()
    {
        Write("content/devices/enrollment.md", "---\ntitle: Enrollment\n---\nThe whole page.");
        Write("secret.md", "---\ntitle: Outside\n---\nnope");
        var content = Path.Combine(_root, "content");
        var page = HandbookIndex.FullPage("devices/enrollment.md", content)!;
        Assert.False(page.IsSummary);
        Assert.Contains("The whole page.", page.Body);
        Assert.Null(HandbookIndex.FullPage("../secret.md", content));
        Assert.Null(HandbookIndex.FullPage("devices/missing.md", content));
    }

    [Fact]
    public void SearchNeedsEveryWordAndRanksTitlesFirst()
    {
        var index = new HandbookIndex(new[]
        {
            HandbookIndex.Page("a.md", "---\ntitle: Printer setup\n---\nqueue drivers")!,
            HandbookIndex.Page("b.md", "---\ntitle: Drivers\n---\nprinter queue")!,
            HandbookIndex.Page("c.md", "---\ntitle: Wi-Fi\n---\nnothing here")!,
        });
        Assert.Equal(new[] { "Printer setup", "Drivers" }, index.Search("printer queue").Select(p => p.Title));
        Assert.Empty(index.Search("printer wifi"));
        Assert.Empty(index.Search("a"));
    }

    [Fact]
    public void HubSkillsHooksAndStandards()
    {
        Write("hub/agents/skills/ship-code/SKILL.md", "---\nname: ship-code\ndescription: Ship a change\n---\n# Ship");
        Write("hub/agents/skills/ship-code/run.sh", "echo");
        Write("hub/agents/skills/no-skill-file/notes.md", "x");
        Write("hub/agents/githooks/pre-commit", "#!/bin/sh\n# Block secrets before commit\nexit 0");
        Write("hub/agents/AGENTS.shared.md", "# Shared");
        Write("hub/agents/scopes/public-repo.md", "# Public");
        Write("hub/agents/scopes/README.md", "# Index");

        var entries = SkillCatalog.Load(Path.Combine(_root, "hub"));
        var skill = Assert.Single(entries, e => e.Kind == SkillKind.Skill);
        Assert.Equal("ship-code", skill.Name);
        Assert.Equal("Ship a change", skill.Summary);
        Assert.Equal(new[] { "run.sh" }, skill.Files);
        Assert.Equal(SkillOrigin.Shared, skill.Origin);
        var hook = Assert.Single(entries, e => e.Kind == SkillKind.Hook);
        Assert.Equal("Block secrets before commit", hook.Summary);
        Assert.StartsWith("```", hook.Body);
        Assert.Equal(new[] { "Estate-wide agent standards", "public-repo" },
            entries.Where(e => e.Kind == SkillKind.Standard).Select(e => e.Name));
    }

    [Fact]
    public void LocalSkillsAndHooksAreGroupedAfterShared()
    {
        Write("claude/skills/blog-post/SKILL.md", "---\nname: blog-post\ndescription: Draft a post\n---\nbody");
        Write("claude/hooks/guard.ps1", "<# Stop pushes to main #>\nexit 0");
        Write("hub/agents/skills/handbook/SKILL.md", "---\nname: handbook\ndescription: Read the Handbook\n---\nbody");

        var all = SkillCatalog.Load(Path.Combine(_root, "hub"))
            .Concat(SkillCatalog.LoadLocal(Path.Combine(_root, "claude"))).ToList();
        var groups = SkillCatalog.Grouped(all, null);
        Assert.Equal(new[] { "Skills · Shared with every repository", "Skills · On this PC", "Hooks · On this PC" },
            groups.Select(g => g.Group));
        Assert.Equal("Stop pushes to main", all.Single(e => e.Name == "guard.ps1").Summary);
        Assert.Equal("~/.claude/skills/blog-post/SKILL.md", all.Single(e => e.Name == "blog-post").Path);

        var filtered = SkillCatalog.Grouped(all, "draft");
        Assert.Equal("blog-post", Assert.Single(Assert.Single(filtered).Entries).Name);
    }

    [Fact]
    public void MissingFoldersGiveNothing()
    {
        Assert.Empty(SkillCatalog.Load(Path.Combine(_root, "nowhere")));
        Assert.Empty(SkillCatalog.LoadLocal(Path.Combine(_root, "nowhere")));
    }
}

/// <summary>The allow-list for links in Handbook and skill text.</summary>
public class HandbookLinkTests
{
    private const string Site = "https://handbook.example.org";

    private static readonly HandbookIndex Index = new(new[]
    {
        HandbookIndex.Page("devices/enrollment.md", "---\ntitle: Enrollment\n---\nbody")!,
        HandbookIndex.Page("devices/wifi.md", "---\ntitle: Wi-Fi\n---\nbody")!,
    });

    private static HandbookPage From => Index.Pages[0];

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData(@"\\server\share\run.exe")]
    [InlineData("//server/share/run.exe")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("mailto:someone@example.org")]
    [InlineData("calc.exe")]
    [InlineData("#steps")]
    [InlineData("")]
    public void AnythingButAPageOrAWebLinkIsDropped(string href)
    {
        var action = HandbookLinks.Classify(href, From, Index, Site);
        // A bare name is a relative link on the site: at most an https page there, never the shell.
        if (action.Kind == HandbookLinkKind.OpenInBrowser)
        {
            Assert.Equal("https", action.Url!.Scheme);
            Assert.Equal("handbook.example.org", action.Url.Host);
        }
        if (action.Kind == HandbookLinkKind.OpenPage) Assert.NotNull(action.Page);
        Assert.Null(HandbookLinks.External(href));
    }

    [Theory]
    [InlineData("https://handbook.example.org@evil.example/x")]
    [InlineData("http://user:pass@learn.example.com/")]
    public void LinksWithAUserNameAreRefused(string href)
    {
        Assert.Equal(HandbookLinkKind.Ignore, HandbookLinks.Classify(href, From, Index, Site).Kind);
        Assert.Null(HandbookLinks.External(href));
    }

    [Fact]
    public void LookalikeHostsAreAnotherSite()
    {
        var action = HandbookLinks.Classify("https://handbook.example.org.evil.example/devices/wifi/", From, Index, Site);
        Assert.Equal(HandbookLinkKind.OpenInBrowser, action.Kind);
        Assert.Equal("handbook.example.org.evil.example", action.Url!.Host);
        Assert.Equal(HandbookLinkKind.OpenInBrowser, HandbookLinks.Classify("HTTPS://learn.example.com/x", From, Index, Site).Kind);
    }

    [Theory]
    [InlineData("../wifi/")]
    [InlineData("/devices/wifi/")]
    [InlineData("/devices/wifi")]
    [InlineData("https://handbook.example.org/devices/wifi/")]
    public void LinksToAnotherPageOpenInTheReader(string href)
    {
        var action = HandbookLinks.Classify(href, From, Index, Site);
        Assert.Equal(HandbookLinkKind.OpenPage, action.Kind);
        Assert.Equal("Wi-Fi", action.Page!.Title);
    }

    [Fact]
    public void RelativePageLinksWorkWithoutASiteAddress()
    {
        Assert.Equal(HandbookLinkKind.OpenPage, HandbookLinks.Classify("../wifi/", From, Index, null).Kind);
        // Unknown on-site page and no site to send it to: nothing happens.
        Assert.Equal(HandbookLinkKind.Ignore, HandbookLinks.Classify("/nowhere/", From, Index, null).Kind);
    }

    [Fact]
    public void WebLinksOpenInTheBrowser()
    {
        var external = HandbookLinks.Classify("https://learn.example.com/a?b=c", From, Index, Site);
        Assert.Equal(HandbookLinkKind.OpenInBrowser, external.Kind);
        Assert.Equal("https://learn.example.com/a?b=c", external.Url!.AbsoluteUri);

        var unknownOnSite = HandbookLinks.Classify("/not-in-the-copy/", From, Index, Site);
        Assert.Equal(HandbookLinkKind.OpenInBrowser, unknownOnSite.Kind);
        Assert.Equal("handbook.example.org", unknownOnSite.Url!.Host);

        Assert.Equal("https://example.com/", HandbookLinks.External("https://example.com/")!.AbsoluteUri);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("//evil.example/x/")]
    [InlineData("/ok/../../x/")]
    public void SitePathsNeverLeaveTheSite(string sitePath)
    {
        var page = Index.Pages[0] with { SitePath = sitePath };
        var url = HandbookSite.PageUrl(Site, page);
        Assert.True(url == null || url.Host == "handbook.example.org");
        Assert.True(url == null || url.Scheme == "https");
        Assert.Null(HandbookSite.PageUrl("javascript:alert(1)", Index.Pages[0]));
        Assert.Null(HandbookSite.PageUrl("file:///C:/", Index.Pages[0]));
    }

    [Theory]
    [InlineData("javascript:alert(1)", "devices/enrollment.md", "/devices/enrollment/")]
    [InlineData("//evil.example/x/", "devices/_index.md", "/devices/")]
    [InlineData("/devices/enrollment/", "devices/enrollment.md", "/devices/enrollment/")]
    [InlineData(null, "_index.md", "/")]
    public void CatalogSitePathsAreSanitised(string? url, string path, string expected) =>
        Assert.Equal(expected, HandbookIndex.SafeSitePath(url, path));
}
