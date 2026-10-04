using FleetMate.Core.Services.Terminal;
using Xunit;

namespace FleetMate.Tests;

public class TerminalSessionListTests
{
    // ── OSC title and directory ──────────────────────────────────────────

    [Theory]
    [InlineData("\x1b]0;claude: fix tests\a", "claude: fix tests")]
    [InlineData("\x1b]2;renamed\x1b\\", "renamed")]
    public void Scanner_ReadsTitles(string output, string title) =>
        Assert.Equal(new TerminalSignal[] { new TitleSignal(title) }, new TerminalSignalScanner().Scan(output));

    [Theory]
    [InlineData("\x1b]7;file://host/C:/Users/pat/src/repo\a", @"C:\Users\pat\src\repo")]
    [InlineData("\x1b]7;file://host/C:/My%20Docs\x1b\\", @"C:\My Docs")]
    [InlineData("\x1b]9;9;\"C:\\work\\repo\"\x1b\\", @"C:\work\repo")]
    public void Scanner_ReadsDirectories(string output, string path) =>
        Assert.Equal(new TerminalSignal[] { new DirectorySignal(path) }, new TerminalSignalScanner().Scan(output));

    [Fact]
    public void Scanner_JoinsASequenceSplitAcrossChunks()
    {
        var scanner = new TerminalSignalScanner();
        Assert.Empty(scanner.Scan("PS> \x1b]0;my ti"));
        Assert.Empty(scanner.Scan("tle"));
        Assert.Equal(new TerminalSignal[] { new TitleSignal("my title") }, scanner.Scan("\a done"));
    }

    [Fact]
    public void Scanner_CountsOnlyBellsOutsideSequences()
    {
        var signals = new TerminalSignalScanner().Scan("ding\a \x1b]0;t\a \x1b[31mred\x1b[0m");
        Assert.Equal(new TerminalSignal[] { new BellSignal(), new TitleSignal("t") }, signals);
    }

    [Fact]
    public void Scanner_IgnoresOtherOscCodes() =>
        Assert.Empty(new TerminalSignalScanner().Scan("\x1b]8;;https://example.invalid\a link \x1b]8;;\a"));

    // ── Directory display ────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\Users\pat", "~")]
    [InlineData(@"C:\Users\pat\src\repo", @"~\src\repo")]
    [InlineData(@"D:\work", @"D:\work")]
    [InlineData(@"D:\", @"D:\")]
    [InlineData(@"C:\Users\pat\src\organisation\very-long-repository-name\sub", @"…\very-long-repository-name\sub")]
    public void Compact_IsHomeRelativeAndHeadTruncated(string path, string expected) =>
        Assert.Equal(expected, TerminalPathDisplay.Compact(path, @"C:\Users\pat", 32));

    [Fact]
    public void Compact_TrimsASingleSegmentThatIsTooLong()
    {
        var shown = TerminalPathDisplay.Compact(@"D:\" + new string('x', 60), @"C:\Users\pat", 20);
        Assert.StartsWith("…\\", shown);
        Assert.True(shown.Length <= 22);
    }

    // ── Activity dot ─────────────────────────────────────────────────────

    [Fact]
    public void Activity_GoesGreenOnOutputAndDecays()
    {
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var activity = new SessionActivity();
        Assert.Equal(ActivityState.Idle, activity.State(t0));
        activity.Output(t0);
        Assert.Equal(ActivityState.Active, activity.State(t0.AddSeconds(1)));
        Assert.Equal(ActivityState.Idle, activity.State(t0.AddSeconds(1.6)));
    }

    [Fact]
    public void Activity_BellAsksForAttentionOnlyWhenHidden_UntilShown()
    {
        var now = DateTime.UtcNow;
        var activity = new SessionActivity();
        activity.Bell(visible: true);
        Assert.Equal(ActivityState.Idle, activity.State(now));

        activity.Bell(visible: false);
        activity.Output(now);
        Assert.Equal(ActivityState.Attention, activity.State(now));

        activity.Shown();
        Assert.Equal(ActivityState.Active, activity.State(now));
    }

    [Fact]
    public void Activity_ExitedWinsOverEverything()
    {
        var activity = new SessionActivity();
        activity.Bell(visible: false);
        activity.Exit();
        Assert.Equal(ActivityState.Exited, activity.State(DateTime.UtcNow));
    }

    // ── Key bindings ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("KeyT", true, true, false, TerminalAction.NewSession)]
    [InlineData("KeyW", true, true, false, TerminalAction.CloseSession)]
    [InlineData("KeyD", false, true, true, TerminalAction.Split)]
    [InlineData("KeyK", true, true, false, TerminalAction.Clear)]
    [InlineData("Tab", true, false, false, TerminalAction.Next)]
    [InlineData("Tab", true, true, false, TerminalAction.Previous)]
    [InlineData("Enter", true, true, false, TerminalAction.FullWindow)]
    public void Bindings_FollowWindowsTerminal(string code, bool ctrl, bool shift, bool alt, TerminalAction action) =>
        Assert.Equal(action, TerminalKeyBindings.Map(code, ctrl, shift, alt, out _));

    [Theory]
    [InlineData("KeyD")]
    [InlineData("KeyW")]
    [InlineData("KeyK")]
    [InlineData("KeyC")]
    [InlineData("Enter")]
    public void PlainCtrlChords_ReachTheShell(string code) =>
        Assert.Equal(TerminalAction.None, TerminalKeyBindings.Map(code, ctrl: true, shift: false, alt: false, out _));

    [Fact]
    public void CtrlDigits_SelectSessions()
    {
        Assert.Equal(TerminalAction.Select, TerminalKeyBindings.Map("Digit1", true, false, false, out var first));
        Assert.Equal(0, first);
        Assert.Equal(TerminalAction.Select, TerminalKeyBindings.Map("Digit9", true, false, false, out var ninth));
        Assert.Equal(8, ninth);
        Assert.Equal(TerminalAction.None, TerminalKeyBindings.Map("Digit0", true, false, false, out _));
    }

    [Fact]
    public void Chords_AreExactlyTheMappedOnes()
    {
        var chords = TerminalKeyBindings.Chords();
        Assert.Contains("110:KeyT", chords);
        Assert.Contains("011:KeyD", chords);
        Assert.Contains("100:Digit3", chords);
        Assert.Contains("110:Enter", chords);
        Assert.DoesNotContain("100:KeyD", chords);
        Assert.DoesNotContain("100:KeyW", chords);
    }

    // ── Full-window mode ─────────────────────────────────────────────────

    [Fact]
    public void Drag_PastThresholdGoesFullWindow_AndDragDownRestoresTheHeight()
    {
        var layout = new TerminalLayoutState(300);
        layout.BeginDrag();
        layout.Drag(420, 1000);
        Assert.False(layout.FullWindow);
        Assert.Equal(420, layout.Height);

        layout.Drag(860, 1000);
        Assert.True(layout.FullWindow);
        Assert.Equal(420, layout.RestoreHeight);

        layout.BeginDrag();
        layout.Drag(500, 1000);
        Assert.False(layout.FullWindow);
        Assert.Equal(420, layout.Height);
        // The rest of that drag leaves the restored height alone.
        layout.Drag(200, 1000);
        Assert.Equal(420, layout.Height);
    }

    [Fact]
    public void Toggle_RestoresThePreviousHeight()
    {
        var layout = new TerminalLayoutState(350);
        layout.Toggle(1000);
        Assert.True(layout.FullWindow);
        layout.Toggle(1000);
        Assert.False(layout.FullWindow);
        Assert.Equal(350, layout.Height);
    }

    [Fact]
    public void Height_IsClampedSoThePageKeepsRoom()
    {
        var layout = new TerminalLayoutState();
        layout.BeginDrag();
        layout.Drag(30, 1000);
        Assert.Equal(TerminalLayoutState.MinHeight, layout.Height);
        layout.Drag(845, 1000);
        Assert.False(layout.FullWindow);
        Assert.Equal(1000 - TerminalLayoutState.MinPageHeight, layout.Height);
    }
}

public class TerminalTitleTests
{
    [Theory]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", false)]
    [InlineData(@"C:\WINDOWS\system32\cmd.exe", false)]
    [InlineData("", false)]
    [InlineData("renamed", true)]
    [InlineData("claude: fix the build", true)]
    [InlineData(@"~\repo", true)]
    public void ExecutablePathTitlesAreIgnored(string title, bool meaningful) =>
        Xunit.Assert.Equal(meaningful, FleetMate.Core.Services.Terminal.TerminalTitle.IsMeaningful(title));
}
