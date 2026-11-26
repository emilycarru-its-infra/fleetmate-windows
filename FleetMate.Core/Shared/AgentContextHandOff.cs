namespace FleetMate.Core.Shared;

/// <summary>
/// The words and glyphs of Copy for Agent and Send to Agent, the same in
/// every menu and on every detail view's Agent button in both desktop apps.
/// </summary>
public static class AgentContextLabels
{
    public const string CopyHeader = "Copy for Agent";
    public const string SendHeader = "Send to Agent";
    public const string CopyGlyph = "";
    public const string SendGlyph = "";
    /// <summary>The detail views' Agent button.</summary>
    public const string AgentGlyph = "";

    public const string SendToolTip =
        "Paste into the running agent's input without pressing Return; with none running, copy and open the terminal";

    /// <summary>What the Agent button's tooltip says a click does for <paramref name="kind"/>.</summary>
    public static string ButtonToolTip(AgentContext.ContextKind? kind, bool copyOnly)
    {
        var noun = kind is { } k ? AgentContext.Label(k).ToLowerInvariant() : "item";
        return copyOnly
            ? $"Copy this {noun} for an agent"
            : $"Send this {noun} to the running agent, or copy it when none is running; right-click to copy";
    }
}

/// <summary>
/// The terminal's side of a row dragged out as its agent context block. The
/// terminal is a web page, so it sees only the dropped text; it takes a drop
/// only while a drag FleetMate started is in flight (or just ended), and only
/// when the text is that drag's own block, so text dragged in from another
/// app never reaches the program.
/// </summary>
public static class AgentContextDragState
{
    /// <summary>The block's own format, beside the plain text.</summary>
    public const string Format = "FleetMateAgentContext";

    /// <summary>How long after a drag ends its drop may still arrive from the terminal page.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    private static readonly object Gate = new();
    private static string? _text;
    private static DateTime _endedAt = DateTime.MaxValue;

    /// <summary>A drag of a block started (true) or ended (false); the terminal shows the copy cursor only meanwhile.</summary>
    public static event Action<bool>? ActiveChanged;

    public static void Begin(string text)
    {
        lock (Gate)
        {
            _text = text;
            _endedAt = DateTime.MaxValue;
        }
        ActiveChanged?.Invoke(true);
    }

    public static void End(DateTime now)
    {
        lock (Gate) _endedAt = now;
        ActiveChanged?.Invoke(false);
    }

    /// <summary>
    /// The block for text dropped on the terminal: the current (or just
    /// ended) drag's own block, taken once, when <paramref name="dropped"/>
    /// is that block; null for anything else, so a drop from another app is
    /// ignored. The block FleetMate rendered is returned, not the page's copy.
    /// </summary>
    public static string? Claim(string? dropped, DateTime now)
    {
        lock (Gate)
        {
            var text = _text;
            if (text == null || string.IsNullOrEmpty(dropped)) return null;
            if (_endedAt != DateTime.MaxValue && now - _endedAt > Grace) { _text = null; return null; }
            if (Normalize(dropped) != Normalize(text)) return null;
            _text = null;
            return text;
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
}
