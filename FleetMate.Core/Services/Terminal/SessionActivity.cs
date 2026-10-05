namespace FleetMate.Core.Services.Terminal;

/// <summary>A session's activity dot: green while output streams, orange when a hidden session rings, grey otherwise.</summary>
public enum ActivityState { Idle, Active, Attention, Exited }

/// <summary>
/// Drives a session's activity dot. Output makes it Active, which decays to
/// Idle after <see cref="Decay"/> without output. A bell from a session that
/// is not on screen asks for Attention, which lasts until the session is
/// shown. An exited session stays Exited.
/// </summary>
public sealed class SessionActivity
{
    public static readonly TimeSpan Decay = TimeSpan.FromSeconds(1.5);

    private DateTime _lastOutput = DateTime.MinValue;
    private bool _attention;
    private bool _exited;

    public void Output(DateTime now) => _lastOutput = now;

    public void Bell(bool visible)
    {
        if (!visible && !_exited) _attention = true;
    }

    /// <summary>The person looked at the session: any attention request is answered.</summary>
    public void Shown() => _attention = false;

    public void Exit() => _exited = true;

    public ActivityState State(DateTime now)
    {
        if (_exited) return ActivityState.Exited;
        if (_attention) return ActivityState.Attention;
        return now - _lastOutput < Decay ? ActivityState.Active : ActivityState.Idle;
    }
}

/// <summary>What a key chord inside a terminal asks the panel to do.</summary>
public enum TerminalAction { None, NewSession, CloseSession, Split, Clear, Select, Next, Previous, FullWindow }

/// <summary>
/// The panel's key bindings, following Windows Terminal. Each needs Ctrl+Shift
/// or Alt+Shift, so plain Ctrl+D, Ctrl+W and Ctrl+K still reach the shell;
/// Ctrl+1…9 select a session (shells give those chords no meaning).
/// </summary>
public static class TerminalKeyBindings
{
    public sealed record Binding(string Keys, TerminalAction Action, string Description);

    public static readonly IReadOnlyList<Binding> All = new[]
    {
        new Binding("Ctrl+Shift+T", TerminalAction.NewSession, "New session (Ctrl+T also works anywhere in the app)"),
        new Binding("Ctrl+Shift+W", TerminalAction.CloseSession, "Close session"),
        new Binding("Alt+Shift+D", TerminalAction.Split, "Split"),
        new Binding("Ctrl+Shift+K", TerminalAction.Clear, "Clear"),
        new Binding("Ctrl+1…9", TerminalAction.Select, "Select session 1–9"),
        new Binding("Ctrl+Tab", TerminalAction.Next, "Next session"),
        new Binding("Ctrl+Shift+Tab", TerminalAction.Previous, "Previous session"),
        new Binding("Ctrl+Shift+Enter", TerminalAction.FullWindow, "Full-window terminal on and off (also works anywhere in the app)"),
    };

    /// <summary>Every chord that maps to an action, as "ctrl,shift,alt:code" — the page swallows exactly these.</summary>
    public static IReadOnlyList<string> Chords()
    {
        var codes = new[] { "KeyT", "KeyW", "KeyD", "KeyK", "Tab", "Enter", "NumpadEnter" }
            .Concat(Enumerable.Range(1, 9).Select(i => $"Digit{i}"));
        var chords = new List<string>();
        foreach (var code in codes)
            for (var m = 0; m < 8; m++)
            {
                bool ctrl = (m & 1) != 0, shift = (m & 2) != 0, alt = (m & 4) != 0;
                if (Map(code, ctrl, shift, alt, out _) != TerminalAction.None)
                    chords.Add($"{(ctrl ? 1 : 0)}{(shift ? 1 : 0)}{(alt ? 1 : 0)}:{code}");
            }
        return chords;
    }

    /// <summary>
    /// Map a keydown (as the browser names it: KeyboardEvent.code) to an
    /// action. <paramref name="index"/> is the 0-based session for Select.
    /// </summary>
    public static TerminalAction Map(string code, bool ctrl, bool shift, bool alt, out int index)
    {
        index = -1;
        if (ctrl && !alt && code.StartsWith("Digit") && code.Length == 6 && code[5] is >= '1' and <= '9' && !shift)
        {
            index = code[5] - '1';
            return TerminalAction.Select;
        }
        return (code, ctrl, shift, alt) switch
        {
            ("KeyT", true, true, false) => TerminalAction.NewSession,
            ("KeyW", true, true, false) => TerminalAction.CloseSession,
            ("KeyD", false, true, true) => TerminalAction.Split,
            ("KeyK", true, true, false) => TerminalAction.Clear,
            ("Tab", true, false, false) => TerminalAction.Next,
            ("Tab", true, true, false) => TerminalAction.Previous,
            ("Enter", true, true, false) => TerminalAction.FullWindow,
            ("NumpadEnter", true, true, false) => TerminalAction.FullWindow,
            _ => TerminalAction.None,
        };
    }
}

/// <summary>
/// The panel's height and full-window mode. Dragging the divider above
/// <see cref="FullWindowThreshold"/> of the space, the shortcut, or the button
/// fills the space below the tab bar with the terminal; dragging it back down,
/// or the same shortcut or button, restores the page and the height from before.
/// </summary>
public sealed class TerminalLayoutState
{
    public const double FullWindowThreshold = 0.85;
    public const double MinHeight = 120;
    /// <summary>The page keeps at least this much room when the terminal is not full-window.</summary>
    public const double MinPageHeight = 160;

    public double Height { get; private set; }
    public bool FullWindow { get; private set; }

    /// <summary>The height to restore when full-window mode ends.</summary>
    public double RestoreHeight { get; private set; }

    public TerminalLayoutState(double height = 300)
    {
        Height = height;
        RestoreHeight = height;
    }

    /// <summary>
    /// The divider is being dragged so the panel would be <paramref name="height"/>
    /// tall in <paramref name="available"/> space (everything below the tab bar).
    /// </summary>
    public void Drag(double height, double available)
    {
        if (available <= 0 || _restoredThisDrag) return;
        if (height >= available * FullWindowThreshold)
        {
            if (!FullWindow) { RestoreHeight = Clamp(Height, available); FullWindow = true; }
            return;
        }
        if (FullWindow)
        {
            // Dragging down out of full-window mode puts back the height from
            // before; the rest of this drag leaves it there.
            FullWindow = false;
            Height = Clamp(RestoreHeight, available);
            _restoredThisDrag = true;
            return;
        }
        Height = Clamp(height, available);
    }

    private bool _restoredThisDrag;

    public void BeginDrag() => _restoredThisDrag = false;

    public void Toggle(double available)
    {
        if (FullWindow)
        {
            FullWindow = false;
            Height = Clamp(RestoreHeight, available);
        }
        else
        {
            RestoreHeight = Clamp(Height, available);
            FullWindow = true;
        }
    }

    private static double Clamp(double height, double available) =>
        Math.Max(MinHeight, Math.Min(height, Math.Max(MinHeight, available - MinPageHeight)));
}
