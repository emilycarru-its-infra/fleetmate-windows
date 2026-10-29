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

    /// <summary>
    /// Each case is a whole config and whether it must be flagged. The same
    /// table runs on the Mac; it includes every bypass reviews found.
    /// </summary>
    public static readonly TheoryData<string, string, bool> ConfigCases = new()
    {
        { "typical clone", "[core]\n\trepositoryformatversion = 0\n\tfilemode = true\n\tbare = false\n\tlogallrefupdates = true\n[remote \"origin\"]\n\turl = https://github.com/owner-a/one.git\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n[branch \"main\"]\n\tremote = origin\n\tmerge = refs/heads/main\n", false },
        { "crlf line endings", "[core]\r\n\tbare = false\r\n", false },
        { "bare boolean key", "[core]\n\tbare\n", false },
        { "comment lines", "# note\n; note\n[core]\n\tbare = false\n", false },
        { "editor branch notes", "[branch \"feature/x\"]\n\tvscode-merge-base = origin/main\n", false },
        { "submodule update rebase", "[submodule \"lib\"]\n\turl = https://github.com/owner-a/lib.git\n\tupdate = rebase\n", false },
        { "fsmonitor false", "[core]\n\tfsmonitor = false\n", false },
        { "fsmonitor program", "[core]\n\tfsmonitor = ./watch.sh\n", true },
        { "fsmonitor true", "[core]\n\tfsmonitor = true\n", true },
        { "hooksPath", "[core]\n\thooksPath = .husky\n", true },
        { "sshCommand", "[core]\n\tsshCommand = ssh -i key\n", true },
        { "pager", "[core]\n\tpager = less\n", true },
        { "core.worktree", "[core]\n\tworktree = /elsewhere\n", true },
        { "credential helper", "[credential]\n\thelper = store\n", true },
        { "textconv", "[diff \"img\"]\n\ttextconv = exiftool\n", true },
        { "filter", "[filter \"lfs\"]\n\tprocess = git-lfs filter-process\n", true },
        { "uploadpack", "[remote \"origin\"]\n\turl = /tmp/x\n\tuploadpack = sh -c x\n", true },
        { "protocol allow", "[protocol \"ext\"]\n\tallow = always\n", true },
        { "include", "[include]\n\tpath = other\n", true },
        { "includeIf", "[includeIf \"gitdir:/work/\"]\n\tpath = other\n", true },
        { "worktreeConfig", "[extensions]\n\tworktreeConfig = true\n", true },
        { "unknown section", "[somethingnew]\n\tvalue = 1\n", true },
        { "remote key without subsection", "[remote]\n\turl = x\n", true },
        { "submodule update command", "[submodule \"lib\"]\n\tupdate = !sh -c x\n", true },
        { "submodule update quoted empty then command", "[submodule \"lib\"]\n\tupdate = \"\"!sh -c x\n", true },
        { "submodule update other value", "[submodule \"lib\"]\n\tupdate = merge-ish\n", true },
        { "url continuation hiding a header", "[remote \"origin\"]\n\turl = x \\\n[user]\n\tpager = less\n", true },
        { "continuation backslash", "[user]\n\tname = Someone \\\n[core]\n\tpager = less\n", true },
        { "quoted value", "[user]\n\tname = \"Some one\"\n", true },
        { "quote spanning lines", "[user]\n\tname = \"Some\n\tone\"\n", true },
        { "inline comment", "[core]\n\tbare = false # note\n", true },
        { "bare carriage return splits a line", "[user]\n\tname = x\r\tpager = less\n[core]\n", true },
        { "unicode line separator", "[user]\n\tname = x\u2028[core]\n\tpager = less\n", true },
        { "unicode paragraph separator", "[user]\n\tname = x\u2029\n", true },
        { "non-ascii value", "[user]\n\tname = Zoë\n", true },
        { "control character", "[user]\n\tname = x\u000By\n", true },
        { "nul byte", "[user]\n\tname = x\0\n", true },
        { "key outside a section", "name = x\n", true },
        { "malformed header", "[core\n\tbare = false\n", true },
        { "bad key name", "[user]\n\tna me = x\n", true },
        { "bad section name", "[us!er]\n\tname = x\n", true },
        { "escaped quote in subsection", "[branch \"a\\\"b\"]\n\tremote = origin\n", true },
        { "commit gpgsign true", "[commit]\n\tgpgsign = true\n", false },
        { "commit gpgsign false", "[commit]\n\tgpgSign = false\n", false },
        { "commit gpgsign bare", "[commit]\n\tgpgsign\n", false },
        { "commit gpgsign quoted", "[commit]\n\tgpgsign = \"true\"\n", true },
        { "commit gpgsign other value", "[commit]\n\tgpgsign = yes-please\n", true },
        { "gpg program", "[gpg]\n\tprogram = /usr/local/bin/gpg\n", true },
        { "gpg format", "[gpg]\n\tformat = ssh\n", true },
        { "gpg ssh program", "[gpg \"ssh\"]\n\tprogram = ssh-keygen\n", true },
        { "user signingkey", "[user]\n\tsigningkey = ABC123\n", true },
    };

    [Theory]
    [MemberData(nameof(ConfigCases))]
    public void ConfigReviewTable(string name, string config, bool flagged)
    {
        var reasons = RepoDiscovery.CommandConfigKeys(config);
        Assert.True(flagged == reasons.Count > 0, $"{name}: [{string.Join(", ", reasons)}]");
    }

    [Fact]
    public void ConfigCases_MatchTheMacTable() => Assert.Equal(51, ConfigCases.Count);

    [Fact]
    public void UserKeys_AreOnlyNameEmailAndUseConfigOnly()
    {
        Assert.Empty(RepoDiscovery.CommandConfigKeys("[user]\n\tname = Dev\n\temail = dev@example.com\n\tuseConfigOnly = true\n"));
        Assert.Equal(new[] { "user.signingkey" }, RepoDiscovery.CommandConfigKeys("[user]\n\tsigningkey = ABC123\n"));
        Assert.Equal(new[] { "user.other" }, RepoDiscovery.CommandConfigKeys("[user]\n\tother = x\n"));
    }

    [Fact]
    public void InvalidUtf8_IsFlagged() =>
        Assert.NotEmpty(RepoDiscovery.ConfigReview(new byte[] { 0x5B, 0x63, 0x5D, 0x0A, 0xC3, 0x28 }));

    [Fact]
    public void EntryPattern_RejectsBackslash()
    {
        Assert.False(RepoDiscovery.EntryPattern.IsMatch("\turl = x \\"));
        Assert.Equal(new[] { "(continuation line)", "user.pager" },
            RepoDiscovery.CommandConfigKeys("[remote \"origin\"]\n\turl = x \\\n[user]\n\tpager = less\n"));
    }

    [Fact]
    public void FlaggedKeys_AreNamed() =>
        Assert.Equal(new[] { "core.pager", "diff.img.textconv" },
            RepoDiscovery.CommandConfigKeys("[core]\n\tpager = less\n[diff \"img\"]\n\ttextconv = x\n"));

    [Fact]
    public void APlainConfig_IsNotFlagged()
    {
        var config = string.Join("\n",
            "[core]", "\trepositoryformatversion = 0", "\tfilemode = true", "\tbare = false", "\tlogallrefupdates = true",
            "\tignorecase = true", "\tprecomposeunicode = true", "\tfsmonitor = false",
            "[remote \"origin\"]", "\turl = https://github.com/owner-a/one.git", "\tfetch = +refs/heads/*:refs/remotes/origin/*",
            "[branch \"main\"]", "\tremote = origin", "\tmerge = refs/heads/main", "\tvscode-merge-base = origin/main",
            "[submodule]", "\tactive = .",
            "[submodule \"lib\"]", "\turl = https://github.com/owner-a/lib.git", "\tactive = true",
            "[user]", "\tname = Someone", "[pull]", "\trebase = true", "[lfs]", "\trepositoryformatversion = 0",
            "[extensions]", "\tobjectformat = sha256", "[init]", "\tdefaultBranch = main");
        Assert.Empty(RepoDiscovery.CommandConfigKeys(config));
    }

    private string GitDir(string name)
    {
        var dir = Dir(name, ".git");
        File.WriteAllText(Path.Combine(dir, "config"), "[core]\n\tbare = false\n");
        return dir;
    }

    [Fact]
    public void APlainClone_HasNoReasons()
    {
        var dir = GitDir("plain");
        Directory.CreateDirectory(Path.Combine(dir, "hooks"));
        File.WriteAllText(Path.Combine(dir, "hooks", "pre-commit.sample"), "#!/bin/sh\n");
        Assert.Empty(RepoDiscovery.CommandConfig(Path.Combine(Root, "plain")));
    }

    [Fact]
    public void Hooks_AreFlagged_EveryNonSampleFileOnWindows()
    {
        var dir = GitDir("hooked");
        Directory.CreateDirectory(Path.Combine(dir, "hooks"));
        File.WriteAllText(Path.Combine(dir, "hooks", "pre-commit.sample"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(dir, "hooks", "post-checkout"), "#!/bin/sh\n");
        Assert.Equal(new[] { "hooks/post-checkout" }, RepoDiscovery.CommandConfig(Path.Combine(Root, "hooked")));
    }

    [Fact]
    public void AnUnreadableConfig_FailsClosed()
    {
        var dir = GitDir("garbled");
        File.WriteAllBytes(Path.Combine(dir, "config"), new byte[] { 0xFF, 0xFE, 0x00, 0xC3, 0x28 });
        Assert.Equal(new[] { "(characters outside plain ASCII)" }, RepoDiscovery.CommandConfig(Path.Combine(Root, "garbled")));
        var worktree = GitDir("garbled-worktree");
        File.WriteAllBytes(Path.Combine(worktree, "config.worktree"), new byte[] { 0xC3, 0x28 });
        Assert.Equal(new[] { "(characters outside plain ASCII)" }, RepoDiscovery.CommandConfig(Path.Combine(Root, "garbled-worktree")));
    }

    [Fact]
    public void Commondir_IsFlagged_AndItsConfigRead()
    {
        var shared = GitDir("shared");
        File.WriteAllText(Path.Combine(shared, "config"), "[core]\n\tpager = less\n");
        var linked = GitDir("linked");
        File.WriteAllText(Path.Combine(linked, "commondir"), "../../shared/.git\n");
        Assert.Equal(new[] { "commondir", "core.pager" }, RepoDiscovery.CommandConfig(Path.Combine(Root, "linked")));
    }

    [Theory]
    [InlineData("wtc", "[extensions]\n\tworktreeConfig = true\n")]
    [InlineData("inc", "[include]\n\tpath = other\n")]
    [InlineData("incif", "[includeIf \"onbranch:main\"]\n\tpath = other\n")]
    [InlineData("cwt", "[core]\n\tworktree = /elsewhere\n")]
    public void WorktreeConfigIncludeAndCoreWorktree_AreFlagged(string name, string text)
    {
        File.WriteAllText(Path.Combine(GitDir(name), "config"), text);
        Assert.NotEmpty(RepoDiscovery.CommandConfig(Path.Combine(Root, name)));
    }

    [Fact]
    public void ConfigWorktree_IsReadToo()
    {
        var dir = GitDir("wtc2");
        File.WriteAllText(Path.Combine(dir, "config"), "[extensions]\n\tworktreeConfig = true\n");
        File.WriteAllText(Path.Combine(dir, "config.worktree"), "[core]\n\tsshCommand = x\n");
        Assert.Equal(new[] { "extensions.worktreeconfig", "core.sshcommand" }, RepoDiscovery.CommandConfig(Path.Combine(Root, "wtc2")));
    }

    [Fact]
    public void CommandConfig_FlagsAnUnreadableGitPointer()
    {
        var checkout = Dir("pointer");
        File.WriteAllText(Path.Combine(checkout, ".git"), "not a gitdir line\n");
        Assert.Equal(new[] { "(unreadable .git)" }, RepoDiscovery.CommandConfig(checkout));
    }

    [Fact]
    public async Task Discovery_FlagsAClone_FromConfigAndConfigWorktree()
    {
        var plain = FakeCheckout("plain");
        File.WriteAllText(Path.Combine(plain, ".git", "config"), "[remote \"origin\"]\n\turl = https://github.com/example-org/plain.git\n");
        var flagged = FakeCheckout("flagged");
        File.WriteAllText(Path.Combine(flagged, ".git", "config"), "[remote \"origin\"]\n\turl = https://github.com/example-org/flagged.git\n");
        File.WriteAllText(Path.Combine(flagged, ".git", "config.worktree"), "[core]\n\tfsmonitor = ./watch\n");

        var found = await RepoDiscovery.DiscoverAsync(new RepoSettings { ScanRoots = new() { Root } });
        Assert.False(found.Single(c => c.Key?.Name == "plain").RunsCommands);
        Assert.Equal(new[] { "core.fsmonitor" }, found.Single(c => c.Key?.Name == "flagged").CommandConfig);
    }

    [Theory]
    [InlineData(@" C:\\work\\remote.git", @"C:\work\remote.git")]
    [InlineData(" \"https://example.com/a b.git\" # note", "https://example.com/a b.git")]
    [InlineData(" https://example.com/x.git ; trailing", "https://example.com/x.git")]
    public void ConfigValues_AreUnescapedAsGitReadsThem(string raw, string expected) =>
        Assert.Equal(expected, RepoDiscovery.ConfigValue(raw));

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

    private DiscoveredCheckout Flagged(string name) => Found(name) with { CommandConfig = new() { "core.fsmonitor" } };

    [Fact]
    public void AFlaggedClone_IsLinkedUntracked_UnlessTrusted()
    {
        var manager = Manager;
        var results = manager.Register(new[] { Flagged("risky") }, track: true);
        Assert.Equal(DiscoveryAction.NeedsReview, results.Single().Action);
        Assert.False(manager.Resolve("risky").IsTracked);

        manager.Register(new[] { Flagged("risky") }, track: true);
        Assert.False(manager.Resolve("risky").IsTracked);

        manager.Register(new[] { Flagged("risky") }, track: true, trustCommandConfig: true);
        Assert.True(manager.Resolve("risky").IsTracked);
    }

    [Fact]
    public async Task TrackFolders_HoldsFlaggedClonesForReview_AndReplacesTheDefaultRoot()
    {
        var manager = Manager;
        WriteOrigin(FakeCheckout("code", "plain"), "plain");
        var risky = FakeCheckout("code", "risky");
        File.WriteAllText(Path.Combine(risky, ".git", "config"),
            "[remote \"origin\"]\n\turl = https://github.com/example-org/risky.git\n[filter \"x\"]\n\tsmudge = ./s\n");

        var (results, review) = await manager.TrackFoldersAsync(new[] { Path.Combine(Root, "code") });

        Assert.Equal(new[] { "risky" }, review.Select(c => c.Key!.Name));
        Assert.Equal(new[] { "filter.x.smudge" }, review.Single().CommandConfig);
        Assert.Single(results);
        Assert.True(manager.Resolve("plain").IsTracked);
        Assert.Throws<RepoException>(() => manager.Resolve("risky"));
        Assert.Equal(new[] { RepoSettings.Abbreviate(Path.Combine(Root, "code")) }, manager.Settings().ScanRoots);
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

    [Theory]
    [InlineData("status", true)]
    [InlineData("log", true)]
    [InlineData("worktree", true)]
    [InlineData("commit", false)]
    [InlineData("push", false)]
    [InlineData("pull", false)]
    [InlineData("switch", false)]
    [InlineData("cherry-pick", false)]
    public void SafetyConfig_KeepsHooksOnlyForCommandsPeopleRun(string command, bool hooksOff)
    {
        var config = GitWorkingCopy.SafetyConfig(new[] { command });
        Assert.Contains("core.fsmonitor=false", config);
        Assert.Contains("core.pager=cat", config);
        Assert.Equal(hooksOff, config.Any(a => a.StartsWith("core.hooksPath=")));
    }

    [Fact]
    public void DiffShowLogAndGrep_NeverRunExternalPrograms()
    {
        Assert.Equal(new[] { "diff", "--no-ext-diff", "--no-textconv", "--cached" }, GitWorkingCopy.WithSafeDiffOptions(new[] { "diff", "--cached" }));
        Assert.Equal(new[] { "log", "--no-ext-diff", "--no-textconv", "-1" }, GitWorkingCopy.WithSafeDiffOptions(new[] { "log", "-1" }));
        Assert.Equal(new[] { "show", "--no-ext-diff", "--no-textconv", "HEAD" }, GitWorkingCopy.WithSafeDiffOptions(new[] { "show", "HEAD" }));
        Assert.Equal(new[] { "grep", "--no-textconv", "x" }, GitWorkingCopy.WithSafeDiffOptions(new[] { "grep", "x" }));
        Assert.Equal(new[] { "status" }, GitWorkingCopy.WithSafeDiffOptions(new[] { "status" }));
    }

    [Fact]
    public async Task AClonesHook_DoesNotRunDuringAStatusRead()
    {
        var hooks = Directory.CreateDirectory(Path.Combine(_parent, "hooks")).FullName;
        var marker = Path.Combine(_parent, "hook-ran");
        var script = Path.Combine(hooks, "post-index-change");
        File.WriteAllText(script, $"#!/bin/sh\necho ran > '{marker.Replace('\\', '/')}'\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await Git(Clone, "config", "core.hooksPath", hooks);
        File.WriteAllText(Path.Combine(Clone, "README.md"), "changed\n");
        await new GitWorkingCopy(Clone).StatusAsync();
        await new GitWorkingCopy(Clone).GitAsync(new[] { "update-index", "--refresh" });
        Assert.False(File.Exists(marker));
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
