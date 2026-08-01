using System.Windows;
using FleetMate.Core.Models.Tickets;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>
/// One row in the ticket outline: the ticket, its indent, and its fold state.
///
/// Wraps <see cref="TicketRow"/> rather than replacing it — the nesting rules
/// live in Core where they are tested, and this only adds the WPF-shaped
/// properties a template can bind to.
/// </summary>
public sealed class TicketRowViewModel
{
    public required TicketRow Row { get; init; }

    public TdxTicket Ticket => Row.Ticket;
    public int Id => Row.Id;

    /// <summary>
    /// Indent for the nesting level. Applied to the row's content rather than
    /// the row itself so the selection highlight still spans the full width —
    /// an indented highlight reads as a rendering bug.
    /// </summary>
    public Thickness IndentMargin => new(4 + (Row.Depth * 16), 4, 4, 4);

    /// <summary>
    /// Indent for a board card. Narrower step than the list because a column is
    /// only 280px wide, and it keeps the 8px gap that separates stacked cards.
    /// </summary>
    public Thickness CardIndentMargin => new(Row.Depth * 12, 0, 0, 8);

    /// <summary>
    /// A right-pointing triangle when folded, down when open. Matching the
    /// glyphs to the direction the content moves is what makes a disclosure
    /// control legible without a label.
    ///
    /// These are real Unicode triangles (U+25BE / U+25B8) rather than Segoe MDL2
    /// private-use codepoints, which render as empty boxes in any font that is
    /// not MDL2 — and the XAML asks for Segoe UI Symbol.
    /// </summary>
    public string TriangleGlyph => Row.IsExpanded ? "▾" : "▸";

    /// <summary>
    /// Leaves get no triangle but still need the space, or their titles would
    /// sit left of their siblings' and break the visual column.
    /// </summary>
    public Visibility TriangleVisibility =>
        Row.HasChildren ? Visibility.Visible : Visibility.Hidden;

    /// <summary>"3 children" as a hint on the disclosure control.</summary>
    public string ChildCountTooltip =>
        Row.ChildCount == 1 ? "1 child ticket" : $"{Row.ChildCount} child tickets";

    // Passthroughs so the existing row template keeps its binding paths short.
    public string? Title => Ticket.Title;
    public string? StatusName => Ticket.StatusName;
    public string? PriorityName => Ticket.PriorityName;
    public string? RequestorName => Ticket.RequestorName;
    public string? ResponsibleGroupName => Ticket.ResponsibleGroupName;
    public string? ResponsibleFullName => Ticket.ResponsibleFullName;

    /// <summary>Days since opened, e.g. "412d" — a queue is read in days.</summary>
    public string AgeLabel => Ticket.AgeLabel();

    /// <summary>
    /// Days since last touched. Shown alongside age because they answer
    /// different questions: an old ticket touched today is healthy, a young one
    /// untouched for three weeks is not.
    /// </summary>
    public string LastActivityLabel => Ticket.LastActivityLabel();
}
