using System.CommandLine;
using System.Text;
using System.Text.Json;
using FleetMate.Commands.Repos;
using FleetMate.Core.Config;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Repos;
using Xunit;

namespace FleetMate.Tests;

// Every URL and git output below is hand-written for these tests; the hosts,
// organizations and repositories are placeholders.

public class RepoRemoteUrlTests
{
    private const string AzureHost = "azure-devops.example.com";

    [Theory]
    [InlineData("https://" + AzureHost + "/example-org/Project/_git/Repo")]
    [InlineData("https://example-org@" + AzureHost + "/example-org/Project/_git/Repo")]
    [InlineData("https://" + AzureHost + "/example-org/Project/_git/Repo.git")]
    [InlineData("https://" + AzureHost + "/example-org/Project/_git/Repo/")]
    [InlineData("https://example-org.visualstudio.com/Project/_git/Repo")]
    [InlineData("https://example-org.visualstudio.com/DefaultCollection/Project/_git/Repo")]
    [InlineData("git@ssh." + AzureHost + ":v3/example-org/Project/Repo")]
    [InlineData("example-org@vs-ssh.visualstudio.com:v3/example-org/Project/Repo")]
    [InlineData("ssh://git@ssh." + AzureHost + "/v3/example-org/Project/Repo")]
    public void AzureDevOpsForms_ParseToOneKey(string url) =>
        Assert.Equal(new RepoKey(RepoProvider.AzureDevOps, "example-org", "Project", "Repo"), RepoRemoteUrl.Parse(url));

    [Fact]
    public void AzureDevOpsIds_IgnoreCase()
    {
        var a = RepoRemoteUrl.Id($"https://{AzureHost}/Example-Org/PROJECT/_git/repo");
        var b = RepoRemoteUrl.Id($"git@ssh.{AzureHost}:v3/example-org/project/Repo");
        Assert.Equal("azdo:example-org/project/repo", a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void AzureDevOpsShortForm_UsesRepoAsProject() =>
        Assert.Equal(new RepoKey(RepoProvider.AzureDevOps, "example-org", "Tools", "Tools"),
            RepoRemoteUrl.Parse($"https://{AzureHost}/example-org/_git/Tools"));

    [Fact]
    public void AzureDevOpsPercentEncodedProject_IsDecoded()
    {
        var key = RepoRemoteUrl.Parse($"https://{AzureHost}/example-org/My%20Project/_git/Repo");
        Assert.Equal("My Project", key?.Project);
        Assert.Equal("My Project/Repo", key?.DisplayName);
    }

    [Theory]
    [InlineData("https://github.com/example-org/example-repo")]
    [InlineData("https://github.com/example-org/example-repo.git")]
    [InlineData("https://github.com/Example-Org/Example-Repo/")]
    [InlineData("git@github.com:example-org/example-repo.git")]
    [InlineData("ssh://git@github.com/example-org/example-repo.git")]
    [InlineData("https://user:secret@github.com/example-org/example-repo.git")]
    public void GitHubForms_ShareAnId(string url) => Assert.Equal("github:example-org/example-repo", RepoRemoteUrl.Id(url));

    [Fact]
    public void OtherHostsAndLocalPaths()
    {
        Assert.Equal(new RepoKey(RepoProvider.Other, "git.example.com", null, "group/sub/repo"),
            RepoRemoteUrl.Parse("https://git.example.com/group/sub/repo.git"));
        Assert.Equal(new RepoKey(RepoProvider.Other, "/srv/git", null, "tool"), RepoRemoteUrl.Parse("/srv/git/tool.git"));
        Assert.Equal("other:/srv/git/tool", RepoRemoteUrl.Id("file:///srv/git/tool.git"));
        Assert.Equal("other:c:/git/remotes/tool", RepoRemoteUrl.Id(@"C:\git\remotes\tool.git"));
        Assert.Equal("other:c:/git/remotes/tool", RepoRemoteUrl.Id("file:///C:/git/remotes/tool.git"));
        Assert.Equal(RepoProvider.Other, RepoRemoteUrl.Parse(@"\\server\share\tool.git")?.Provider);
        Assert.Null(RepoRemoteUrl.Parse(""));
        Assert.Null(RepoRemoteUrl.Parse("not a url"));
    }

    [Fact]
    public void CatalogEntryAndCheckout_ShareAnId()
    {
        var repo = new DevOpsGitRepository("0", "Repo", "Project",
            $"https://example-org@{AzureHost}/example-org/Project/_git/Repo",
            $"git@ssh.{AzureHost}:v3/example-org/Project/Repo",
            $"https://{AzureHost}/example-org/Project/_git/Repo", "refs/heads/main", false);
        var entry = RepoCatalogService.CatalogRepoFrom(repo, "example-org");
        Assert.Equal($"https://{AzureHost}/example-org/Project/_git/Repo", entry?.CloneUrl);
        Assert.Equal("main", entry?.DefaultBranch);
        Assert.Equal(RepoRemoteUrl.Id($"git@ssh.{AzureHost}:v3/example-org/project/repo"), entry?.Id);
    }

    [Fact]
    public void DisabledAzureRepository_IsSkipped()
    {
        var repo = new DevOpsGitRepository("0", "Old", "Project", $"https://{AzureHost}/example-org/Project/_git/Old", null, null, null, true);
        Assert.Null(RepoCatalogService.CatalogRepoFrom(repo, "example-org"));
    }

    [Fact]
    public void AzureListing_ParsesCloneUrlsAndDisabledFlag()
    {
        var json = JsonDocument.Parse("""
            {"value":[
              {"id":"1","name":"Repo","project":{"name":"Project"},"remoteUrl":"https://h/o/Project/_git/Repo","sshUrl":"git@ssh.h:v3/o/Project/Repo","webUrl":"https://h/o/Project/_git/Repo","defaultBranch":"refs/heads/main"},
              {"id":"2","name":"Gone","project":{"name":"Project"},"isDisabled":true}
            ]}
            """).RootElement;
        var list = AzureDevOpsService.ParseGitRepositories(json);
        Assert.Equal(2, list.Count);
        Assert.Equal("Project", list[0].Project);
        Assert.Equal("refs/heads/main", list[0].DefaultBranch);
        Assert.True(list[1].IsDisabled);
    }

    [Fact]
    public void GitHubNextLink()
    {
        const string header = "<https://api.github.com/user/repos?page=2>; rel=\"next\", <https://api.github.com/user/repos?page=5>; rel=\"last\"";
        Assert.Equal("https://api.github.com/user/repos?page=2", RepoCatalogService.NextLink(header)?.AbsoluteUri);
        Assert.Null(RepoCatalogService.NextLink("<https://api.github.com/user/repos?page=1>; rel=\"prev\""));
        Assert.Null(RepoCatalogService.NextLink(null));
    }

    [Fact]
    public async Task CatalogFetch_KeepsOneProviderWhenTheOtherFails()
    {
        var service = new RepoCatalogService(
            () => Task.FromResult(new List<DevOpsGitRepository>
            {
                new("1", "Repo", "Project", $"https://{AzureHost}/example-org/Project/_git/Repo", null, null, "refs/heads/main", false),
            }),
            "example-org",
            () => throw new InvalidOperationException("no network"));
        var catalog = await service.FetchAsync();
        Assert.Single(catalog.Repos);
        Assert.Contains(catalog.Errors, e => e.StartsWith("GitHub:"));
    }

    [Fact]
    public void DefaultClonePath_FollowsTheLayout()
    {
        var azure = new RepoKey(RepoProvider.AzureDevOps, "example-org", "Project", "Repo");
        var github = new RepoKey(RepoProvider.GitHub, "example-org", null, "example-repo");
        Assert.Equal(@"C:\work\AzDevOps\Project\Repo", RepoManager.DefaultClonePath(azure, @"C:\work"));
        Assert.Equal(@"C:\work\GitHub\example-org\example-repo", RepoManager.DefaultClonePath(github, @"C:\work"));
    }
}

public class RepoResolverTests
{
    private static RepoRecord Record(RepoKey key, string? path = null, bool tracked = false) =>
        new(key, null, path == null ? null : new RepoRegistryEntry { Key = key, Path = path, Tracked = tracked });

    private readonly List<RepoRecord> _records = new()
    {
        Record(new RepoKey(RepoProvider.AzureDevOps, "example-org", "Devices", "Tools"), @"C:\work\AzDevOps\Devices\Tools"),
        Record(new RepoKey(RepoProvider.AzureDevOps, "example-org", "Systems", "Portal")),
        Record(new RepoKey(RepoProvider.GitHub, "example-org", null, "tools")),
        Record(new RepoKey(RepoProvider.GitHub, "someone", null, "dotfiles"), @"C:\work\GitHub\someone\dotfiles"),
    };

    [Fact]
    public void ResolvesByUniqueName()
    {
        Assert.Equal("Portal", RepoResolver.Resolve("portal", _records).Key.Name);
        Assert.Equal("someone", RepoResolver.Resolve("DOTFILES", _records).Key.Owner);
    }

    [Fact]
    public void ResolvesByScopeAndName()
    {
        Assert.Equal(RepoProvider.AzureDevOps, RepoResolver.Resolve("Devices/Tools", _records).Key.Provider);
        Assert.Equal(RepoProvider.GitHub, RepoResolver.Resolve("example-org/tools", _records).Key.Provider);
        Assert.Equal("Devices", RepoResolver.Resolve("example-org/Devices/Tools", _records).Key.Project);
    }

    [Fact]
    public void ResolvesById()
    {
        Assert.Equal(RepoProvider.GitHub, RepoResolver.Resolve("github:example-org/tools", _records).Key.Provider);
        Assert.Equal(RepoProvider.AzureDevOps, RepoResolver.Resolve("AZDO:example-org/devices/tools", _records).Key.Provider);
    }

    [Fact]
    public void ResolvesByPath_InAnyCaseAndSlash()
    {
        Assert.Equal("dotfiles", RepoResolver.Resolve(@"c:\WORK\GitHub\someone\dotfiles\", _records).Key.Name);
        Assert.Equal("dotfiles", RepoResolver.Resolve("C:/work/GitHub/someone/dotfiles", _records).Key.Name);
    }

    [Fact]
    public void AmbiguousName_ListsCandidates()
    {
        var error = Assert.Throws<RepoException>(() => RepoResolver.Resolve("tools", _records));
        Assert.Equal(RepoErrorKind.Ambiguous, error.Kind);
        Assert.Equal(2, error.Candidates.Count);
        Assert.Contains(error.Candidates, c => c.Contains("Devices/Tools"));
        Assert.Contains(error.Candidates, c => c.Contains("example-org/tools"));
    }

    [Fact]
    public void UnknownName_IsNotFound() =>
        Assert.Equal(RepoErrorKind.NotFound, Assert.Throws<RepoException>(() => RepoResolver.Resolve("missing", _records)).Kind);

    [Fact]
    public void Merge_JoinsCatalogAndRegistry_WithTheCatalogSpelling()
    {
        var key = new RepoKey(RepoProvider.GitHub, "example-org", null, "tools");
        var catalog = new[] { new CatalogRepo { Key = key, CloneUrl = "https://github.com/example-org/tools.git", DefaultBranch = "main" } };
        var local = new RepoKey(RepoProvider.GitHub, "Example-Org", null, "Tools");
        var registry = new[] { new RepoRegistryEntry { Key = local, Path = @"C:\work\tools", Tracked = true } };
        var merged = RepoRecord.Merge(catalog, registry);
        Assert.Single(merged);
        Assert.True(merged[0].IsTracked);
        Assert.Equal("main", merged[0].DefaultBranch);
        Assert.Equal("example-org", merged[0].Key.Owner);
    }
}

public sealed class RepoRegistryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "repo-registry-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private RepoRegistryStore Store => new(Path.Combine(_directory, "repos.json"));

    [Fact]
    public void MissingFile_LoadsDefaults()
    {
        var document = Store.Load();
        Assert.Equal(RepoSettings.Default.ScanDepth, document.Settings.ScanDepth);
        Assert.Equal(new[] { "main", "master" }, document.Settings.ProtectedBranches);
        Assert.Empty(document.Repos);
        Assert.Equal(Path.Combine(_directory, "repos-catalog.json"), Store.CatalogPath);
    }

    [Fact]
    public void RoundTrip()
    {
        var key = new RepoKey(RepoProvider.AzureDevOps, "example-org", "Project", "Repo");
        var entry = new RepoRegistryEntry
        {
            Key = key, Path = @"C:\work\AzDevOps\Project\Repo", Tracked = true,
            RemoteUrl = "https://azure-devops.example.com/example-org/Project/_git/Repo",
            DefaultBranch = "main", AddedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
        };
        Store.Update(doc =>
        {
            doc.Repos[key.Id] = entry;
            doc.Settings.ScanRoots = new() { @"~\Code" };
            doc.Settings.GitHubOwners = new() { "example-org" };
        });
        var loaded = Store.Load();
        Assert.Equal(entry, loaded.Repos[key.Id]);
        Assert.Equal(new[] { @"~\Code" }, loaded.Settings.ScanRoots);
        Assert.Equal(new[] { "example-org" }, loaded.Settings.GitHubOwners);
        Assert.Equal(new[] { key }, loaded.TrackedEntries.Select(e => e.Key));
    }

    [Fact]
    public void FileUsesTheSharedJsonShape()
    {
        var key = new RepoKey(RepoProvider.GitHub, "example-org", null, "example-repo");
        Store.Update(doc => doc.Repos[key.Id] = new RepoRegistryEntry { Key = key, Path = @"C:\work\x", Tracked = true });
        var text = File.ReadAllText(Store.RegistryPath);
        Assert.Contains("\"github:example-org/example-repo\"", text);
        Assert.Contains("\"provider\": \"github\"", text);
        Assert.Contains("\"scanRoots\"", text);
    }

    [Fact]
    public void PartialSettings_FallBackToDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "repos.json"), """{"settings": {"cloneRoot": "~\\Code"}}""");
        var loaded = Store.Load();
        Assert.Equal(@"~\Code", loaded.Settings.CloneRoot);
        Assert.Equal(RepoSettings.Default.ScanDepth, loaded.Settings.ScanDepth);
        Assert.Equal(1, loaded.Version);
    }

    [Fact]
    public void CatalogCache_RoundTrips()
    {
        var catalog = new RepoCatalog
        {
            Repos = new()
            {
                new CatalogRepo
                {
                    Key = new RepoKey(RepoProvider.GitHub, "example-org", null, "example-repo"),
                    CloneUrl = "https://github.com/example-org/example-repo.git", DefaultBranch = "refs/heads/main", IsArchived = true,
                },
            },
            Errors = new() { "GitHub example-owner: HTTP 404" },
            FetchedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
        };
        Store.SaveCatalog(catalog);
        var loaded = Store.LoadCatalog()!;
        Assert.Equal(catalog.Repos, loaded.Repos);
        Assert.Equal("main", loaded.Repos[0].DefaultBranch);
        Assert.Equal(catalog.Errors, loaded.Errors);
    }

    [Fact]
    public void SettingsExpand_ResolvesHome() =>
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Developer"),
            RepoSettings.Expand(@"~\Developer"));
}

public class GitOutputParserTests
{
    private static readonly string Oid = new('a', 40);
    private static readonly string Oid2 = new('b', 40);

    [Fact]
    public void Status_HeaderAndChanges()
    {
        var records = new[]
        {
            $"# branch.oid {Oid}",
            "# branch.head feature/example",
            "# branch.upstream origin/feature/example",
            "# branch.ab +2 -1",
            $"1 M. N... 100644 100644 100644 {Oid} {Oid2} Sources/Staged.cs",
            $"1 .M N... 100644 100644 100644 {Oid} {Oid} Sources/Modified file.cs",
            $"1 MM N... 100644 100644 100644 {Oid} {Oid2} both.txt",
            $"1 A. N... 000000 100644 100644 {new string('0', 40)} {Oid2} added.txt",
            $"2 R. N... 100644 100644 100644 {Oid} {Oid} R100 new/name.txt",
            "old/name.txt",
            $"u UU N... 100644 100644 100644 100644 {Oid} {Oid2} {Oid} conflict.txt",
            "? notes/todo.md",
            "! build/output.log",
        };
        var snapshot = GitOutputParser.Status(string.Join("\0", records) + "\0");

        Assert.Equal(Oid, snapshot.HeadOid);
        Assert.Equal("feature/example", snapshot.Branch);
        Assert.Equal("origin/feature/example", snapshot.Upstream);
        Assert.Equal(2, snapshot.Ahead);
        Assert.Equal(1, snapshot.Behind);
        Assert.Equal(8, snapshot.Changes.Count);
        Assert.Equal("Sources/Modified file.cs", snapshot.Changes[1].Path);
        var rename = snapshot.Changes[4];
        Assert.Equal(RepoChangeKind.Renamed, rename.Kind);
        Assert.Equal("new/name.txt", rename.Path);
        Assert.Equal("old/name.txt", rename.OriginalPath);
        Assert.Equal(RepoChangeKind.Unmerged, snapshot.Changes[5].Kind);
        Assert.Equal(RepoChangeKind.Untracked, snapshot.Changes[6].Kind);
        Assert.Equal(RepoChangeKind.Ignored, snapshot.Changes[7].Kind);
        // Staged: M., MM, A., R., UU. Unstaged: .M, MM, UU.
        Assert.Equal(5, snapshot.StagedCount);
        Assert.Equal(3, snapshot.UnstagedCount);
        Assert.Equal(1, snapshot.UntrackedCount);
        Assert.Equal(1, snapshot.ConflictedCount);
        Assert.False(snapshot.IsClean);
    }

    [Fact]
    public void Status_DetachedInitialAndClean()
    {
        var snapshot = GitOutputParser.Status(string.Join("\0", "# branch.oid (initial)", "# branch.head (detached)", "! ignored.log") + "\0");
        Assert.Null(snapshot.HeadOid);
        Assert.Null(snapshot.Branch);
        Assert.Null(snapshot.Upstream);
        Assert.Equal(0, snapshot.Ahead);
        Assert.True(snapshot.IsClean);
    }

    [Fact]
    public void FileChange_JsonCarriesStagedFlags()
    {
        var change = new RepoFileChange { Path = "a.txt", Kind = RepoChangeKind.Changed, IndexStatus = "M", WorktreeStatus = "." };
        var json = JsonSerializer.Serialize(change, RepoRegistryStore.Json);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("staged").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("unstaged").GetBoolean());
        Assert.Equal("changed", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal(change, JsonSerializer.Deserialize<RepoFileChange>(json, RepoRegistryStore.Json));
    }

    [Fact]
    public void Worktrees()
    {
        var output = $"worktree C:/work/example-repo\nHEAD {Oid}\nbranch refs/heads/main\n\n" +
                     $"worktree C:/work/example-repo/.worktrees/feature\nHEAD {Oid2}\nbranch refs/heads/feature/example\nlocked\n\n" +
                     $"worktree C:/work/example-repo/.worktrees/review\nHEAD {Oid}\ndetached\nprunable gitdir file points to non-existent location\n\n";
        var worktrees = GitOutputParser.Worktrees(output);
        Assert.Equal(3, worktrees.Count);
        Assert.Equal("main", worktrees[0].Branch);
        Assert.Equal("feature/example", worktrees[1].Branch);
        Assert.True(worktrees[1].IsLocked);
        Assert.Null(worktrees[2].Branch);
        Assert.True(worktrees[2].IsDetached);
        Assert.True(worktrees[2].IsPrunable);
    }

    [Fact]
    public void Log_DropsMalformedRecords()
    {
        var output = string.Join("\u001e\n", new[]
        {
            string.Join("\u001f", Oid, "aaaaaaa", "Example Author", "author@example.com", "2026-01-02T03:04:05-08:00", "Add the first thing"),
            string.Join("\u001f", Oid2, "bbbbbbb", "Other Author", "other@example.com", "2026-01-01T00:00:00Z", "Subject with \u001f? no"),
        }) + "\u001e\n";
        var commits = GitOutputParser.Log(output);
        Assert.Single(commits);
        Assert.Equal("aaaaaaa", commits[0].ShortSha);
        Assert.Equal("Add the first thing", commits[0].Subject);
        Assert.NotNull(commits[0].Date);
    }

    [Fact]
    public void Grep()
    {
        var matches = GitOutputParser.Grep("Sources/App.cs\u000012\u00005\u0000var needle = 1\nREADME.md\u00003\u00001\u0000needle: with: colons\n");
        Assert.Equal(new[]
        {
            new RepoGrepMatch("Sources/App.cs", 12, 5, "var needle = 1"),
            new RepoGrepMatch("README.md", 3, 1, "needle: with: colons"),
        }, matches);
    }

    [Fact]
    public void Paths() => Assert.Equal(new[] { "a.txt", "dir/b c.txt" }, GitOutputParser.Paths("a.txt\0dir/b c.txt\0"));
}

/// <summary>A throwaway repository in a temporary folder, so staging, discarding and the branch guard run real git.</summary>
public abstract class TempGitRepository : IAsyncLifetime
{
    /// <summary>A folder of its own around the checkout, so a discovery scan of it sees nothing else.</summary>
    protected string Parent { get; } = Path.Combine(Path.GetTempPath(), "fleetmate-git-" + Guid.NewGuid().ToString("N"));
    protected string Root => Path.Combine(Parent, "repo");
    protected GitWorkingCopy Copy { get; private set; } = null!;

    public virtual async Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        Copy = new GitWorkingCopy(Root);
        foreach (var args in new[]
        {
            new[] { "init", "-q", "-b", "main" },
            new[] { "config", "user.email", "dev@example.com" },
            new[] { "config", "user.name", "Dev" },
            new[] { "config", "commit.gpgsign", "false" },
            new[] { "config", "core.autocrlf", "false" },
        })
        {
            var result = await Copy.GitAsync(args);
            Assert.True(result.Succeeded, result.StandardError);
        }
    }

    public Task DisposeAsync()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Parent, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Parent, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    protected void Write(string name, string text) => Copy.WriteFile(name, Encoding.UTF8.GetBytes(text));

    protected string Read(string name) => Encoding.UTF8.GetString(Copy.ReadFile(name));
}

public sealed class GitWorkingCopyTests : TempGitRepository
{
    private static readonly IReadOnlySet<string> Main = new HashSet<string> { "main" };

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Write("tracked.txt", "one\n");
        Write(".gitignore", "*.log\n");
        await Copy.CommitAsync("Initial", null, new HashSet<string>(), allowProtected: true);
    }

    [Fact]
    public async Task ProtectedBranch_IsRefusedForCommitAndPush()
    {
        Write("tracked.txt", "two\n");
        var refused = await Assert.ThrowsAsync<RepoException>(() => Copy.CommitAsync("Change", null, Main));
        Assert.Equal(RepoErrorKind.ProtectedBranch, refused.Kind);
        var push = await Assert.ThrowsAsync<RepoException>(() => Copy.PushAsync(Main));
        Assert.Equal(RepoErrorKind.ProtectedBranch, push.Kind);

        await Copy.SwitchBranchAsync("feature/example");
        var commit = await Copy.CommitAsync("Change", null, Main);
        Assert.Equal("Change", commit.Subject);
        Assert.Equal("feature/example", await Copy.CurrentBranchAsync());
    }

    [Fact]
    public async Task StageUnstageDiscardAndDiff()
    {
        Write("tracked.txt", "two\n");
        Write("new.txt", "fresh\n");
        Write("debug.log", "ignored\n");

        var status = await Copy.StatusAsync();
        Assert.Equal(1, status.UnstagedCount);
        Assert.Equal(1, status.UntrackedCount);

        Assert.Equal(new HashSet<string> { ".gitignore", "tracked.txt", "new.txt" }, (await Copy.ListFilesAsync()).ToHashSet());

        await Copy.StageAsync(new[] { "tracked.txt" });
        Assert.Equal(1, (await Copy.StatusAsync()).StagedCount);
        Assert.Contains("+two", await Copy.FileDiffAsync("tracked.txt", staged: true));
        Assert.Contains("+fresh", await Copy.FileDiffAsync("new.txt"));

        await Copy.UnstageAsync(new[] { "tracked.txt" });
        Assert.Equal(0, (await Copy.StatusAsync()).StagedCount);

        await Copy.DiscardAsync(new[] { "tracked.txt", "new.txt" });
        Assert.True((await Copy.StatusAsync()).IsClean);
        Assert.Equal("one\n", Read("tracked.txt"));
    }

    [Fact]
    public async Task GrepLogAndLastCommit()
    {
        Assert.Equal("tracked.txt", (await Copy.GrepAsync("one")).First().Path);
        Assert.Empty(await Copy.GrepAsync("absent-text"));
        Assert.Equal(new[] { "Initial" }, (await Copy.LogAsync(5)).Select(c => c.Subject));
        Assert.NotNull(await Copy.LastCommitDateAsync());
        Assert.Equal("main", await Copy.CurrentBranchAsync());
    }

    [Fact]
    public void PathsOutsideTheCheckout_AreRefused()
    {
        Assert.Throws<RepoException>(() => Copy.ConfinedPath(@"..\outside.txt"));
        Assert.Throws<RepoException>(() => Copy.ConfinedPath("../outside.txt"));
        Assert.Throws<RepoException>(() => Copy.ConfinedPath(@"C:\Windows\win.ini"));
        Assert.Throws<RepoException>(() => Copy.ConfinedPath(".git/config"));
        Assert.Throws<RepoException>(() => Copy.ConfinedPath(@".GIT\hooks\pre-commit"));
        Assert.Throws<RepoException>(() => Copy.ConfinedPath("sub/.Git/config"));
        Assert.Throws<RepoException>(() => Copy.ConfinedPath(".git./config"));
        Assert.Throws<RepoException>(() => Copy.ConfinedPath("GIT~1/config"));
        Assert.Throws<RepoException>(() => Copy.ConfinedPath("tracked.txt:hidden"));
        Assert.Equal("tracked.txt", Copy.ConfinedPath("dir/../tracked.txt"));
        Assert.Equal("dir/sub/file.txt", Copy.ConfinedPath(@"dir\sub\file.txt"));
    }

    [Fact]
    public async Task PathspecMagicInAFileName_IsLiteral()
    {
        Write("tracked.txt", "changed\n");
        try { await Copy.DiscardAsync(new[] { ":(top)" }); } catch (RepoException) { }
        Assert.Equal("changed\n", Read("tracked.txt"));
    }

    [Fact]
    public void NewFilesUnderAJunctionOutOfTheCheckout_AreRefused()
    {
        var outside = Path.Combine(Path.GetTempPath(), "fleetmate-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var link = Path.Combine(Root, "escape");
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (IOException) { CreateJunction(link, outside); }
            catch (UnauthorizedAccessException) { CreateJunction(link, outside); }

            Assert.Throws<RepoException>(() => Copy.ConfinedPath("escape/new-file.txt"));
            Assert.Throws<RepoException>(() => Copy.WriteFile("escape/deeper/new-file.txt", "x"u8.ToArray()));
            Assert.False(Directory.Exists(Path.Combine(outside, "deeper")));
            Assert.Equal("brand/new/file.txt", Copy.ConfinedPath("brand/new/file.txt"));
        }
        finally
        {
            var link = Path.Combine(Root, "escape");
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(outside, true);
        }
    }

    private static void CreateJunction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
    }
}

/// <summary>The CLI end to end against a temporary registry and repository: nothing touches the user's own registry.</summary>
public sealed class ReposCommandTests : TempGitRepository
{
    private readonly string _registry = Path.Combine(Path.GetTempPath(), "fleetmate-repos-" + Guid.NewGuid().ToString("N"));
    private RepoManager Manager => new(new RepoRegistryStore(Path.Combine(_registry, "repos.json")));

    private RootCommand Cli()
    {
        var root = new RootCommand("FleetMate") { Name = "fleetmate" };
        root.AddCommand(ReposCommand.Create(new FleetMateConfig(), Manager));
        return root;
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Write("tracked.txt", "one\n");
        await Copy.CommitAsync("Initial", null, new HashSet<string>(), allowProtected: true);
        Assert.True((await Copy.GitAsync(new[] { "remote", "add", "origin", "https://github.com/example-org/example-repo.git" })).Succeeded);
    }

    [Theory]
    [InlineData("repos")]
    [InlineData("repos catalog --cached --provider github -f tool --json")]
    [InlineData("repos list --all -j")]
    [InlineData("repos discover --root C:\\work --depth 2 --track")]
    [InlineData("repos link example-repo C:\\work\\x --untracked")]
    [InlineData("repos clone example-org/example-repo --ssh --into C:\\work\\y")]
    [InlineData("repos status a b --files --json")]
    [InlineData("repos fetch")]
    [InlineData("repos pull a --json")]
    [InlineData("repos push a --allow-main")]
    [InlineData("repos commit a -m \"Message\" one.txt two.txt --allow-main")]
    [InlineData("repos branch a feature/x --from origin/main")]
    [InlineData("repos diff a --staged --stat")]
    [InlineData("repos log a -n 5 --ref main --json")]
    [InlineData("repos stats --since 30d --by week --top 5 --json")]
    [InlineData("repos files a --under src")]
    [InlineData("repos grep a needle -i -F --limit 5")]
    [InlineData("repos settings --scan-root C:\\a --scan-root C:\\b --depth 3 --github-owner example-org --concurrency 4")]
    [InlineData("repos untrack a b")]
    public void CommandLines_Parse(string line) => Assert.Empty(Cli().Parse(line).Errors);

    [Fact]
    public async Task LinkCommitRefusalAndStatus()
    {
        Environment.ExitCode = 0;
        try
        {
            Assert.Equal(0, await Cli().InvokeAsync($"repos link \"{Root}\""));
            Assert.Equal(0, Environment.ExitCode);
            var record = Manager.Resolve("example-repo");
            Assert.True(record.IsTracked);

            Write("tracked.txt", "two\n");
            await Cli().InvokeAsync("repos commit example-repo -m Change");
            Assert.Equal(1, Environment.ExitCode);
            Assert.Equal("one\n", Encoding.UTF8.GetString(Copy.ReadFile("tracked.txt")) == "two\n" ? "one\n" : "changed");
            Assert.Equal(1, (await Copy.LogAsync(10)).Count);

            Environment.ExitCode = 0;
            await Cli().InvokeAsync("repos branch example-repo feature/example");
            await Cli().InvokeAsync("repos commit example-repo -m Change");
            Assert.Equal(0, Environment.ExitCode);
            Assert.Equal("Change", (await Copy.LogAsync(1))[0].Subject);

            var status = await Manager.StatusAsync(record);
            Assert.Equal("feature/example", status.Branch);
            Assert.True(status.IsClean);
            Assert.NotNull(status.LastCommitAt);

            await Cli().InvokeAsync("repos untrack example-repo");
            Assert.False(Manager.Resolve("example-repo").IsTracked);
            await Cli().InvokeAsync("repos unlink example-repo");
            Assert.Equal(RepoErrorKind.NotFound, Assert.Throws<RepoException>(() => Manager.Resolve("example-repo")).Kind);
            Assert.True(Directory.Exists(Root), "unlink leaves the folder alone");
        }
        finally
        {
            Environment.ExitCode = 0;
            if (Directory.Exists(_registry)) Directory.Delete(_registry, true);
        }
    }

    [Fact]
    public async Task Discover_LinksCheckoutsUnderTheRoot_AndSkipsWorktrees()
    {
        try
        {
            var wt = Path.Combine(Root, ".worktrees", "side");
            Assert.True((await Copy.GitAsync(new[] { "worktree", "add", "-q", "-b", "side", wt })).Succeeded);
            var settings = RepoSettings.Default;
            settings.ScanRoots = new() { Parent };
            settings.ScanDepth = 1;
            var results = await Manager.DiscoverAsync(track: true, settings);
            var mine = Assert.Single(results);
            Assert.Equal(DiscoveryAction.Linked, mine.Action);
            Assert.Equal("github:example-org/example-repo", mine.Id);
            Assert.DoesNotContain(results, r => r.Path.Contains(".worktrees"));
            Assert.True(Manager.Resolve("example-org/example-repo").IsTracked);
        }
        finally
        {
            if (Directory.Exists(_registry)) Directory.Delete(_registry, true);
        }
    }
}
