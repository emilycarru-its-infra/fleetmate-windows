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
    public void CodeWorkspaceFolders_AreProposals_WithCommentsAndTrailingCommas()
    {
        var outside = Path.Combine(Path.GetTempPath(), "fleetmate-outside-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(outside, "elsewhere", ".git"));
            // Deeper than the walk goes, so only the workspace file finds it.
            FakeCheckout("a", "b", "c", "d", "e", "deep");
            FakeCheckout("monorepo");
            Dir("monorepo", "apps", "web");
            var outsideRelative = Path.GetRelativePath(Root, Path.Combine(outside, "elsewhere")).Replace('\\', '/');
            File.WriteAllText(Path.Combine(Root, "team.code-workspace"), $$"""
                {
                  // the folders the team opens together
                  "folders": [
                    { "path": "a/b/c/d/e/deep" },
                    /* a subfolder of a repository the walk already found */
                    { "name": "web", "path": "{{Path.Combine(Root, "monorepo", "apps", "web").Replace("\\", "\\\\")}}", },
                    { "path": "{{outsideRelative}}" },
                    { "path": "//server/share/repo" },
                    { "path": "\\\\server\\share\\repo" },
                    { "path": "~/elsewhere" },
                    { "path": "does-not-exist" },
                  ],
                  "settings": { "files.exclude": { "**/bin": true, }, },
                }
                """);

            var result = RepoDiscovery.Scan(new[] { Root }, 3, Array.Empty<string>());
            Assert.Single(result.WorkspaceFiles);
            Assert.Equal(new[] { "monorepo" }, result.Checkouts.Select(Path.GetFileName));
            Assert.Equal(new[] { "deep" }, result.WorkspaceCheckouts.Select(Path.GetFileName));
            Assert.DoesNotContain(RepoDiscovery.FindCheckouts(new[] { Root }, 3, Array.Empty<string>()), p => p.EndsWith("deep"));
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void WorkspaceFilesBelowTheTopOfARoot_AreNotRead()
    {
        FakeCheckout("a", "b", "c", "d", "e", "deep");
        var checkout = FakeCheckout("repo");
        File.WriteAllText(Path.Combine(checkout, "inside.code-workspace"), """{ "folders": [ { "path": "../a/b/c/d/e/deep" } ] }""");
        File.WriteAllText(Path.Combine(Dir("nested"), "nested.code-workspace"), """{ "folders": [ { "path": "../a/b/c/d/e/deep" } ] }""");
        var result = RepoDiscovery.Scan(new[] { Root }, 3, Array.Empty<string>());
        Assert.Empty(result.WorkspaceFiles);
        Assert.Empty(result.WorkspaceCheckouts);
    }

    [Fact]
    public void AWorkspaceFolderReachedThroughALinkOutOfTheRoot_IsRejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "fleetmate-outside-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(outside, "target", ".git"));
            try { Directory.CreateSymbolicLink(Path.Combine(Root, "link"), outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            File.WriteAllText(Path.Combine(Root, "team.code-workspace"), """{ "folders": [ { "path": "link/target" } ] }""");
            Assert.Empty(RepoDiscovery.Scan(new[] { Root }, 3, Array.Empty<string>()).WorkspaceCheckouts);
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void IsInside_ComparesOnASeparatorBoundary()
    {
        var root = Path.Combine(Root, "code");
        Assert.True(RepoDiscovery.IsInside(Path.Combine(root, "x"), new[] { root }));
        Assert.True(RepoDiscovery.IsInside(root, new[] { root }));
        Assert.False(RepoDiscovery.IsInside(root + "-other", new[] { root }));
    }

    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData("//server/share")]
    [InlineData(@"\\?\C:\repo")]
    [InlineData(@"\\.\pipe\x")]
    public void UncAndDevicePaths_AreRefused(string path)
    {
        Assert.True(RepoDiscovery.IsUncOrDevicePath(path));
        Assert.Null(RepoDiscovery.ResolveLinks(path));
    }

    [Fact]
    public void ReadOrigin_ParsesTheConfig_AndFollowsAGitdirFile()
    {
        var checkout = FakeCheckout("one");
        File.WriteAllText(Path.Combine(checkout, ".git", "config"), """
            [core]
            	bare = false
            ; a comment
            [remote "upstream"]
            	url = https://github.com/example-org/upstream.git
            [remote "origin"]
            	fetch = +refs/heads/*:refs/remotes/origin/*
            	url = "https://github.com/example-org/one.git"
            """);
        Assert.Equal("https://github.com/example-org/one.git", RepoDiscovery.ReadOrigin(checkout));

        var modules = Dir("one", ".git", "modules", "sub");
        File.WriteAllText(Path.Combine(modules, "config"), "[remote \"origin\"]\n\turl = git@github.com:example-org/sub.git\n");
        var sub = Dir("one", "sub");
        File.WriteAllText(Path.Combine(sub, ".git"), "gitdir: ../.git/modules/sub\n");
        Assert.Equal("git@github.com:example-org/sub.git", RepoDiscovery.ReadOrigin(sub));

        Assert.Null(RepoDiscovery.ReadOrigin(FakeCheckout("no-remote")));
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

    private static void WriteOrigin(string checkout, string name) =>
        File.WriteAllText(Path.Combine(checkout, ".git", "config"), $"[remote \"origin\"]\n\turl = https://github.com/example-org/{name}.git\n");

    [Fact]
    public async Task NewClones_AreOfferedNotRegistered_AndOnlyOnceRootsAreChosen()
    {
        var manager = Manager;
        var known = Found("known");
        WriteOrigin(known.Path, "known");
        manager.Register(new[] { known });
        WriteOrigin(FakeCheckout("code", "fresh"), "fresh");

        Assert.Null(await manager.NewClonesAsync());
        manager.AddScanRoots(new[] { Path.Combine(Root, "code") });
        Assert.True(manager.Settings().RootsChosen);

        var offered = await manager.NewClonesAsync();
        Assert.Equal(new[] { "fresh" }, offered!.Select(c => c.Key!.Name));
        Assert.Throws<RepoException>(() => manager.Resolve("fresh"));

        manager.Register(offered!);
        Assert.Empty((await manager.NewClonesAsync())!);
        Assert.False(manager.Resolve("fresh").IsTracked);

        manager.UpdateSettings(s => s.AutoTrackNewClones = false);
        Assert.Null(await manager.NewClonesAsync());
    }

    [Fact]
    public async Task TrackFolders_TracksWhatTheWalkFinds_ButNotWorkspaceProposals()
    {
        var manager = Manager;
        WriteOrigin(FakeCheckout("code", "walked"), "walked");
        var deep = FakeCheckout("code", "a", "b", "c", "d", "e", "deep");
        WriteOrigin(deep, "deep");
        File.WriteAllText(Path.Combine(Root, "code", "team.code-workspace"), """{ "folders": [ { "path": "a/b/c/d/e/deep" } ] }""");

        await manager.TrackFoldersAsync(new[] { Path.Combine(Root, "code") });
        Assert.True(manager.Resolve("walked").IsTracked);
        Assert.Throws<RepoException>(() => manager.Resolve("deep"));

        var preview = await manager.PreviewAsync(new[] { Path.Combine(Root, "code") });
        Assert.Contains(preview, c => c.Key?.Name == "deep" && c.FromWorkspace);
        Assert.Contains(preview, c => c.Key?.Name == "walked" && !c.FromWorkspace);
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
    public void Discovery_ReadsTheOriginWithoutGit() =>
        Assert.Equal(Remote, RepoDiscovery.ReadOrigin(Clone));

    [Fact]
    public void InspectionConfig_TurnsOffFsmonitorAndHooks()
    {
        Assert.Contains("core.fsmonitor=false", GitWorkingCopy.InspectionConfig);
        Assert.Contains(GitWorkingCopy.InspectionConfig, a => a.StartsWith("core.hooksPath="));
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
