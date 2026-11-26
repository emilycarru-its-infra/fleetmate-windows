using FleetMate.Core.Config;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Terminal;
using FleetMate.Core.Shared;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The Agent Terminal's session list and pane, as both desktop apps share
/// them from FleetMate.Core. Every list here is a demo list (no
/// <see cref="AgentSessionHost"/>), so no shell, agent or file is touched.
/// </summary>
public class TerminalPanelModelTests
{
    private static TerminalSessionList DemoList() =>
        new(() => new TerminalSettings(), agent: null, post: a => a(), defer: a => a(), isDark: () => true);

    private static TerminalLaunch Launch(string title) =>
        new(title, new TerminalCommand("pwsh.exe", Array.Empty<string>()), null) { TakeFocus = false };

    // ── The page and its messages ────────────────────────────────────────

    [Theory]
    [InlineData("https://terminal.fleetmate.invalid/terminal.html?theme=dark", true)]
    [InlineData("https://TERMINAL.fleetmate.invalid/xterm.js", true)]
    [InlineData("http://terminal.fleetmate.invalid/terminal.html", false)]
    [InlineData("https://terminal.fleetmate.invalid:8443/terminal.html", false)]
    [InlineData("https://example.com/terminal.html", false)]
    [InlineData("https://terminal.fleetmate.invalid.example.com/", false)]
    [InlineData("file:///C:/terminal.html", false)]
    [InlineData(null, false)]
    public void OnlyTheTerminalPageIsOwn(string? uri, bool own) =>
        Assert.Equal(own, TerminalPaneController.IsOwnUri(uri));

    [Fact]
    public void ThePageMayOnlyReadTheClipboard()
    {
        Assert.True(TerminalPaneController.AllowsPermission(TerminalPaneController.PageUrl(true), "ClipboardRead"));
        Assert.False(TerminalPaneController.AllowsPermission(TerminalPaneController.PageUrl(true), "Camera"));
        Assert.False(TerminalPaneController.AllowsPermission("https://example.com/", "ClipboardRead"));
    }

    [Fact]
    public void PageUrlCarriesTheTheme()
    {
        Assert.Equal("https://terminal.fleetmate.invalid/terminal.html?theme=dark", TerminalPaneController.PageUrl(true));
        Assert.EndsWith("?theme=light", TerminalPaneController.PageUrl(false));
    }

    [Fact]
    public void ADemoPaneShowsItsTranscriptAndNeverStartsAProcess()
    {
        var sent = new List<string>();
        using var pane = new TerminalPaneController(Launch("codex") with { DemoTranscript = "fictional output\n" }, null, a => a(), () => true);
        pane.AttachPage(sent.Add);
        pane.OnPageMessage("""{"type":"ready"}""");
        pane.OnPageMessage("""{"type":"resize","cols":100,"rows":30}""");
        Assert.True(pane.IsReady);
        Assert.Equal(0, pane.ProcessId);
        Assert.Contains(sent, m => m.Contains("\"type\":\"config\""));
        Assert.Contains(sent, m => m.Contains("fictional output\\r\\n"));
        Assert.False(pane.PasteText("hello"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"type":7}""")]
    [InlineData("""{"type":"resize","cols":"wide","rows":30}""")]
    [InlineData("""{"type":"drop","text":5}""")]
    [InlineData("""{"type":"key"}""")]
    [InlineData("""{"type":"unknown"}""")]
    public void MalformedPageMessagesAreIgnored(string json)
    {
        var events = 0;
        using var pane = new TerminalPaneController(Launch("shell"), null, a => a(), () => true);
        pane.TextDropped += (_, _) => events++;
        pane.KeyAction += (_, _, _) => events++;
        pane.OnPageMessage(json);
        Assert.Equal(0, events);
        Assert.False(pane.IsReady);
    }

    [Fact]
    public void PageKeysAndDropsReachThePanel()
    {
        using var pane = new TerminalPaneController(Launch("shell"), null, a => a(), () => true);
        TerminalAction? action = null;
        string? dropped = null;
        pane.KeyAction += (_, a, _) => action = a;
        pane.TextDropped += (_, text) => dropped = text;
        pane.OnPageMessage("""{"type":"key","code":"KeyT","ctrl":true,"shift":true,"alt":false}""");
        pane.OnPageMessage("""{"type":"drop","text":"### Ticket: Printer"}""");
        Assert.Equal(TerminalAction.NewSession, action);
        Assert.Equal("### Ticket: Printer", dropped);
    }

    [Fact]
    public void AnOversizedDropIsIgnored()
    {
        using var pane = new TerminalPaneController(Launch("shell"), null, a => a(), () => true);
        var dropped = false;
        pane.TextDropped += (_, _) => dropped = true;
        var huge = new string('a', TerminalPaneController.MaxMessageText + 1);
        pane.OnPageMessage($$"""{"type":"drop","text":"{{huge}}"}""");
        Assert.False(dropped);
    }

    // ── The list ─────────────────────────────────────────────────────────

    [Fact]
    public void DemoLaunchSeedsFictionalSessionsWithoutProcesses()
    {
        var list = DemoList();
        list.StartAtLaunch();
        Assert.True(list.IsDemo);
        Assert.Equal(3, list.Sessions.Count);
        Assert.Contains("Sample Repos", list.Sections);
        Assert.Same(list.Sessions[0], list.Active);
        Assert.All(list.Sessions.SelectMany(s => s.Panes), p => Assert.Equal(0, p.ProcessId));
        Assert.Equal(TerminalDot.Waiting, list.RowState(list.Sessions[2]).Dot);
        Assert.Equal("Ctrl+1", list.RowState(list.Sessions[0]).Hint);
        Assert.Equal("sample-app", list.RowState(list.Sessions[0]).Breadcrumb);
        // Nothing is read from, or written to, the workspaces file.
        Assert.Empty(list.LoadWorkspaces(out _)!.Workspaces);
        Assert.Null(list.LaunchWorkspace());
    }

    [Fact]
    public void PinnedRowsLeadAndCollapsedSectionsHideTheirRows()
    {
        var list = DemoList();
        list.OpenSession(Launch("one"));
        list.OpenSession(Launch("two"));
        list.TogglePin(list.Sessions[1]);
        var entries = list.Entries();
        var pinned = Assert.IsType<TerminalSectionEntry>(entries[0]);
        Assert.True(pinned.IsPinned);
        Assert.Same(list.Sessions[1], Assert.IsType<TerminalSessionEntry>(entries[1]).Session);

        var sessions = entries.OfType<TerminalSectionEntry>().Single(e => !e.IsPinned);
        list.ToggleCollapsed(sessions);
        var collapsed = list.Entries();
        Assert.StartsWith("▸ Sessions", collapsed.OfType<TerminalSectionEntry>().Single(e => !e.IsPinned).Header);
        Assert.DoesNotContain(collapsed.OfType<TerminalSessionEntry>(), e => ReferenceEquals(e.Session, list.Sessions[0]));
    }

    [Fact]
    public void ASearchWithNoMatchShowsTheEmptyState()
    {
        var list = DemoList();
        list.OpenSession(Launch("alpha"));
        list.SetQuery("zzz-nothing");
        Assert.IsType<TerminalEmptyEntry>(Assert.Single(list.Entries()));
        list.ClearFilters();
        Assert.Contains(list.Entries(), e => e is TerminalSessionEntry);
    }

    [Fact]
    public void SectionsRenameMoveAndRemoveTakeTheirSessions()
    {
        var list = DemoList();
        list.AddSection("Work");
        list.OpenSession(Launch("one"));
        list.MoveSession(list.Sessions[0], before: null, section: "Work");
        Assert.Equal("Work", list.Sessions[0].Section);

        list.RenameSection("Work", "Projects");
        Assert.Equal("Projects", list.Sessions[0].Section);
        // The default section cannot be renamed, and a taken name is refused.
        list.RenameSection(TerminalSessionList.DefaultSection, "Other");
        list.RenameSection("Projects", TerminalSessionList.DefaultSection);
        Assert.Equal(new[] { TerminalSessionList.DefaultSection, "Projects" }, list.Sections);

        list.MoveSection("Projects", -1);
        Assert.Equal("Projects", list.Sections[0]);
        list.RemoveSection("Projects");
        Assert.Equal(TerminalSessionList.DefaultSection, list.Sessions[0].Section);
    }

    [Fact]
    public void DuplicateGoesBelowTheOriginalWithANumberedName()
    {
        var list = DemoList();
        list.OpenSession(Launch("one"));
        list.OpenSession(Launch("two"));
        list.Rename(list.Sessions[0], "Fix the build");
        list.DuplicateSession(list.Sessions[0], preset: null);
        Assert.Equal("Fix the build 2", list.Sessions[1].DisplayName);
        Assert.Same(list.Sessions[1], list.Active);
    }

    [Fact]
    public void ClosingTheActiveSessionSelectsTheNearestStartedOne()
    {
        var list = DemoList();
        list.OpenSession(Launch("one"));
        list.OpenSession(Launch("two"));
        list.OpenSession(Launch("three"));
        list.Close(list.Sessions[1]);
        Assert.Equal(2, list.Sessions.Count);
        Assert.NotNull(list.Active);
        list.Close(list.Sessions[0]);
        list.Close(list.Sessions[0]);
        Assert.Null(list.Active);
        Assert.False(list.HasSessions);
    }

    [Fact]
    public void ShowingTheListWithNothingSelectedOpensOneSession()
    {
        var list = DemoList();
        list.SetVisible(true);
        var session = Assert.Single(list.Sessions);
        Assert.Same(session, list.Active);
        Assert.True(session.Panes[0].IsShown);
    }

    [Fact]
    public void SnapshotKeepsOnlyNamesThePersonGave()
    {
        var list = DemoList();
        list.OpenSession(Launch("one"));
        list.OpenSession(Launch("two"));
        list.Rename(list.Sessions[1], "Named");
        var workspace = list.Snapshot("Mine");
        var sessions = workspace.Sections.Single(s => s.Name == TerminalSessionList.DefaultSection).Sessions;
        Assert.Null(sessions[0].Name);
        Assert.Equal("Named", sessions[1].Name);
    }

    [Fact]
    public void KeysFromAPaneDriveTheList()
    {
        var list = DemoList();
        list.OpenSession(Launch("one"));
        list.OpenSession(Launch("two"));
        Assert.True(list.HandleAppKey(TerminalAction.Select, 0));
        Assert.Same(list.Sessions[0], list.Active);
        list.HandleAppKey(TerminalAction.Next, -1);
        Assert.Same(list.Sessions[1], list.Active);
        list.HandleAppKey(TerminalAction.Split, -1);
        Assert.Equal(2, list.Sessions[1].Panes.Count);
        list.HandleAppKey(TerminalAction.ToggleSessionList, -1);
        Assert.True(list.ListCollapsed);
        Assert.False(list.HandleAppKey(TerminalAction.None, -1));
    }

    [Fact]
    public void InsertRefusesWhenNoAgentTakesAPaste()
    {
        var list = DemoList();
        list.OpenSession(Launch("shell"));
        Assert.False(list.Insert("### Pull request"));
    }

    [Fact]
    public void FilterButtonsSayWhatTheyDo()
    {
        var on = new TerminalFilterButton(TerminalFilterKind.Running, "\uE945", "Running", true, 0);
        Assert.True(on.IsEnabled);
        Assert.Equal("Show all sessions", on.ToolTip);
        Assert.Equal("Running, 0 sessions, on", on.AutomationName);
        var off = new TerminalFilterButton(TerminalFilterKind.Paused, "\uE769", "Paused", false, 1);
        Assert.Equal("Paused, 1 session", off.AutomationName);
        Assert.False(new TerminalFilterButton(TerminalFilterKind.Active, "", "Active", false, 0).IsEnabled);
    }

    [Theory]
    [InlineData(false, false, "Close Session")]
    [InlineData(true, true, "Stop Session")]
    [InlineData(true, false, "Remove from List")]
    public void CloseSaysWhatItDoes(bool repoRow, bool started, string title) =>
        Assert.Equal(title, TerminalSession.CloseTitleFor(repoRow, started));

    [Fact]
    public void SessionStateCombinesItsPanes()
    {
        Assert.Equal(ActivityState.Attention, TerminalSession.Combine(new[] { ActivityState.Idle, ActivityState.Attention }));
        Assert.Equal(ActivityState.Active, TerminalSession.Combine(new[] { ActivityState.Exited, ActivityState.Active }));
        Assert.Equal(ActivityState.Exited, TerminalSession.Combine(new[] { ActivityState.Exited, ActivityState.Exited }));
        Assert.Equal(ActivityState.Idle, TerminalSession.Combine(new[] { ActivityState.Exited, ActivityState.Idle }));
    }

    // ── What both apps publish and hand over ─────────────────────────────

    [Fact]
    public void PullRequestSelectionHasTheSameShapeInBothApps()
    {
        var pr = new UnifiedPullRequest
        {
            Source = PullRequestSource.GitHub, Repository = "sample-app", Container = "example-org", Number = 12,
            Title = "Tidy the README", SourceBranch = "docs/readme", TargetBranch = "main", AuthorName = "Sample Author",
            WebUrl = "https://example.com/pr/12",
        };
        var selection = Assert.Single(AppContextSelections.PullRequest(pr));
        Assert.Equal("pullRequest", selection.Kind);
        Assert.Equal("sample-app#12", selection.Id);
        Assert.Equal("example-org/sample-app", selection.Fields["repository"]);
        Assert.Empty(AppContextSelections.PullRequest(null));
    }

    [Fact]
    public void AgentLabelsMatchAcrossApps()
    {
        Assert.Equal("Copy for Agent", AgentContextLabels.CopyHeader);
        Assert.Equal("Send to Agent", AgentContextLabels.SendHeader);
        Assert.Equal("Copy this item for an agent", AgentContextLabels.ButtonToolTip(null, copyOnly: true));
    }
}
