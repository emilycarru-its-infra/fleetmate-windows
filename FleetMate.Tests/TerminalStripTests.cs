using FleetMate.Core.Services.Terminal;
using Xunit;

namespace FleetMate.Tests;

public class TerminalStripTests
{
    // ── Folding the panel into the strip ─────────────────────────────────

    [Fact]
    public void DragBelowTheSnap_ClosesAndKeepsTheHeightFromBefore()
    {
        var layout = new TerminalLayoutState(320);
        layout.BeginDrag();
        layout.Drag(60, 1000);
        // The panel shrinks visibly toward the snap while dragged.
        Assert.Equal(60, layout.DisplayHeight);
        Assert.True(layout.EndDrag());
        Assert.Equal(320, layout.Height);
        Assert.Equal(320, layout.DisplayHeight);
    }

    [Fact]
    public void DragAboveTheSnap_StaysOpenAtTheMinimum()
    {
        var layout = new TerminalLayoutState(320);
        layout.BeginDrag();
        layout.Drag(100, 1000);
        Assert.Equal(100, layout.DisplayHeight);
        Assert.False(layout.EndDrag());
        Assert.Equal(TerminalLayoutState.MinHeight, layout.Height);
        Assert.Equal(TerminalLayoutState.MinHeight, layout.DisplayHeight);
    }

    [Fact]
    public void DragDisplay_NeverDrawsBelowTheFloor()
    {
        var layout = new TerminalLayoutState(320);
        layout.BeginDrag();
        layout.Drag(-40, 1000);
        Assert.Equal(TerminalLayoutState.DragFloor, layout.DisplayHeight);
    }

    [Fact]
    public void AClickWithoutADrag_DoesNotClose()
    {
        var layout = new TerminalLayoutState(320);
        layout.BeginDrag();
        Assert.False(layout.EndDrag());
        Assert.Equal(320, layout.Height);
    }

    [Fact]
    public void DraggingDownOutOfFullWindow_DoesNotClose()
    {
        var layout = new TerminalLayoutState(320);
        layout.Toggle(1000);
        layout.BeginDrag();
        layout.Drag(40, 1000);
        Assert.False(layout.EndDrag());
        Assert.False(layout.FullWindow);
        Assert.Equal(320, layout.Height);
    }

    // ── What the strip says ──────────────────────────────────────────────

    [Fact]
    public void Status_IsPlainText()
    {
        Assert.Equal("No sessions", TerminalStripStatus.Describe([]));
        Assert.Equal("1 session", TerminalStripStatus.Describe([ActivityState.Idle]));
        Assert.Equal("2 sessions · working", TerminalStripStatus.Describe([ActivityState.Idle, ActivityState.Active]));
        Assert.Equal("1 session · waiting for you", TerminalStripStatus.Describe([ActivityState.Attention]));
        // Waiting for you outranks working.
        Assert.Equal("3 sessions · waiting for you",
            TerminalStripStatus.Describe([ActivityState.Active, ActivityState.Attention, ActivityState.Exited]));
    }

    // ── Text size ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1.0, 0, 13)]
    [InlineData(1.6, 0, 21)]
    [InlineData(0.9, 0, 12)]
    [InlineData(1.0, 3, 16)]
    [InlineData(1.0, -20, TerminalFontSize.Min)]
    [InlineData(1.6, 40, TerminalFontSize.Max)]
    public void FontSize_FollowsTheAppTextSizePlusTheOffset(double scale, double offset, double expected) =>
        Assert.Equal(expected, TerminalFontSize.For(scale, offset));

    [Fact]
    public void FontStep_StopsAtTheEdgesOfTheRange()
    {
        Assert.Equal(1, TerminalFontSize.Step(1.0, 0, 1));
        Assert.Equal(-1, TerminalFontSize.Step(1.0, 0, -1));
        // 13 + 23 = 36 is the largest; one more step is refused.
        Assert.Equal(23, TerminalFontSize.Step(1.0, 23, 1));
        // 13 - 5 = 8 is the smallest.
        Assert.Equal(-5, TerminalFontSize.Step(1.0, -5, -1));
    }

    [Theory]
    [InlineData("Equal", false, TerminalAction.ZoomIn)]
    [InlineData("Equal", true, TerminalAction.ZoomIn)]
    [InlineData("NumpadAdd", false, TerminalAction.ZoomIn)]
    [InlineData("Minus", false, TerminalAction.ZoomOut)]
    [InlineData("NumpadSubtract", false, TerminalAction.ZoomOut)]
    [InlineData("Digit0", false, TerminalAction.ActualSize)]
    [InlineData("Numpad0", false, TerminalAction.ActualSize)]
    public void ZoomKeysInsideATerminal_SizeItsText(string code, bool shift, TerminalAction action)
    {
        Assert.Equal(action, TerminalKeyBindings.Map(code, ctrl: true, shift, alt: false, out _));
        Assert.Contains($"1{(shift ? 1 : 0)}0:{code}", TerminalKeyBindings.Chords());
    }

    [Fact]
    public void CtrlShiftMinus_StillReachesTheShell() =>
        // Ctrl+_ is undo in readline-style shells.
        Assert.Equal(TerminalAction.None, TerminalKeyBindings.Map("Minus", ctrl: true, shift: true, alt: false, out _));

    // ── The bar's controls ──────────────────────────────────────────────

    [Fact]
    public void ClosedBar_OffersShowAndFullWindow()
    {
        var bar = TerminalBarControls.For(showing: false, fullWindow: true);
        Assert.False(bar.FullWindow);
        Assert.Equal("Full Window", bar.FullWindowName);
        Assert.Equal("Show Agent Terminal", bar.ToggleName);
        Assert.Contains("Ctrl+`", bar.ToggleTip);
    }

    [Fact]
    public void OpenBar_OffersHide_AndRestoreWhenFillingTheWindow()
    {
        var open = TerminalBarControls.For(showing: true, fullWindow: false);
        Assert.Equal("Hide Agent Terminal", open.ToggleName);
        Assert.Equal("Full Window", open.FullWindowName);
        var full = TerminalBarControls.For(showing: true, fullWindow: true);
        Assert.True(full.FullWindow);
        Assert.Equal("Restore", full.FullWindowName);
        Assert.Contains("Ctrl+Shift+Enter", full.FullWindowTip);
    }
}
