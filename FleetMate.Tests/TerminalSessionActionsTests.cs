using FleetMate.Core.Services.Repos;
using FleetMate.Core.Services.Terminal;
using Xunit;

namespace FleetMate.Tests;

public class TerminalSessionActionsTests
{
    private static readonly DateTime Now = new(2033, 5, 18, 0, 0, 0, DateTimeKind.Utc);

    private static TerminalWorkspaceBaseline.Checkout Tracked(string name) =>
        new(RepoProvider.GitHub, "o", $"o/{name}", $@"C:\src\o\{name}", Now.AddDays(-300), Tracked: true);

    private static string Key(string name, string? id = null) => TerminalWorkspaceBaseline.RowKey($@"C:\src\o\{name}", id);

    private static TerminalSessionActions.RepoRow Row(string name, string? command = null, string? customName = null, string? id = null) =>
        new(Key(name, id), "GitHub", $"o/{name}", command, $@"C:\src\o\{name}", customName, false, id);

    [Fact]
    public void Presets_AreRemoteThenDirectThenShell()
    {
        Assert.Equal(new[] { "claude-remote", "codex-remote", "claude", "codex", "shell" },
            TerminalSessionActions.Presets.Select(p => p.Command));
        Assert.Equal("Claude (remote)", TerminalSessionActions.Presets[0].Label);
    }

    [Fact]
    public void TheCheckedPreset_IsWhatTheSessionRuns()
    {
        Assert.True(TerminalSessionActions.IsCurrent("shell", ""));
        Assert.True(TerminalSessionActions.IsCurrent("shell", null));
        Assert.True(TerminalSessionActions.IsCurrent("Codex", "codex"));
        Assert.False(TerminalSessionActions.IsCurrent("claude", "claude-remote"));
        Assert.False(TerminalSessionActions.IsCurrent("codex", "codex --model o4"));
    }

    [Fact]
    public void ADuplicate_IsNamedTwo_OrTheNextFreeNumber()
    {
        Assert.Equal("o/a 2", TerminalSessionActions.DuplicateName("o/a", new[] { "o/a" }));
        Assert.Equal("o/a 4", TerminalSessionActions.DuplicateName("o/a", new[] { "o/a", "o/a 2", "O/A 3" }));
    }

    [Fact]
    public void SwitchingAgent_WritesAnExplicitCommand_AndFollowingDefaultWritesNone()
    {
        var workspace = TerminalWorkspaceBaseline.Make(new[] { Tracked("a"), Tracked("b") }, Now)!;
        var changed = TerminalSessionActions.WriteBack(workspace, new[] { "GitHub" }, Array.Empty<string>(),
            new[] { Row("a", command: "claude-remote"), Row("b") });
        Assert.True(changed);
        Assert.Equal(new string?[] { "claude-remote", null }, workspace.Sections[0].Sessions.Select(s => s.Command));

        // Sync keeps the person's choice; the migration never touches it.
        TerminalWorkspaceBaseline.Sync(workspace, new[] { Tracked("a"), Tracked("b") }, new[] { "claude-remote", "codex" }, Now);
        Assert.Equal("claude-remote", workspace.Sections[0].Sessions[0].Command);

        Assert.False(TerminalSessionActions.WriteBack(workspace, new[] { "GitHub" }, Array.Empty<string>(),
            new[] { Row("a", command: "claude-remote"), Row("b") }));
    }

    [Fact]
    public void ADuplicate_IsWrittenDirectlyBelow_AsOneThePersonAdded()
    {
        var workspace = TerminalWorkspaceBaseline.Make(new[] { Tracked("a"), Tracked("b") }, Now)!;
        TerminalSessionActions.WriteBack(workspace, new[] { "GitHub" }, Array.Empty<string>(),
            new[] { Row("a"), Row("a", command: "claude", customName: "o/a 2", id: "d1"), Row("b") });

        var sessions = workspace.Sections[0].Sessions;
        Assert.Equal(new[] { "o/a", "o/a", "o/b" }, sessions.Select(s => s.Label));
        var copy = sessions[1];
        Assert.Equal(("o/a 2", "claude", true, "d1"), (copy.Name, copy.Command, copy.Added, copy.Id));
        Assert.Equal(TerminalWorkspaceStore.Abbreviate(@"C:\src\o\a"), copy.Directory);
        Assert.NotEqual(TerminalWorkspaceBaseline.RowKey(sessions[0]), TerminalWorkspaceBaseline.RowKey(copy));
        // The folder FleetMate placed is the repository's row's; the duplicate is not counted.
        Assert.Equal(2, workspace.Generated!.Count);
    }

    [Fact]
    public void Sync_NeverTakesOutADuplicate_EvenWhenItsRepositoryGoes()
    {
        var workspace = TerminalWorkspaceBaseline.Make(new[] { Tracked("a"), Tracked("b") }, Now)!;
        TerminalSessionActions.WriteBack(workspace, new[] { "GitHub" }, Array.Empty<string>(),
            new[] { Row("a"), Row("a", customName: "o/a 2", id: "d1"), Row("b") });

        var change = TerminalWorkspaceBaseline.Sync(workspace, new[] { Tracked("b") }, Array.Empty<string>(), Now);
        Assert.Single(change.Removed);
        Assert.Equal(new[] { "o/a 2", null }, workspace.Sections[0].Sessions.Select(s => s.Name));
        Assert.True(workspace.Sections[0].Sessions[0].Added);

        // Tracked again, the repository gets its own row back beside the duplicate.
        TerminalWorkspaceBaseline.Sync(workspace, new[] { Tracked("a"), Tracked("b") }, Array.Empty<string>(), Now);
        Assert.Equal(2, workspace.Sections[0].Sessions.Count(s => s.Label == "o/a"));
    }

    [Fact]
    public void WriteBack_KeepsUnkeyedSessions_AndSectionState()
    {
        var workspace = TerminalWorkspaceBaseline.Make(new[] { Tracked("a") }, Now)!;
        workspace.Sections[0].Sessions.Add(new TerminalWorkspaceSession { Name = "notes", Command = "codex" });
        TerminalSessionActions.WriteBack(workspace, new[] { "GitHub", "Empty" }, new[] { "GitHub" }, new[] { Row("a", customName: "Mine") });
        Assert.Equal(new[] { "GitHub" }, workspace.Sections.Select(s => s.Name));
        Assert.True(workspace.Sections[0].Collapsed);
        Assert.Equal(new[] { "Mine", "notes" }, workspace.Sections[0].Sessions.Select(s => s.Name));
    }

    [Fact]
    public void AnAddedSession_RoundTripsThroughTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "fm-added-" + Guid.NewGuid().ToString("N"), "terminal-workspaces.json");
        try
        {
            var file = new TerminalWorkspaceFile();
            file.Workspaces.Add(TerminalWorkspaceBaseline.Make(new[] { Tracked("a") }, Now)!);
            file.Workspaces[0].Sections[0].Sessions.Add(new TerminalWorkspaceSession { Name = "o/a 2", Directory = @"C:\src\o\a", Added = true, Id = "d1" });
            TerminalWorkspaceStore.Save(file, path);
            var copy = TerminalWorkspaceStore.Load(path).Workspaces[0].Sections[0].Sessions[1];
            Assert.Equal(Key("a", "d1"), TerminalWorkspaceBaseline.RowKey(copy));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch (IOException) { } }
    }
}
