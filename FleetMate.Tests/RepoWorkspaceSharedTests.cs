using FleetMate.Core.Services.Repos;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The Repos workspace pieces both apps share from FleetMate.Core: the row and
/// header wording, and the fictional repositories demo mode and screenshots use.
/// </summary>
public class RepoWorkspaceSharedTests
{
    [Fact]
    public void ChangeFolderAndTooltipReadForwardSlashes()
    {
        var nested = new GitStatusEntry("src/app/main.cs", GitEntryKind.Modified, Staged: false);
        var root = new GitStatusEntry("README.md", GitEntryKind.Added, Staged: true);
        var renamed = new GitStatusEntry("docs/new.md", GitEntryKind.Renamed, Staged: true, OriginalPath: "docs/old.md");
        Assert.Equal("src/app", RepoWorkspaceText.ChangeFolder(nested));
        Assert.Equal("", RepoWorkspaceText.ChangeFolder(root));
        Assert.Equal("docs/old.md → docs/new.md", RepoWorkspaceText.ChangeTooltip(renamed));
        Assert.Equal("README.md", RepoWorkspaceText.ChangeTooltip(root));
    }

    [Fact]
    public void RefLabelMarksOnlyTheLocalBranchHeadIsOn()
    {
        Assert.Equal("HEAD → main", RepoWorkspaceText.RefLabel(new RepoGitRef("main", GitRefKind.LocalBranch, IsHead: true)));
        Assert.Equal("feature/x", RepoWorkspaceText.RefLabel(new RepoGitRef("feature/x", GitRefKind.LocalBranch)));
        Assert.Equal("v1.0", RepoWorkspaceText.RefLabel(new RepoGitRef("v1.0", GitRefKind.Tag)));
        Assert.Equal("origin/main", RepoWorkspaceText.RefLabel(new RepoGitRef("origin/main", GitRefKind.RemoteBranch, IsHead: true)));
    }

    [Fact]
    public void BranchTitleNamesItsUpstream()
    {
        Assert.Equal("main   →  origin/main", RepoWorkspaceText.BranchTitle(new GitBranch("main", true, "origin/main")));
        Assert.Equal("scratch", RepoWorkspaceText.BranchTitle(new GitBranch("scratch", false)));
    }

    [Fact]
    public void NewClonesPlaceNamesTheOneWatchedFolderOrSaysSeveral()
    {
        var root = Path.Combine(Path.GetTempPath(), "fleetmate-place-" + Guid.NewGuid().ToString("N"));
        var other = root + "-other";
        var one = new DiscoveredCheckout(Path.Combine(root, "a"), null, null);
        var two = new DiscoveredCheckout(Path.Combine(root, "b"), null, null);
        var elsewhere = new DiscoveredCheckout(Path.Combine(other, "c"), null, null);
        Assert.Equal(root, RepoWorkspaceText.NewClonesPlace(new[] { one, two }, new[] { root, other }));
        Assert.Equal("your watched folders", RepoWorkspaceText.NewClonesPlace(new[] { one, elsewhere }, new[] { root, other }));
        Assert.Equal("your watched folders", RepoWorkspaceText.NewClonesPlace(new[] { one }, null));
        Assert.StartsWith("2 new repositories found in ", RepoWorkspaceText.NewClonesSummary(new[] { one, two }, new[] { root }));
        Assert.StartsWith("1 new repository found in ", RepoWorkspaceText.NewClonesSummary(new[] { one }, new[] { root }));
    }

    [Fact]
    public async Task DemoFixtureBuildsFictionalTrackedRepositoriesWithOriginMain()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fleetmate-demo-" + Guid.NewGuid().ToString("N"));
        try
        {
            var manager = await RepoDemoFixture.CreateAsync(folder, new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
            var records = manager.Records().Where(r => r.IsTracked).ToList();
            Assert.Equal(3, records.Count);
            // Everything lives under the fixture's own folder: no real clone or registry is read.
            Assert.All(records, r => Assert.StartsWith(folder, r.Local!.Path, StringComparison.OrdinalIgnoreCase));
            Assert.All(records, r => Assert.True(r.Key.Scope is "fabrikam-demo" or "contoso-sample"));

            var first = records.Single(r => r.Key.Name == "harbor-api");
            var copy = new GitWorkingCopy(first.Local!.Path);
            var status = await copy.StatusAsync();
            Assert.Equal("feature/tide-alerts", status.Branch);
            Assert.NotEmpty(GitStatusEntry.Entries(status));
            var history = await copy.HistoryAsync(200);
            Assert.Contains(history, c => c.Parents.Count == 2);
            Assert.All(history, c => Assert.DoesNotContain("@", c.Author));

            // Insights read origin/main, and it is there.
            var stats = await manager.StatsAsync(first, null, top: 12, reference: RepoInsightsModel.HistoryReference);
            Assert.True(stats.Total.Commits >= 40);
            Assert.True(stats.Total.Authors >= 3);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(folder, true);
            }
        }
    }
}
