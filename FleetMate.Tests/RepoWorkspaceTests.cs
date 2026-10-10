using System.Text;
using FleetMate.Core.Services.Repos;
using FleetMate.Core.Shared;
using Xunit;

namespace FleetMate.Tests;

public class RepoFileTreeTests
{
    private static readonly string[] Paths =
    {
        "README.md",
        "Sources/App/main.cs",
        "Sources/App/Views/Editor.cs",
        "Sources/Core/Model.cs",
        "Package.cs",
        ".gitignore",
        "docs/guide.md",
        "Sources/App/file10.cs",
        "Sources/App/file2.cs",
    };

    [Fact]
    public void FoldersFirst_ThenFiles_InNaturalOrder()
    {
        var tree = RepoFileTree.Build(Paths);
        Assert.Equal(new[] { "docs", "Sources", ".gitignore", "Package.cs", "README.md" }, tree.Select(n => n.Name));
        var app = tree[1].Children![0];
        Assert.Equal("Sources/App", app.Path);
        Assert.Equal(new[] { "Views", "file2.cs", "file10.cs", "main.cs" }, app.Children!.Select(n => n.Name));
        Assert.Equal("Sources/App/Views/Editor.cs", app.Children![0].Children![0].Path);
    }

    [Fact]
    public void FilesHaveNoChildren_AndFoldersDo()
    {
        var tree = RepoFileTree.Build(Paths);
        Assert.True(tree[0].IsFolder);
        Assert.Null(tree[^1].Children);
        Assert.Equal(Paths.Length, RepoFileTree.FileCount(tree));
    }

    [Fact]
    public void IgnoresEmptyAndDuplicateSeparators()
    {
        var tree = RepoFileTree.Build(new[] { "", "a//b.txt", "a/b.txt" });
        Assert.Single(tree);
        Assert.Equal(new[] { "a/b.txt" }, tree[0].Children!.Select(n => n.Path));
    }

    [Fact]
    public void Filter_KeepsMatchingFilesAndTheirFolders()
    {
        var filtered = RepoFileTree.Filter(RepoFileTree.Build(Paths), "editor");
        Assert.Equal(1, RepoFileTree.FileCount(filtered));
        Assert.Equal(new HashSet<string> { "Sources", "Sources/App", "Sources/App/Views" }, RepoFileTree.FolderPaths(filtered));
    }

    [Fact]
    public void FilterOnAFolderName_KeepsItsContents()
    {
        var filtered = RepoFileTree.Filter(RepoFileTree.Build(Paths), "DOCS");
        Assert.Equal(new[] { "docs" }, filtered.Select(n => n.Path));
        Assert.Equal(new[] { "docs/guide.md" }, filtered[0].Children!.Select(n => n.Path));
    }

    [Fact]
    public void Filter_MatchesThroughThePath() =>
        Assert.Equal(1, RepoFileTree.FileCount(RepoFileTree.Filter(RepoFileTree.Build(Paths), "core/mod")));

    [Fact]
    public void VisibleRows_DescendOnlyIntoExpandedFolders()
    {
        var tree = RepoFileTree.Build(Paths);
        var collapsed = RepoFileTree.VisibleRows(tree, new HashSet<string>());
        Assert.Equal(new[] { "docs", "Sources", ".gitignore", "Package.cs", "README.md" }, collapsed.Select(r => r.Node.Path));
        Assert.All(collapsed, r => Assert.True(r.Depth == 0 && !r.IsExpanded));

        var open = RepoFileTree.VisibleRows(tree, new HashSet<string> { "Sources", "Sources/Core", "Sources/App/Views" });
        // A folder inside a collapsed one stays hidden even when marked expanded.
        Assert.Equal(new[]
        {
            "docs", "Sources", "Sources/App", "Sources/Core", "Sources/Core/Model.cs", ".gitignore", "Package.cs", "README.md",
        }, open.Select(r => r.Node.Path));
        Assert.Equal(2, open.First(r => r.Node.Path == "Sources/Core/Model.cs").Depth);
        Assert.True(open.First(r => r.Node.Path == "Sources").IsExpanded);
    }

    [Fact]
    public void EmptyFilter_ReturnsTheTree()
    {
        var tree = RepoFileTree.Build(Paths);
        Assert.Same(tree, RepoFileTree.Filter(tree, "  "));
        Assert.Empty(RepoFileTree.Filter(tree, "absent"));
    }
}

public class RepoRecordGroupTests
{
    internal static RepoRecord Record(RepoProvider provider, string owner, string? project, string name, string? path = null)
    {
        var key = new RepoKey(provider, owner, project, name);
        return new RepoRecord(key, null, new RepoRegistryEntry { Key = key, Path = path ?? $@"C:\checkouts\{name}", Tracked = true });
    }

    [Fact]
    public void GroupsByProjectThenOwner_InProviderOrder()
    {
        var groups = RepoRecordGroup.Groups(new[]
        {
            Record(RepoProvider.GitHub, "zeta", null, "tool"),
            Record(RepoProvider.AzureDevOps, "org", "Beta", "service"),
            Record(RepoProvider.GitHub, "alpha", null, "b-app"),
            Record(RepoProvider.GitHub, "alpha", null, "a-app"),
            Record(RepoProvider.AzureDevOps, "org", "Alpha", "site"),
        });
        Assert.Equal(new[] { "Azure DevOps · Alpha", "Azure DevOps · Beta", "GitHub · alpha", "GitHub · zeta" }, groups.Select(g => g.Title));
        Assert.Equal(new[] { "a-app", "b-app" }, groups[2].Records.Select(r => r.Key.Name));
    }

    [Fact]
    public void Filter_MatchesNameAndLocalPath()
    {
        var records = new[]
        {
            Record(RepoProvider.GitHub, "owner", null, "widget"),
            Record(RepoProvider.GitHub, "owner", null, "gadget", @"C:\checkouts\special"),
        };
        Assert.Equal(new[] { "widget" }, RepoRecordGroup.Groups(records, "WIDG").SelectMany(g => g.Records).Select(r => r.Key.Name));
        Assert.Equal(new[] { "gadget" }, RepoRecordGroup.Groups(records, "special").SelectMany(g => g.Records).Select(r => r.Key.Name));
        Assert.Empty(RepoRecordGroup.Groups(records, "nothing"));
    }
}

public class RepoSidebarOrganizerTests
{
    private static RepoRecord R(RepoProvider provider, string owner, string? project, string name) =>
        RepoRecordGroupTests.Record(provider, owner, project, name);

    private static RepoStatus Status(RepoRecord record, int behind = 0, int changes = 0, long? last = null)
    {
        var snapshot = new GitStatusSnapshot
        {
            Branch = "main",
            Behind = behind,
            Changes = Enumerable.Range(0, changes)
                .Select(i => new RepoFileChange { Path = $"f{i}", Kind = RepoChangeKind.Untracked, IndexStatus = "?", WorktreeStatus = "?" })
                .ToList(),
        };
        return RepoStatus.From(record.Id, record.Key.DisplayName, @"C:\tmp", snapshot, null, null,
            last is { } s ? DateTimeOffset.FromUnixTimeSeconds(s) : null);
    }

    private readonly List<RepoRecord> _records = new()
    {
        R(RepoProvider.GitHub, "acme", null, "zeta"),
        R(RepoProvider.AzureDevOps, "org", "Devices", "beta"),
        R(RepoProvider.GitHub, "acme", null, "alpha"),
        R(RepoProvider.AzureDevOps, "org", "Apps", "gamma"),
        R(RepoProvider.AzureDevOps, "org", "Devices", "alpha"),
        R(RepoProvider.GitHub, "other", null, "tool"),
    };

    private static readonly Dictionary<string, RepoStatus> NoStatus = new();

    [Fact]
    public void Sections_NestHostThenScope()
    {
        var sections = RepoSidebarOrganizer.Sections(_records, NoStatus, RepoSidebarSort.Name);
        Assert.Equal(new[] { "Azure DevOps", "GitHub" }, sections.Select(s => s.Title));
        Assert.Equal(new[] { "Apps", "Devices" }, sections[0].Groups.Select(g => g.Scope));
        Assert.Equal(new[] { "alpha", "beta" }, sections[0].Groups[1].Records.Select(r => r.Key.Name));
        Assert.Equal(new[] { "acme", "other" }, sections[1].Groups.Select(g => g.Scope));
        Assert.Equal(3, sections[0].RepositoryCount);
    }

    [Fact]
    public void SortsWithinGroups_ByTheChosenKey()
    {
        var alpha = _records[4];
        var beta = _records[1];
        var statuses = new Dictionary<string, RepoStatus>
        {
            [alpha.Id] = Status(alpha, behind: 1, changes: 0, last: 100),
            [beta.Id] = Status(beta, behind: 5, changes: 3, last: 50),
        };
        IEnumerable<string> Devices(RepoSidebarSort sort) =>
            RepoSidebarOrganizer.Sections(_records, statuses, sort)[0].Groups[1].Records.Select(r => r.Key.Name);
        Assert.Equal(new[] { "alpha", "beta" }, Devices(RepoSidebarSort.Name));
        Assert.Equal(new[] { "alpha", "beta" }, Devices(RepoSidebarSort.RecentlyChanged));
        Assert.Equal(new[] { "beta", "alpha" }, Devices(RepoSidebarSort.MostChanges));
        Assert.Equal(new[] { "beta", "alpha" }, Devices(RepoSidebarSort.MostBehind));
    }

    [Fact]
    public void RepositoriesWithoutStatus_FallBackToName() =>
        Assert.Equal(new[] { "alpha", "alpha", "beta", "gamma", "tool", "zeta" },
            RepoSidebarOrganizer.Flat(_records, NoStatus, RepoSidebarSort.MostBehind).Select(r => r.Key.Name));

    [Fact]
    public void Filter_DropsEmptyGroupsAndSections()
    {
        var sections = RepoSidebarOrganizer.Sections(_records, NoStatus, RepoSidebarSort.Name, "TOOL");
        Assert.Equal(new[] { "GitHub" }, sections.Select(s => s.Title));
        Assert.Equal(new[] { "other" }, sections[0].Groups.Select(g => g.Scope));
    }
}

public class RepoTextAndHighlightTests
{
    [Fact]
    public void TextFilesDecode_AndBinaryFilesDoNot()
    {
        Assert.Equal("héllo\n", RepoTextFile.Decode(Encoding.UTF8.GetBytes("héllo\n")));
        Assert.Equal("", RepoTextFile.Decode(Array.Empty<byte>()));
        Assert.Null(RepoTextFile.Decode(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x01 }));
        Assert.Null(RepoTextFile.Decode(new byte[] { 0xFF, 0xFE, 0xFD }));
        Assert.Equal("bom", RepoTextFile.Decode(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'b', (byte)'o', (byte)'m' }));
    }

    private static List<(CodeTokenKind Kind, string Text)> Spans(string source, CodeLanguage language) =>
        CodeHighlighter.Tokens(source, language).Select(t => (t.Kind, source.Substring(t.Start, t.Length))).ToList();

    [Fact]
    public void DetectsByExtension_ThenShebang()
    {
        Assert.Equal(CodeLanguage.CSharp, CodeLanguages.Detect("Sources/App/main.cs", ""));
        Assert.Equal(CodeLanguage.Hcl, CodeLanguages.Detect("infra/main.tf", ""));
        Assert.Equal(CodeLanguage.PowerShell, CodeLanguages.Detect(@"scripts\build.ps1", ""));
        Assert.Equal(CodeLanguage.Xml, CodeLanguages.Detect("FleetMate.GUI/App.xaml", ""));
        Assert.Equal(CodeLanguage.Python, CodeLanguages.Detect("scripts/run", "#!/usr/bin/env python3\nprint(1)"));
        Assert.Equal(CodeLanguage.Shell, CodeLanguages.Detect("hooks/post-merge", "#!/bin/bash\n"));
        Assert.Equal(CodeLanguage.PlainText, CodeLanguages.Detect("README.md", "# Title"));
        Assert.Equal(CodeLanguage.Xml, CodeLanguages.Detect("data", "<?xml version=\"1.0\"?>"));
    }

    [Fact]
    public void CommentsStringsKeywordsAndNumbers()
    {
        var found = Spans("var x = \"a // not a comment\" // note \"quoted\"\nreturn 42", CodeLanguage.CSharp);
        Assert.Equal(new[] { CodeTokenKind.Keyword, CodeTokenKind.String, CodeTokenKind.Comment, CodeTokenKind.Keyword, CodeTokenKind.Number },
            found.Select(f => f.Kind));
        Assert.Equal("\"a // not a comment\"", found[1].Text);
        Assert.Equal("// note \"quoted\"", found[2].Text);
    }

    [Fact]
    public void KeywordsInsideStringsAndComments_AreNotColoured()
    {
        var found = Spans("echo \"if then\" # for done\nfi", CodeLanguage.Shell);
        Assert.Equal(new[] { CodeTokenKind.String, CodeTokenKind.Comment, CodeTokenKind.Keyword }, found.Select(f => f.Kind));
        Assert.Equal("fi", found[^1].Text);
    }

    [Fact]
    public void PlainTextAndOversizedSources_AreNotColoured()
    {
        Assert.Empty(CodeHighlighter.Tokens("if 1", CodeLanguage.PlainText));
        Assert.Empty(CodeHighlighter.Tokens(new string('x', CodeHighlighter.SizeLimit + 1), CodeLanguage.CSharp));
    }

    [Fact]
    public void BlockComments_SpanLines() =>
        Assert.Equal(new[] { CodeTokenKind.Comment, CodeTokenKind.Keyword },
            Spans("/* one\ntwo */ var", CodeLanguage.CSharp).Select(f => f.Kind));
}

public sealed class GitIndexLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fleetmate-lock-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void RecognisesLockErrors()
    {
        Assert.True(GitIndexLock.Matches(@"fatal: Unable to create 'C:/x/.git/index.lock': File exists."));
        Assert.True(GitIndexLock.Matches("Another git process seems to be running in this repository"));
        Assert.False(GitIndexLock.Matches("nothing to commit"));
    }

    [Fact]
    public void LockPath_ForACheckoutAndALinkedWorktree()
    {
        var main = Path.Combine(_root, "main");
        Directory.CreateDirectory(Path.Combine(main, ".git"));
        Assert.Equal(Path.Combine(main, ".git", "index.lock"), GitIndexLock.LockPath(main));

        var linked = Path.Combine(_root, "linked");
        Directory.CreateDirectory(linked);
        File.WriteAllText(Path.Combine(linked, ".git"), $"gitdir: {main.Replace('\\', '/')}/.git/worktrees/linked\n");
        Assert.Equal(Path.Combine(main, ".git", "worktrees", "linked", "index.lock"), GitIndexLock.LockPath(linked));
    }

    [Fact]
    public void RemovesAStaleLock_ButNotAHeldOne()
    {
        Directory.CreateDirectory(_root);
        var lockFile = Path.Combine(_root, "index.lock");
        File.WriteAllBytes(lockFile, Array.Empty<byte>());
        using (new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.NotEqual("", GitIndexLock.Holders(lockFile));
            Assert.Throws<RepoException>(() => GitIndexLock.RemoveStale(lockFile));
        }
        Assert.Equal("", GitIndexLock.Holders(lockFile));
        GitIndexLock.RemoveStale(lockFile);
        Assert.False(File.Exists(lockFile));
        GitIndexLock.RemoveStale(lockFile);
    }
}

public class GitPaneSupportTests
{
    [Fact]
    public void StatusEntries_SplitStagedAndUnstagedSides()
    {
        var snapshot = new GitStatusSnapshot
        {
            Changes = new()
            {
                new RepoFileChange { Path = "both.txt", Kind = RepoChangeKind.Changed, IndexStatus = "M", WorktreeStatus = "M" },
                new RepoFileChange { Path = "new.txt", Kind = RepoChangeKind.Untracked, IndexStatus = "?", WorktreeStatus = "?" },
                new RepoFileChange { Path = "moved.txt", OriginalPath = "old.txt", Kind = RepoChangeKind.Renamed, IndexStatus = "R", WorktreeStatus = "." },
                new RepoFileChange { Path = "debug.log", Kind = RepoChangeKind.Ignored, IndexStatus = "!", WorktreeStatus = "!" },
            },
        };
        var entries = GitStatusEntry.Entries(snapshot);
        Assert.Equal(new[] { "staged:both.txt", "work:both.txt", "work:new.txt", "staged:moved.txt" }, entries.Select(e => e.Id));
        Assert.Equal(GitEntryKind.Renamed, entries[3].Kind);
        Assert.Equal("old.txt", entries[3].OriginalPath);
        Assert.Equal(GitEntryKind.Untracked, entries[2].Kind);
    }

    [Fact]
    public void RefsLogAndBranches_Parse()
    {
        Assert.Equal(new[]
        {
            new RepoGitRef("main", GitRefKind.LocalBranch, true),
            new RepoGitRef("origin/main", GitRefKind.RemoteBranch),
            new RepoGitRef("v1.0", GitRefKind.Tag),
        }, RepoGitRef.Parse("HEAD -> refs/heads/main, refs/remotes/origin/main, tag: refs/tags/v1.0"));

        var log = "aaa\u001fDev\u001f2026-01-02T03:04:05Z\u001fbbb ccc\u001f\u001fMerge\u001e\nbbb\u001fDev\u001f2026-01-01T03:04:05Z\u001f\u001f\u001fRoot\u001e";
        var commits = GitCommit.ParseLog(log);
        Assert.Equal(new[] { "Merge", "Root" }, commits.Select(c => c.Subject));
        Assert.Equal(new[] { "bbb", "ccc" }, commits[0].Parents);
        Assert.Empty(commits[1].Parents);

        Assert.Equal(new[] { new GitBranch("main", true, "origin/main"), new GitBranch("feature/x", false) },
            GitBranch.Parse("main|origin/main|*\nfeature/x||\n"));
    }

    [Fact]
    public void Graph_PutsABranchOnItsOwnLane()
    {
        var now = DateTimeOffset.Now;
        var graph = CommitGraphBuilder.Build(new[]
        {
            new GitCommit("m", "Merge", "", now, new[] { "a", "b" }, Array.Empty<RepoGitRef>()),
            new GitCommit("b", "Side", "", now, new[] { "a" }, Array.Empty<RepoGitRef>()),
            new GitCommit("a", "Root", "", now, Array.Empty<string>(), Array.Empty<RepoGitRef>()),
        });
        Assert.Equal(2, graph.LaneCount);
        Assert.Equal(new[] { 0, 1, 0 }, graph.Rows.Select(r => r.DotColumn));
    }

    [Fact]
    public void Revisions_MustBeHashes()
    {
        Assert.Throws<RepoException>(() => GitPaneOperations.ValidateRevision("--orphan"));
        Assert.Throws<RepoException>(() => GitPaneOperations.ValidateRevision("main"));
        GitPaneOperations.ValidateRevision("0123abcdef");
    }
}

public sealed class GitPaneOperationsTests : TempGitRepository
{
    private static readonly IReadOnlySet<string> Main = new HashSet<string> { "main" };

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Write("file.txt", string.Join("\n", Enumerable.Range(1, 30).Select(i => $"line {i}")) + "\n");
        Write("b.txt", "b\n");
        await Copy.CommitAsync("Initial", null, new HashSet<string>(), allowProtected: true);
        await Copy.SwitchBranchAsync("feature/pane");
    }

    [Fact]
    public async Task StagesOneHunkOfTwo()
    {
        var lines = Enumerable.Range(1, 30).Select(i => $"line {i}").ToArray();
        lines[1] = "changed near the top";
        lines[27] = "changed near the bottom";
        Write("file.txt", string.Join("\n", lines) + "\n");

        var file = DiffParser.Parse(await Copy.CombinedDiffAsync("file.txt")).Files.Single();
        Assert.Equal(2, file.Hunks.Count);
        await Copy.ApplyPatchAsync(file.PatchForHunk(file.Hunks[0]), cached: true, reverse: false);

        var staged = await Copy.DiffAsync(staged: true);
        Assert.Contains("+changed near the top", staged);
        Assert.DoesNotContain("+changed near the bottom", staged);
        Assert.Equal(new[] { "staged:file.txt", "work:file.txt" }, (await Copy.StatusEntriesAsync()).Select(e => e.Id));

        // Discarding the other chunk from the working tree leaves the staged one.
        var rest = DiffParser.Parse(await Copy.DiffAsync(paths: new[] { "file.txt" })).Files.Single();
        await Copy.ApplyPatchAsync(rest.PatchForHunk(rest.Hunks[0]), cached: false, reverse: true);
        Assert.DoesNotContain("changed near the bottom", Read("file.txt"));
        Assert.Contains("changed near the top", Read("file.txt"));
    }

    [Fact]
    public async Task CommitWithBody_HistoryShow_AndProtection()
    {
        Write("file.txt", "new\n");
        await Copy.StageAsync(new[] { "file.txt" });
        var commit = await Copy.CommitAsync("Replace the file", "Why it changed.", amend: false, runHooks: true, Main);
        Assert.Equal("Replace the file", commit.Subject);
        Assert.Contains("Why it changed.", (await Copy.GitAsync(new[] { "log", "-1", "--format=%B" })).StandardOutput);

        var history = await Copy.HistoryAsync();
        Assert.Equal(new[] { "Replace the file", "Initial" }, history.Select(c => c.Subject));
        Assert.Contains(new RepoGitRef("feature/pane", GitRefKind.LocalBranch, true), history[0].Refs);
        Assert.Contains("+new", await Copy.ShowAsync(history[0].Sha));
        Assert.Contains("Subject: [PATCH] Replace the file", await Copy.FormatPatchAsync(history[0].Sha));

        await Copy.CreateBranchAsync("feature/other", history[1].Sha);
        await Copy.TagAsync("v0.1", history[1].Sha, "First tag");
        Assert.Equal(new HashSet<string> { "main", "feature/pane", "feature/other" }, (await Copy.BranchListAsync()).Select(b => b.Name).ToHashSet());

        await Copy.SwitchBranchAsync("main");
        var refused = await Assert.ThrowsAsync<RepoException>(() =>
            Copy.CommitAsync("Amend main", null, amend: true, runHooks: true, Main));
        Assert.Equal(RepoErrorKind.ProtectedBranch, refused.Kind);
        await Assert.ThrowsAsync<RepoException>(() => Copy.CheckoutCommitAsync("--orphan"));
    }

    [Fact]
    public async Task CommitStaged_CommitsOnlyTheIndex_IncludingARename()
    {
        Assert.True((await Copy.GitAsync(new[] { "mv", "b.txt", "renamed.txt" })).Succeeded);
        Write("file.txt", "changed\n");
        var commit = await Copy.CommitStagedAsync("Rename b", Main);
        Assert.Equal("Rename b", commit.Subject);
        var status = await Copy.StatusAsync();
        Assert.Equal(0, status.StagedCount);
        Assert.Equal(new[] { "file.txt" }, status.Changes.Select(c => c.Path));
        Assert.Equal(new HashSet<string> { "renamed.txt", "file.txt" }, (await Copy.ListFilesAsync()).ToHashSet());
    }

    [Fact]
    public async Task CommitStaged_RefusesAnEmptyIndexAndAProtectedBranch()
    {
        Assert.Equal(RepoErrorKind.NothingToCommit,
            (await Assert.ThrowsAsync<RepoException>(() => Copy.CommitStagedAsync("Nothing", Main))).Kind);
        await Copy.SwitchBranchAsync("main");
        Write("b.txt", "edit\n");
        await Copy.StageAsync(new[] { "b.txt" });
        Assert.Equal(RepoErrorKind.ProtectedBranch,
            (await Assert.ThrowsAsync<RepoException>(() => Copy.CommitStagedAsync("On main", Main))).Kind);
    }

    [Fact]
    public async Task RevertAndCherryPick_AddCommits()
    {
        Write("b.txt", "two\n");
        var second = await Copy.CommitAsync("Second", null, Main);
        await Copy.RevertAsync(second.Sha);
        Assert.Equal("b\n", Read("b.txt"));
        await Copy.CherryPickAsync(second.Sha);
        Assert.Equal("two\n", Read("b.txt"));
        Assert.Equal(4, (await Copy.LogAsync(10)).Count);
    }
}
