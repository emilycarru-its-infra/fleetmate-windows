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
