using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FleetMate.Core.Shared;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// Rows drag out as their agent context blocks: as plain text, so they land
/// wherever text goes, and onto the agent terminal, which hands them over the
/// way Send to Agent does. The terminal is a web page, so it sees only the
/// dropped text; it takes a drop only while a drag FleetMate started is in
/// flight, and only when the text is that drag's own block, so text dragged
/// in from another app never reaches the program.
/// </summary>
public static class AgentContextDrag
{
    /// <summary>The block's own format, beside the plain text.</summary>
    public const string Format = "FleetMateAgentContext";

    /// <summary>How long after a drag ends its drop may still arrive from the terminal page.</summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    private static string? _text;
    private static DateTime _endedAt = DateTime.MaxValue;

    /// <summary>A drag of a block started (true) or ended (false); the terminal shows the copy cursor only meanwhile.</summary>
    public static event Action<bool>? ActiveChanged;

    // ── Sources ──────────────────────────────────────────────────────────

    /// <summary>
    /// Drag the row under the pointer out of <paramref name="host"/> once it
    /// moves past the system threshold. <paramref name="targets"/> gives the
    /// blocks for the element pressed, or null when it is not a record.
    /// Presses on text, scroll bars, splitters and column headers are left
    /// alone, so selecting text and resizing still work.
    /// </summary>
    internal static void Attach(FrameworkElement host, Func<DependencyObject, IReadOnlyList<AgentContext>?> targets)
    {
        Point? start = null;
        DependencyObject? origin = null;
        host.PreviewMouseLeftButtonDown += (_, e) =>
        {
            origin = e.OriginalSource as DependencyObject;
            start = origin != null && !IsNoDragZone(origin, host) ? e.GetPosition(host) : null;
        };
        host.PreviewMouseLeftButtonUp += (_, _) => start = null;
        host.PreviewMouseMove += (_, e) =>
        {
            if (start is not { } from || origin == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { start = null; return; }
            var delta = e.GetPosition(host) - from;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            start = null;
            if (targets(origin) is not { Count: > 0 } contexts) return;
            DoDragDrop(host, new DataObject(), contexts, DragDropEffects.Copy);
        };
    }

    /// <summary>
    /// Run a drag that carries <paramref name="contexts"/> as text beside
    /// whatever <paramref name="data"/> already holds, such as a board card's
    /// own key for moving it between columns. Copy is always allowed, so the
    /// terminal can take it.
    /// </summary>
    public static DragDropEffects DoDragDrop(DependencyObject source, DataObject data, IReadOnlyList<AgentContext> contexts,
        DragDropEffects effects)
    {
        if (contexts.Count == 0) return DragDrop.DoDragDrop(source, data, effects);
        var text = AgentContextRenderer.Render(contexts);
        data.SetText(text, TextDataFormat.UnicodeText);
        data.SetData(Format, text);
        Begin(text);
        try { return DragDrop.DoDragDrop(source, data, effects | DragDropEffects.Copy); }
        finally { End(DateTime.UtcNow); }
    }

    private static bool IsNoDragZone(DependencyObject source, FrameworkElement host)
    {
        for (var node = source; node != null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is TextBoxBase or PasswordBox or ScrollBar or Thumb or DataGridColumnHeader or GridViewColumnHeader) return true;
            if (ReferenceEquals(node, host)) break;
        }
        return false;
    }

    // ── The terminal's side ──────────────────────────────────────────────

    internal static void Begin(string text)
    {
        _text = text;
        _endedAt = DateTime.MaxValue;
        ActiveChanged?.Invoke(true);
    }

    internal static void End(DateTime now)
    {
        _endedAt = now;
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
        var text = _text;
        if (text == null || string.IsNullOrEmpty(dropped)) return null;
        if (_endedAt != DateTime.MaxValue && now - _endedAt > Grace) { _text = null; return null; }
        if (Normalize(dropped) != Normalize(text)) return null;
        _text = null;
        return text;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
}
