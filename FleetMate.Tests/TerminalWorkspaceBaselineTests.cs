using FleetMate.Core.Services.Repos;
using FleetMate.Core.Services.Terminal;
using Xunit;

namespace FleetMate.Tests;

public class TerminalWorkspaceBaselineTests
{
    private static readonly DateTime Now = new(2033, 5, 18, 0, 0, 0, DateTimeKind.Utc);

    private static TerminalWorkspaceBaseline.Checkout Checkout(RepoProvider provider, string scope, string name, double? daysAgo) =>
        new(provider, scope, $"{scope}/{name}", $@"C:\src\{scope}\{name}", daysAgo is { } d ? Now.AddDays(-d) : null);

    [Fact]
    public void NoCheckouts_MakesNoWorkspace() =>
        Assert.Null(TerminalWorkspaceBaseline.Make(Array.Empty<TerminalWorkspaceBaseline.Checkout>(), Now));

    [Fact]
    public void AFewCheckouts_AreAllIncluded_GroupedByHost()
    {
        var workspace = TerminalWorkspaceBaseline.Make(new[]
        {
            Checkout(RepoProvider.GitHub, "zed", "b", 400),
            Checkout(RepoProvider.AzureDevOps, "Proj", "a", null),
            Checkout(RepoProvider.GitHub, "alpha", "c", 1),
        }, Now)!;
        Assert.Equal(TerminalWorkspaceBaseline.Source, workspace.Source);
        Assert.Equal(new[] { "Azure DevOps", "GitHub" }, workspace.Sections.Select(s => s.Name));
        Assert.Equal(new[] { "alpha/c", "zed/b" }, workspace.Sections[1].Sessions.Select(s => s.Label));
        // No command: the row runs the default launch as it is when it starts.
        Assert.Null(workspace.Sections[0].Sessions[0].Command);
        Assert.True(workspace.SessionsFollowDefault);
    }

    [Fact]
    public void ManyCheckouts_KeepOnlyTheActiveOnes()
    {
        var all = Enumerable.Range(0, 10).Select(i => Checkout(RepoProvider.GitHub, "o", $"old{i}", 60)).ToList();
        all.Add(Checkout(RepoProvider.GitHub, "o", "recent", 3));
        Assert.Equal(new[] { "o/recent" }, TerminalWorkspaceBaseline.ActiveCheckouts(all, Now).Select(c => c.DisplayName));
    }

    [Fact]
    public void ManyIdleCheckouts_FallBackToTheMostRecent()
    {
        var all = Enumerable.Range(0, 12).Select(i => Checkout(RepoProvider.GitHub, "o", $"r{i}", 30 + i)).ToList();
        var chosen = TerminalWorkspaceBaseline.ActiveCheckouts(all, Now);
        Assert.Equal(TerminalWorkspaceBaseline.IncludeAllUpTo, chosen.Count);
        Assert.Equal("o/r0", chosen[0].DisplayName);
    }

    [Fact]
    public void AddMissing_KeepsEdits_AndAddsOnlyNewFolders()
    {
        var workspace = TerminalWorkspaceBaseline.Make(new[] { Checkout(RepoProvider.GitHub, "o", "a", 1) }, Now)!;
        workspace.Sections[0].Sessions[0].Command = "claude";
        workspace.Sections.Add(new TerminalWorkspaceSection { Name = "Mine" });
        var added = TerminalWorkspaceBaseline.AddMissing(new[]
        {
            Checkout(RepoProvider.GitHub, "o", "a", 1),
            Checkout(RepoProvider.AzureDevOps, "P", "b", 1),
        }, workspace, Now);
        Assert.Equal(1, added);
        Assert.Equal("claude", workspace.Sections[0].Sessions[0].Command);
        Assert.Equal(new[] { "GitHub", "Mine", "Azure DevOps" }, workspace.Sections.Select(s => s.Name));
    }

    [Fact]
    public void Activity_FollowsTheHeadReflog()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git", "logs"));
            Assert.Null(TerminalWorkspaceBaseline.LastActivity(Path.Combine(root, "missing")));
            File.WriteAllText(Path.Combine(root, ".git", "logs", "HEAD"), "");
            Assert.NotNull(TerminalWorkspaceBaseline.LastActivity(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Baseline_IsFoundAfterARename()
    {
        var file = new TerminalWorkspaceFile();
        file.Upsert(new TerminalWorkspace { Name = "Mine" });
        file.Upsert(new TerminalWorkspace { Name = "Daily repos", Source = TerminalWorkspaceBaseline.Source });
        Assert.Equal("Daily repos", file.Baseline?.Name);
    }

    [Fact]
    public void Checkouts_LeaveOutReposHiddenFromTheSidebar()
    {
        var root = Path.Combine(Path.GetTempPath(), "fm-baseline-" + Guid.NewGuid().ToString("N"));
        try
        {
            RepoRegistryEntry Entry(RepoProvider provider, string owner, string? project, string name, bool hidden)
            {
                var path = Path.Combine(root, name);
                Directory.CreateDirectory(path);
                return new RepoRegistryEntry { Key = new RepoKey(provider, owner, project, name), Path = path, Tracked = true, Hidden = hidden };
            }
            var shown = Entry(RepoProvider.GitHub, "example", null, "shown", hidden: false);
            var hidden = Entry(RepoProvider.GitHub, "example", null, "hidden", hidden: true);
            var hiddenAzure = Entry(RepoProvider.AzureDevOps, "example", "Sample", "secret", hidden: true);
            var document = new RepoRegistryDocument();
            foreach (var e in new[] { shown, hidden, hiddenAzure }) document.Repos[e.Key.Id] = e;

            var checkouts = TerminalWorkspaceBaseline.Checkouts(document);

            Assert.Equal(new[] { shown.Path }, checkouts.Select(c => c.Path));
            var workspace = TerminalWorkspaceBaseline.Make(checkouts, Now)!;
            Assert.Equal(new[] { "GitHub" }, workspace.Sections.Select(s => s.Name));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }
}
