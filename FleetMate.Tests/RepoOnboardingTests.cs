using System.Text;
using FleetMate.Core.Services.Repos;
using Xunit;

namespace FleetMate.Tests;

// Folder names, owners and URLs below are placeholders made up for these tests.

/// <summary>A temp folder deleted afterwards, read-only git objects included.</summary>
public abstract class TempFolder : IDisposable
{
    protected string Root { get; } = Path.Combine(Path.GetTempPath(), "fleetmate-onboarding-" + Guid.NewGuid().ToString("N"));

    protected TempFolder() => Directory.CreateDirectory(Root);

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    protected string Dir(params string[] parts)
    {
        var path = Path.Combine(new[] { Root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A folder that looks like a checkout to the walk: it holds a <c>.git</c> folder.</summary>
    protected string FakeCheckout(params string[] parts)
    {
        var path = Dir(parts);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        return path;
    }
}

public sealed class RepoDiscoveryScanTests : TempFolder
{
    private List<string> Scan(int depth = 4, params string[] skip) =>
        RepoDiscovery.FindCheckouts(new[] { Root }, depth, skip).Select(Path.GetFileName).ToList()!;

    [Fact]
    public void FindsNestedClones_AndStopsAtACheckout()
    {
        FakeCheckout("one");
        FakeCheckout("Group", "two");
        FakeCheckout("one", "nested-inside-checkout");
        Assert.Equal(new[] { "two", "one" }, Scan().OrderByDescending(n => n));
    }

    [Fact]
    public void RespectsTheDepthLimit()
    {
        FakeCheckout("a", "b", "shallow");
        FakeCheckout("a", "b", "c", "d", "e", "deep");
        Assert.Equal(new[] { "shallow" }, Scan(depth: 3));
    }

    [Theory]
    [InlineData("node_modules")]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData(".worktrees")]
    [InlineData(".build")]
    [InlineData("packages")]
    public void NeverWalksBuiltInSkipFolders(string folder)
    {
        FakeCheckout(folder, "dependency");
        FakeCheckout("kept");
        Assert.Equal(new[] { "kept" }, Scan());
    }

    [Fact]
    public void ConfiguredSkipNamesAreHonoured()
    {
        FakeCheckout("Archive", "old");
        FakeCheckout("kept");
        Assert.Equal(new[] { "kept" }, Scan(4, "archive"));
    }

    [Fact]
    public void ACheckoutWithASkippedName_IsStillFound()
    {
        FakeCheckout("build");
        FakeCheckout("build", "inner");
        Assert.Equal(new[] { "build" }, Scan());
    }

    [Fact]
    public void LinkedWorktrees_AreNotReported()
    {
        var worktree = Dir("feature-worktree");
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: /somewhere/main/.git/worktrees/feature-worktree\n");
        var submodule = Dir("submodule");
        File.WriteAllText(Path.Combine(submodule, ".git"), "gitdir: ../main/.git/modules/submodule\n");
        Assert.Equal(new[] { "submodule" }, Scan());
    }

    [Fact]
    public void CodeWorkspaceFolders_AreAdded_WithCommentsAndTrailingCommas()
    {
        var outside = Path.Combine(Path.GetTempPath(), "fleetmate-outside-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(outside, "elsewhere", ".git"));
            Directory.CreateDirectory(Path.Combine(outside, "monorepo", ".git"));
            Directory.CreateDirectory(Path.Combine(outside, "monorepo", "apps", "web"));
            var scanRoot = Dir("workspaces");
            var relativeElsewhere = Path.GetRelativePath(scanRoot, Path.Combine(outside, "elsewhere")).Replace('\\', '/');
            File.WriteAllText(Path.Combine(scanRoot, "team.code-workspace"), $$"""
                {
                  // the folders the team opens together
                  "folders": [
                    { "path": "{{relativeElsewhere}}" },
                    /* a subfolder of a repository */
                    { "name": "web", "path": "{{Path.Combine(outside, "monorepo", "apps", "web").Replace("\\", "\\\\")}}", },
                    { "path": "does-not-exist" },
                  ],
                  "settings": { "files.exclude": { "**/bin": true, }, },
                }
                """);

            var result = RepoDiscovery.Scan(new[] { Root }, 4, Array.Empty<string>());
            Assert.Single(result.WorkspaceFiles);
            Assert.Equal(new[] { "elsewhere", "monorepo" }, result.Checkouts.Select(Path.GetFileName).OrderBy(n => n));
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void WorkspaceFolders_ParsesRelativeAndAbsolutePaths()
    {
        var folders = RepoDiscovery.WorkspaceFolders("""{ "folders": [ { "path": "a" }, { "path": "../b" }, ], }""", Root);
        Assert.Equal(new[] { Path.Combine(Root, "a"), Path.GetFullPath(Path.Combine(Root, "..", "b")) }, folders);
        Assert.Empty(RepoDiscovery.WorkspaceFolders("not json", Root));
    }

    [Fact]
    public void CommonLocations_IncludeTheUsualPlaces()
    {
        Assert.Contains(@"~\DevOps", RepoDiscovery.CommonLocations);
        Assert.Contains(@"~\source\repos", RepoDiscovery.CommonLocations);
        Assert.Contains(@"~\Documents\GitHub", RepoDiscovery.CommonLocations);
    }
}

public sealed class RepoRegistryCompatibilityTests : TempFolder
{
    private RepoRegistryStore Store => new(Path.Combine(Root, "repos.json"));

    [Fact]
    public void AFileWithoutTheNewKeys_Loads()
    {
        File.WriteAllText(Store.RegistryPath, """
            {
              "version": 1,
              "settings": { "scanRoots": ["~/Developer"], "scanDepth": 3 },
              "repos": {
                "github:example-org/one": {
                  "key": { "provider": "github", "owner": "example-org", "name": "one" },
                  "path": "/tmp/one", "tracked": true, "addedAt": "2026-01-01T00:00:00Z"
                }
              }
            }
            """);
        var document = Store.Load();
        Assert.True(document.Settings.AutoTrackNewClones);
        Assert.False(document.Settings.RootsChosen);
        Assert.False(document.Settings.TracksNewClones);
        Assert.False(document.Repos["github:example-org/one"].Hidden);
    }

    [Fact]
    public void Hidden_IsWrittenOnlyWhenSet()
    {
        var key = RepoRemoteUrl.Parse("https://github.com/example-org/one.git")!;
        Store.Update(d => d.Repos[key.Id] = new RepoRegistryEntry { Key = key, Path = Root, Tracked = true });
        Assert.DoesNotContain("\"hidden\"", File.ReadAllText(Store.RegistryPath));
        Store.Update(d => d.Repos[key.Id] = d.Repos[key.Id] with { Hidden = true });
        Assert.Contains("\"hidden\": true", File.ReadAllText(Store.RegistryPath));
        Assert.True(Store.Load().Repos[key.Id].Hidden);
    }
}

public sealed class RepoRegisterTests : TempFolder
{
    private RepoManager Manager => new(new RepoRegistryStore(Path.Combine(Root, "state", "repos.json")));

    private DiscoveredCheckout Found(string name)
    {
        var path = FakeCheckout("code", name);
        var url = $"https://github.com/example-org/{name}.git";
        return new DiscoveredCheckout(path, url, RepoRemoteUrl.Parse(url));
    }

    [Fact]
    public void TrackNew_TracksOnlyClonesSeenForTheFirstTime()
    {
        var manager = Manager;
        var known = Found("known");
        manager.Register(new[] { known });
        Assert.False(manager.Resolve("known").IsTracked);

        var results = manager.Register(new[] { known, Found("fresh") }, trackNew: true);

        Assert.Equal(new[] { DiscoveryAction.AlreadyLinked, DiscoveryAction.Linked }, results.Select(r => r.Action));
        Assert.False(manager.Resolve("known").IsTracked);
        Assert.True(manager.Resolve("fresh").IsTracked);
    }

    [Fact]
    public void Track_TracksEveryCloneFound()
    {
        var manager = Manager;
        var known = Found("known");
        manager.Register(new[] { known });
        manager.Register(new[] { known }, track: true);
        Assert.True(manager.Resolve("known").IsTracked);
    }

    [Fact]
    public void HiddenSurvivesARescan()
    {
        var manager = Manager;
        var known = Found("known");
        manager.Register(new[] { known }, track: true);
        manager.SetHidden("known", true);
        manager.Register(new[] { known }, track: true);
        var record = manager.Resolve("known");
        Assert.True(record.IsHidden);
        Assert.True(record.IsTracked);
    }

    [Fact]
    public async Task TrackNewClones_DoesNothingUntilRootsAreChosen()
    {
        var manager = Manager;
        Assert.Null(await manager.TrackNewClonesAsync());
        manager.AddScanRoots(new[] { Path.Combine(Root, "code") });
        Assert.True(manager.Settings().RootsChosen);
        Assert.NotNull(await manager.TrackNewClonesAsync());
        manager.UpdateSettings(s => s.AutoTrackNewClones = false);
        Assert.Null(await manager.TrackNewClonesAsync());
    }

    [Fact]
    public void AddScanRoots_DeduplicatesAndAbbreviatesTheProfileFolder()
    {
        var manager = Manager;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        manager.AddScanRoots(new[] { Path.Combine(home, "SomeCode"), Path.Combine(home, "SomeCode") + Path.DirectorySeparatorChar });
        var roots = manager.Settings().ScanRoots;
        Assert.Single(roots, r => r == "~" + Path.DirectorySeparatorChar + "SomeCode");
    }
}

/// <summary>A real clone of a local bare repository, so pushed and unpushed are real.</summary>
public sealed class CloneRemovalTests : IAsyncLifetime
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "fleetmate-removal-" + Guid.NewGuid().ToString("N"));
    private string Remote => Path.Combine(_parent, "remote.git");
    private string Clone => Path.Combine(_parent, "code", "example-repo");
    private readonly List<string> _recycled = new();

    private static async Task Git(string? directory, params string[] args)
    {
        var all = directory == null ? args : new[] { "-C", directory }.Concat(args).ToArray();
        var result = await GitWorkingCopy.RunGitAsync(all);
        Assert.True(result.Succeeded, $"git {string.Join(' ', args)}: {result.StandardError}");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_parent, "code"));
        await Git(null, "init", "-q", "--bare", "-b", "main", Remote);
        await Git(null, "clone", "-q", Remote, Clone);
        foreach (var (k, v) in new[] { ("user.email", "dev@example.com"), ("user.name", "Dev"), ("commit.gpgsign", "false"), ("core.autocrlf", "false") })
            await Git(Clone, "config", k, v);
        File.WriteAllText(Path.Combine(Clone, "README.md"), "hello\n");
        await Git(Clone, "add", "README.md");
        await Git(Clone, "commit", "-q", "-m", "Initial");
        await Git(Clone, "push", "-q", "origin", "main");
    }

    public Task DisposeAsync()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_parent, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_parent, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    private RepoManager Manager()
    {
        var manager = new RepoManager(new RepoRegistryStore(Path.Combine(_parent, "state", "repos.json")), path =>
        {
            _recycled.Add(path);
            Directory.Move(path, Path.Combine(_parent, "trash-" + _recycled.Count));
        });
        var key = new RepoKey(RepoProvider.GitHub, "example-org", null, "example-repo");
        manager.Register(new[] { new DiscoveredCheckout(Clone, "https://github.com/example-org/example-repo.git", key) }, track: true);
        return manager;
    }

    [Fact]
    public async Task ACleanPushedClone_IsSafe()
    {
        var check = await CloneRemovalCheck.InspectAsync(Clone);
        Assert.True(check.IsSafe, string.Join("; ", check.LossDescription));
        Assert.Empty(check.LossDescription);
    }

    [Fact]
    public async Task UncommittedAndUntrackedFiles_AreReported()
    {
        File.WriteAllText(Path.Combine(Clone, "README.md"), "changed\n");
        File.WriteAllText(Path.Combine(Clone, "notes.txt"), "draft\n");
        var check = await CloneRemovalCheck.InspectAsync(Clone);
        Assert.False(check.IsSafe);
        Assert.Equal(new[] { "README.md", "notes.txt" }, check.Uncommitted.OrderBy(p => p, StringComparer.Ordinal));
        Assert.StartsWith("2 uncommitted files: ", check.LossDescription[0]);
    }

    [Fact]
    public async Task UnpushedCommits_AreReportedWithTheirBranch()
    {
        await Git(Clone, "switch", "-q", "-c", "feature/local-only");
        File.WriteAllText(Path.Combine(Clone, "work.txt"), "work\n");
        await Git(Clone, "add", "work.txt");
        await Git(Clone, "commit", "-q", "-m", "Local work");
        var check = await CloneRemovalCheck.InspectAsync(Clone);
        Assert.Equal(1, check.UnpushedCount);
        Assert.Equal(new[] { "feature/local-only" }, check.UnpushedBranches);
        Assert.Single(check.UnpushedSample, s => s.EndsWith(" Local work"));
        Assert.Contains("1 commit not pushed to any remote on feature/local-only", check.LossDescription);
    }

    [Fact]
    public async Task Stashes_AreReported()
    {
        File.WriteAllText(Path.Combine(Clone, "README.md"), "stashed\n");
        await Git(Clone, "stash", "push", "-q", "-m", "half done");
        var check = await CloneRemovalCheck.InspectAsync(Clone);
        Assert.Empty(check.Uncommitted);
        Assert.Single(check.Stashes);
        Assert.False(check.IsSafe);
        Assert.StartsWith("1 stash: ", check.LossDescription.Single());
    }

    [Fact]
    public async Task OtherWorktrees_AreReported()
    {
        var worktree = Path.Combine(_parent, "elsewhere-worktree");
        await Git(Clone, "worktree", "add", "-q", "-b", "side", worktree);
        var check = await CloneRemovalCheck.InspectAsync(Clone);
        Assert.Single(check.Worktrees);
        Assert.False(check.IsSafe);
    }

    [Fact]
    public async Task RemoveClone_RefusesWithoutForce_AndSaysWhatWouldBeLost()
    {
        var manager = Manager();
        File.WriteAllText(Path.Combine(Clone, "notes.txt"), "draft\n");
        var refused = await Assert.ThrowsAsync<RepoException>(() => manager.RemoveCloneAsync("example-repo"));
        Assert.Equal(RepoErrorKind.WouldLoseWork, refused.Kind);
        Assert.Contains("notes.txt", refused.Message);
        Assert.True(Directory.Exists(Clone));
        Assert.Empty(_recycled);
        Assert.True(manager.Resolve("example-repo").IsTracked);
    }

    [Fact]
    public async Task RemoveClone_WithForce_RecyclesAndForgets()
    {
        var manager = Manager();
        File.WriteAllText(Path.Combine(Clone, "notes.txt"), "draft\n");
        var check = await manager.RemoveCloneAsync("example-repo", force: true);
        Assert.False(check.IsSafe);
        Assert.Equal(new[] { Clone }, _recycled);
        Assert.False(Directory.Exists(Clone));
        Assert.Empty(manager.Registry().Repos);
    }

    [Fact]
    public async Task RemoveClone_OfASafeClone_NeedsNoForce()
    {
        var manager = Manager();
        await manager.RemoveCloneAsync("example-repo");
        Assert.Single(_recycled);
        Assert.Empty(manager.Registry().Repos);
    }

    [Fact]
    public void UnsafeTargets_AreRefused()
    {
        var settings = new RepoSettings { ScanRoots = new() { Path.Combine(_parent, "code") }, CloneRoot = Path.Combine(_parent, "clones") };
        Assert.Throws<RepoException>(() => RepoManager.RefuseUnsafeTarget(Path.Combine(_parent, "code"), settings));
        Assert.Throws<RepoException>(() => RepoManager.RefuseUnsafeTarget(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), settings));
        Assert.Throws<RepoException>(() => RepoManager.RefuseUnsafeTarget(Path.GetPathRoot(_parent)!, settings));
        var notACheckout = Directory.CreateDirectory(Path.Combine(_parent, "plain")).FullName;
        Assert.Equal(RepoErrorKind.NotAGitRepository,
            Assert.Throws<RepoException>(() => RepoManager.RefuseUnsafeTarget(notACheckout, settings)).Kind);
        RepoManager.RefuseUnsafeTarget(Clone, settings);
    }
}
