using FleetMate.Core.Models.Tickets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FleetMate.WinUI.Pages;

/// <summary>Display values for one ticket in the list, the board and the detail pane.</summary>
public sealed class TicketRow
{
    /// <summary>
    /// Each status keeps one series slot everywhere it appears, so its colour
    /// follows the status rather than its rank in a chart.
    /// </summary>
    private static readonly string[] StatusOrder = ["New", "In Process", "Awaiting Input", "On Hold", "Resolved", "Closed"];

    public TicketRow(TdxTicket ticket)
    {
        Ticket = ticket;
        People = new[]
        {
            new PersonLine("Requestor", ticket.RequestorName ?? "-", ticket.RequestorEmail ?? string.Empty),
            new PersonLine("Responsible", ticket.ResponsibleFullName ?? TicketStats.Unassigned, ticket.ResponsibleGroupName ?? string.Empty),
            new PersonLine("Created by", ticket.CreatedFullName ?? "-", "via " + (ticket.SourceName ?? "-")),
        };
    }

    public TdxTicket Ticket { get; }

    public string IdText => $"#{Ticket.Id}";
    public string Title => Ticket.Title;
    public string Subtitle => $"{Ticket.RequestorName} · {Ticket.ResponsibleGroupName}";
    public string Status => Ticket.StatusName ?? "None";
    public string Priority => Ticket.PriorityName ?? "None";
    public string Responsible => string.IsNullOrWhiteSpace(Ticket.ResponsibleFullName) ? TicketStats.Unassigned : Ticket.ResponsibleFullName!;
    public bool IsUnassigned => string.IsNullOrWhiteSpace(Ticket.ResponsibleFullName);
    public string Age => Ticket.AgeLabel();
    public string Group => Ticket.ResponsibleGroupName ?? "-";
    public string Requestor => Ticket.RequestorName ?? "-";
    public string Type => Ticket.TypeName ?? "-";
    public string Service => Ticket.ServiceName ?? "-";
    public string Source => Ticket.SourceName ?? "-";
    public string Created => Ticket.CreatedDate.ToLocalTime().ToString("MMM d, h:mm tt");
    public string Modified => Ticket.ModifiedDate?.ToLocalTime().ToString("MMM d, h:mm tt") ?? "-";
    public string RespondBy => Ticket.RespondByDate?.ToLocalTime().ToString("MMM d, h:mm tt") ?? "-";
    public string ResolveBy => Ticket.ResolveByDate?.ToLocalTime().ToString("MMM d, h:mm tt") ?? "-";
    public string Sla => Ticket.IsSlaViolated ? $"{Ticket.SlaName} · Violated" : Ticket.SlaName ?? "None";
    public string Description => Ticket.Description ?? string.Empty;
    public string Eyebrow => $"{IdText} · {Ticket.TypeName} · {Ticket.ClassificationName}";

    public Visibility SlaVisibility => Ticket.IsSlaViolated ? Visibility.Visible : Visibility.Collapsed;

    public IReadOnlyList<PersonLine> People { get; }

    public Brush StatusBrush => SeriesBrush(StatusSlot(Status));

    public Brush ResponsibleForeground => (Brush)Application.Current.Resources[
        IsUnassigned ? "TextFillColorTertiaryBrush" : "TextFillColorPrimaryBrush"];

    public Brush PriorityForeground => (Brush)Application.Current.Resources[
        Priority is "Emergency" or "High" ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];

    public static int StatusSlot(string status)
    {
        var i = Array.IndexOf(StatusOrder, status);
        return i < 0 ? 6 : i + 1;
    }

    public static Brush SeriesBrush(int slot) => (Brush)Application.Current.Resources[$"Series{slot}Brush"];

    public bool Matches(string query) =>
        string.IsNullOrWhiteSpace(query)
        || Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || IdText.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Requestor.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Responsible.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Group.Contains(query, StringComparison.CurrentCultureIgnoreCase);
}

public sealed record PersonLine(string Role, string Name, string Detail)
{
    public string Initials => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => p[0]));
}

public enum FeedKind { Comment, Edit, Status }

/// <summary>One activity entry. TDX logs edits and status changes as feed entries; their wording tells them apart.</summary>
public sealed class FeedItem
{
    public FeedItem(TdxFeedEntry entry)
    {
        Entry = entry;
        var body = entry.Body ?? string.Empty;
        Kind = body.StartsWith("Changed Status", StringComparison.Ordinal) ? FeedKind.Status
            : body.StartsWith("Changed ", StringComparison.Ordinal) || body.StartsWith("Ticket created", StringComparison.Ordinal) ? FeedKind.Edit
            : FeedKind.Comment;
    }

    public TdxFeedEntry Entry { get; }
    public FeedKind Kind { get; }
    public string Author => Entry.CreatedFullName ?? "System";
    public string Body => Entry.Body ?? string.Empty;
    public string When => Entry.CreatedDate.ToLocalTime().ToString("MMM d, h:mm tt");
    public string Glyph => Kind switch { FeedKind.Status => "", FeedKind.Edit => "", _ => "" };
    public string KindLabel => Kind switch { FeedKind.Status => "Status", FeedKind.Edit => "Edit", _ => "Comment" };
    public Visibility PrivateVisibility => Entry.IsPrivate ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CommentVisibility => Kind == FeedKind.Comment ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EventVisibility => Kind == FeedKind.Comment ? Visibility.Collapsed : Visibility.Visible;
}
