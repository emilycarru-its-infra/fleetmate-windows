using FleetMate.Core.Services.Repos;
using FleetMate.Core.Services.Terminal;
using Xunit;

namespace FleetMate.Tests;

public class TerminalWorkspaceSyncTests
{
    private static readonly DateTime Now = new(2033, 5, 18, 0, 0, 0, DateTimeKind.Utc);

    private static TerminalWorkspaceBaseline.Checkout Tracked(RepoProvider provider, string scope, string name) =>
        new(provider, scope, $"{scope}/{name}", $@"C:\src\{scope}\{name}", Now.AddDays(-300), Tracked: true);

    private static readonly string[] Defaults = { "codex", "shell" };

    private static string Folder(string scope, string name) => $@"c:\src\{scope}\{name}".ToLowerInvariant();

    private static IEnumerable<string?> Directories(TerminalWorkspace workspace) =>
        workspace.Sections.SelectMany(s => s.Sessions).Select(s => s.Directory is { } d ? TerminalWorkspaceBaseline.Normalized(d) : null);

    [Fact]
    public void EveryTrackedCheckout_IsIncludedHoweverIdle()
    {
        var all = Enumerable.Range(0, 20).Select(i => Tracked(RepoProvider.GitHub, "o", $"r{i}")).ToList();
        Assert.Equal(20, TerminalWorkspaceBaseline.ActiveCheckouts(all, Now).Count);
    }

    [Fact]
    public void AnEmptyFile_GetsTheReposWorkspace_AtTheTop()
    {
        // The case that left the menu reading "No saved workspaces": the file
        // existed, empty, before the generated workspace was first written.
        var file = new TerminalWorkspaceFile();
        file.Workspaces.Add(new TerminalWorkspace { Name = "Mine" });
        var checkouts = new[] { Tracked(RepoProvider.AzureDevOps, "Cloud", "Vantage"), Tracked(RepoProvider.GitHub, "o", "a"), Tracked(RepoProvider.Other, "host", "z") };
        Assert.True(TerminalWorkspaceBaseline.SyncBaseline(file, checkouts, Defaults, Now));
        Assert.Equal(0, file.BaselineIndex);
        Assert.Equal(TerminalWorkspaceBaseline.Source, file.Workspaces[0].Source);
        Assert.Equal(new[] { "Azure DevOps", "GitHub", "Other" }, file.Baseline!.Sections.Select(s => s.Name));
        var first = file.Baseline.Sections[0].Sessions[0];
        Assert.Equal("Cloud/Vantage", first.Label);
        Assert.Null(first.Name);
        Assert.False(TerminalWorkspaceBaseline.SyncBaseline(file, checkouts, Defaults, Now));
    }

    [Fact]
    public void ADeletedReposWorkspace_IsNotWrittenAgain()
    {
        var file = new TerminalWorkspaceFile();
        TerminalWorkspaceBaseline.SyncBaseline(file, new[] { Tracked(RepoProvider.GitHub, "o", "a") }, Defaults, Now);
        file.Remove(TerminalWorkspaceBaseline.Name);
        Assert.False(file.GenerateFromRepos);
        Assert.False(TerminalWorkspaceBaseline.SyncBaseline(file, new[] { Tracked(RepoProvider.GitHub, "o", "a") }, Defaults, Now));
        Assert.Null(file.Baseline);
    }

    [Fact]
    public void Sync_AddsNew_RemovesUntracked_AndKeepsEdits()
    {
        var workspace = TerminalWorkspaceBaseline.Make(new[]
        {
            Tracked(RepoProvider.GitHub, "o", "a"), Tracked(RepoProvider.GitHub, "o", "b"), Tracked(RepoProvider.AzureDevOps, "P", "c"),
        }, Now)!;
        // The person renames a, moves it to their own section, and removes b.
        var github = workspace.Sections[1];
        var a = github.Sessions[0];
        github.Sessions.Remove(a);
        a.Name = "Mine";
        github.Sessions.RemoveAll(s => TerminalWorkspaceBaseline.Normalized(s.Directory!) == Folder("o", "b"));
        workspace.Sections.Add(new TerminalWorkspaceSection { Name = "Focus", Sessions = { a } });

        // c stops being tracked (or is hidden); d is newly tracked; b still is.
        var change = TerminalWorkspaceBaseline.Sync(workspace, new[]
        {
            Tracked(RepoProvider.GitHub, "o", "a"), Tracked(RepoProvider.GitHub, "o", "b"), Tracked(RepoProvider.GitHub, "o", "d"),
        }, Defaults, Now);
        Assert.Equal(new[] { Folder("o", "d") }, change.Added);
        Assert.Equal(new[] { Folder("p", "c") }, change.Removed);
        // The emptied Azure DevOps section goes; GitHub, which the person left empty, stays and gets d.
        Assert.Equal(new[] { "GitHub", "Focus" }, workspace.Sections.Select(s => s.Name));
        Assert.Equal(new[] { Folder("o", "d") }, workspace.Sections[0].Sessions.Select(s => TerminalWorkspaceBaseline.Normalized(s.Directory!)));
        Assert.Equal("Mine", workspace.Sections[1].Sessions[0].Name);
        // Removed by the person: not added back while it stays tracked.
        Assert.DoesNotContain(Folder("o", "b"), Directories(workspace));

        // Untracked, then tracked again, it comes back.
        TerminalWorkspaceBaseline.Sync(workspace, new[] { Tracked(RepoProvider.GitHub, "o", "a"), Tracked(RepoProvider.GitHub, "o", "d") }, Defaults, Now);
        TerminalWorkspaceBaseline.Sync(workspace, new[]
        {
            Tracked(RepoProvider.GitHub, "o", "a"), Tracked(RepoProvider.GitHub, "o", "b"), Tracked(RepoProvider.GitHub, "o", "d"),
        }, Defaults, Now);
        Assert.Contains(Folder("o", "b"), Directories(workspace));
    }

    [Fact]
    public void Sync_LeavesASectionThePersonEmptiedAlone()
    {
        var workspace = TerminalWorkspaceBaseline.Make(new[] { Tracked(RepoProvider.GitHub, "o", "a") }, Now)!;
        workspace.Sections.Add(new TerminalWorkspaceSection { Name = "Later" });
        TerminalWorkspaceBaseline.Sync(workspace, Array.Empty<TerminalWorkspaceBaseline.Checkout>(), Defaults, Now);
        Assert.Equal(new[] { "Later" }, workspace.Sections.Select(s => s.Name));
    }

    [Fact]
    public void ANameStillTheGeneratedOne_BecomesTheLabel()
    {
        var workspace = new TerminalWorkspace
        {
            Name = "Repos", Source = TerminalWorkspaceBaseline.Source,
            Sections =
            {
                new TerminalWorkspaceSection
                {
                    Name = "GitHub",
                    Sessions =
                    {
                        new TerminalWorkspaceSession { Name = "o/a", Command = "codex", Directory = @"C:\src\o\a" },
                        new TerminalWorkspaceSession { Name = "Release prep", Command = "codex", Directory = @"C:\src\o\b" },
                    },
                },
            },
        };
        TerminalWorkspaceBaseline.Sync(workspace, new[] { Tracked(RepoProvider.GitHub, "o", "a"), Tracked(RepoProvider.GitHub, "o", "b") }, Defaults, Now);
        Assert.Equal(new[] { null, "Release prep" }, workspace.Sections[0].Sessions.Select(s => s.Name));
        Assert.Equal("o/a", workspace.Sections[0].Sessions[0].Label);
    }

    [Fact]
    public void GeneratedSessions_LoseTheDefaultCommandTheyWereWrittenWith_Once()
    {
        var workspace = new TerminalWorkspace
        {
            Name = "Repos", Source = TerminalWorkspaceBaseline.Source,
            Generated = new() { Folder("o", "a"), Folder("o", "b"), Folder("o", "c") },
            Sections =
            {
                new TerminalWorkspaceSection
                {
                    Name = "GitHub",
                    Sessions =
                    {
                        new TerminalWorkspaceSession { Label = "o/a", Command = "codex", Directory = @"C:\src\o\a" },
                        new TerminalWorkspaceSession { Label = "o/b", Command = "claude --model opus", Directory = @"C:\src\o\b" },
                        new TerminalWorkspaceSession { Label = "o/c", Command = "Shell", Directory = @"C:\src\o\c" },
                        // Added by the person, not placed by FleetMate: kept as written.
                        new TerminalWorkspaceSession { Label = "mine", Command = "codex", Directory = @"C:\work\mine" },
                    },
                },
            },
        };
        var checkouts = new[] { Tracked(RepoProvider.GitHub, "o", "a"), Tracked(RepoProvider.GitHub, "o", "b"), Tracked(RepoProvider.GitHub, "o", "c") };
        TerminalWorkspaceBaseline.Sync(workspace, checkouts, Defaults, Now);
        Assert.Equal(new string?[] { null, "claude --model opus", null, "codex" }, workspace.Sections[0].Sessions.Select(s => s.Command));
        Assert.True(workspace.CommandsFollowDefault);

        // Once migrated, a command written in later is the person's choice and stays.
        workspace.Sections[0].Sessions[0].Command = "codex";
        TerminalWorkspaceBaseline.Sync(workspace, checkouts, Defaults, Now);
        Assert.Equal("codex", workspace.Sections[0].Sessions[0].Command);
    }

    [Fact]
    public void NewlyPlacedSessions_CarryNoCommand()
    {
        var workspace = TerminalWorkspaceBaseline.Make(new[] { Tracked(RepoProvider.GitHub, "o", "a") }, Now)!;
        TerminalWorkspaceBaseline.Sync(workspace, new[] { Tracked(RepoProvider.GitHub, "o", "a"), Tracked(RepoProvider.GitHub, "o", "b") }, Defaults, Now);
        TerminalWorkspaceBaseline.AddMissing(new[] { Tracked(RepoProvider.AzureDevOps, "P", "c") }, workspace, Now);
        Assert.Equal(3, workspace.SessionCount);
        Assert.All(workspace.Sections.SelectMany(s => s.Sessions), s => Assert.Null(s.Command));
    }

    [Fact]
    public void RestoreAndNewFields_RoundTripThroughTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "fm-restore-" + Guid.NewGuid().ToString("N"), "terminal-workspaces.json");
        try
        {
            var file = new TerminalWorkspaceFile
            {
                GenerateFromRepos = false,
                Restore = new TerminalWorkspace
                {
                    Name = "Restore",
                    Sections = { new TerminalWorkspaceSection { Name = "Sessions", Sessions =
                    {
                        new TerminalWorkspaceSession { Label = "o/a", Command = "claude", Directory = "~/src/o/a", Pinned = true, Resume = true },
                    } } },
                },
            };
            file.Workspaces.Add(new TerminalWorkspace { Name = "Repos", Source = "repos", Generated = new() { @"c:\src\o\a" }, CommandsFollowDefault = true });
            TerminalWorkspaceStore.Save(file, path);
            var loaded = TerminalWorkspaceStore.Load(path);
            Assert.False(loaded.GenerateFromRepos);
            var session = loaded.Restore!.Sections[0].Sessions[0];
            Assert.Equal(("o/a", "claude", true, true), (session.Label, session.Command, session.Pinned, session.Resume));
            Assert.Equal(new[] { @"c:\src\o\a" }, loaded.Workspaces[0].Generated);
            Assert.True(loaded.Workspaces[0].CommandsFollowDefault);
            Assert.DoesNotContain("\"name\": null", File.ReadAllText(path));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch (IOException) { } }
    }
}

public class TerminalSessionNamingTests
{
    [Fact]
    public void AnAgentTitle_LosesItsGlyph_AndReportsWorking()
    {
        Assert.Equal(new TerminalTitleTask("Fix login redirect", true), TerminalTitleTask.Parse("⠐ Fix login redirect"));
        Assert.Equal(new TerminalTitleTask("Fix login redirect", false), TerminalTitleTask.Parse("✳ Fix login redirect"));
        Assert.Equal(new TerminalTitleTask("Release notes", null), TerminalTitleTask.Parse("Release notes"));
    }

    [Theory]
    [InlineData("✳ Claude Code")]
    [InlineData("codex")]
    [InlineData("person@workstation: ~/src/app")]
    [InlineData("person@workstation ~/src")]
    [InlineData("~/src/app")]
    [InlineData(@"C:\WINDOWS\system32\cmd.exe")]
    [InlineData("Windows PowerShell")]
    [InlineData("pwsh")]
    [InlineData("  ")]
    public void ShellAndProgramTitles_NameNoTask(string title) => Assert.Null(TerminalTitleTask.Parse(title).Task);

    [Fact]
    public void ControlCharacters_AreStripped_AndLengthCapped()
    {
        Assert.Equal("Deploy fix", TerminalTitleTask.Parse("Deploy\a fix").Task);
        Assert.Equal(TerminalTitleTask.MaxLength, TerminalTitleTask.Parse(new string('x', 300)).Task!.Length);
    }

    [Fact]
    public void AManualName_WinsOverEveryAutomaticOne()
    {
        Assert.Equal("Mine", TerminalSessionName.Resolve("Mine", "Agent", "Task", "o/a", "Codex"));
        Assert.Equal("Task", TerminalSessionName.Resolve("  ", null, "Task", "o/a", "Codex"));
        Assert.Equal("Agent", TerminalSessionName.Resolve("", "Agent", "Task", null, "Codex"));
        Assert.Equal("o/a", TerminalSessionName.Resolve("", null, null, "o/a", "Codex"));
        Assert.Equal("Codex", TerminalSessionName.Resolve(null, null, null, null, "Codex"));
    }

    [Fact]
    public void Breadcrumb_StartsBelowTheNearestRoot()
    {
        const string home = @"C:\Users\person";
        Assert.Equal("Cloud · Vantage", TerminalBreadcrumb.Make(@"~\src\Cloud\Vantage", new[] { @"~\src" }, home));
        Assert.Equal("o · app · fix", TerminalBreadcrumb.Make(@"C:\Users\person\src\GitHub\o\app\.worktrees\fix", new[] { @"~\src", @"~\src\GitHub" }, home));
        Assert.Equal("src", TerminalBreadcrumb.Make(@"C:\Users\person\src", new[] { @"~\src\" }, home));
        Assert.Equal("~ · Documents · x", TerminalBreadcrumb.Make(@"C:\Users\person\Documents\x", new[] { @"~\src" }, home));
        Assert.Equal("~", TerminalBreadcrumb.Make(@"C:\Users\person", Array.Empty<string>(), home));
        Assert.Equal(@"D:\tools", TerminalBreadcrumb.Make(@"D:\tools", Array.Empty<string>(), home));
        Assert.Equal("Cloud · Vantage", TerminalBreadcrumb.Make("C:/Users/person/src/Cloud/Vantage", new[] { "~/src" }, home));
    }

    [Fact]
    public void ResumeCommands()
    {
        Assert.Equal("claude --continue", AgentResume.Command("claude"));
        Assert.Equal("claude --model opus --continue", AgentResume.Command("claude --model opus"));
        Assert.Null(AgentResume.Command("claude --continue"));
        Assert.Equal("codex resume --last", AgentResume.Command("codex"));
        Assert.Null(AgentResume.Command("codex exec hi"));
        Assert.Null(AgentResume.Command("claude-remote"));
        Assert.Null(AgentResume.Command(""));
    }
}
