using FleetMate.Core.Config;

namespace FleetMate.Core.Services.Terminal;

/// <summary>
/// The terminal's text size: the base size times the app's text size
/// (Settings › Appearance, Ctrl+= and Ctrl+-), plus the terminal's own offset
/// from Ctrl+= and Ctrl+- pressed inside a terminal, as in Windows Terminal.
/// </summary>
public static class TerminalFontSize
{
    public const double Base = 13;
    public const double Min = 8;
    public const double Max = 36;

    /// <summary>The size every session draws at, for a text scale and an offset in points.</summary>
    public static double For(double textScale, double offset)
    {
        var scaled = Math.Round(Base * AppTextScale.Clamp(textScale));
        return Math.Clamp(scaled + offset, Min, Max);
    }

    /// <summary>
    /// The offset after one step of <paramref name="points"/>, or the same
    /// offset when the step would leave the range.
    /// </summary>
    public static double Step(double textScale, double offset, double points)
    {
        var target = Math.Round(Base * AppTextScale.Clamp(textScale)) + offset + points;
        return target is >= Min and <= Max ? offset + points : offset;
    }
}

/// <summary>
/// What the strip at the bottom of the window says while the terminal is
/// closed: plain text, never a badge.
/// </summary>
public static class TerminalStripStatus
{
    public static string Describe(IReadOnlyCollection<ActivityState> sessions)
    {
        if (sessions.Count == 0) return "No sessions";
        var text = sessions.Count == 1 ? "1 session" : $"{sessions.Count} sessions";
        if (sessions.Contains(ActivityState.Attention)) text += " · waiting for you";
        else if (sessions.Contains(ActivityState.Active)) text += " · working";
        return text;
    }
}

/// <summary>
/// The two controls on the bar at the window's bottom edge, which stays there
/// whether the terminal is open or not: full window, and show or hide. The
/// shortcuts are in the tooltips rather than as hint text on the bar.
/// </summary>
public sealed record TerminalBarControls(
    bool FullWindow, string FullWindowName, string FullWindowTip,
    bool Showing, string ToggleName, string ToggleTip)
{
    public static TerminalBarControls For(bool showing, bool fullWindow)
    {
        // Full window means nothing while the terminal is closed.
        var full = showing && fullWindow;
        return new TerminalBarControls(
            full,
            full ? "Restore" : "Full Window",
            full ? "Restore the terminal to its height (Ctrl+Shift+Enter)" : "Fill the window with the terminal (Ctrl+Shift+Enter)",
            showing,
            showing ? "Hide Agent Terminal" : "Show Agent Terminal",
            showing ? "Hide the Agent Terminal (Ctrl+`)" : "Show the Agent Terminal (Ctrl+`)");
    }
}
