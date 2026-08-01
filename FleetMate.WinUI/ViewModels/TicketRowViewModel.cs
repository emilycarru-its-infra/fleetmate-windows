using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using FleetMate.Core.Models.Tickets;

namespace FleetMate.WinUI.ViewModels;

/// <summary>
/// Row display model for the Tickets list. Wraps a <see cref="TdxTicket"/> with
/// formatted columns + a status colour keyed off StatusClass.
/// </summary>
public sealed class TicketRowViewModel
{
    public TdxTicket Ticket { get; }

    public TicketRowViewModel(TdxTicket ticket) => Ticket = ticket;

    public string IdText => $"#{Ticket.Id}";
    public string Title => string.IsNullOrEmpty(Ticket.Title) ? "(no title)" : Ticket.Title;
    public string Status => Ticket.StatusName ?? "—";
    public string Priority => Ticket.PriorityName ?? "—";
    public string Requestor => Ticket.RequestorName ?? "—";
    public string Responsible => Ticket.ResponsibleFullName ?? Ticket.ResponsibleGroupName ?? "—";
    public string Modified => (Ticket.ModifiedDate ?? Ticket.CreatedDate).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public Brush StatusBrush => new SolidColorBrush(Ticket.StatusClass?.ToLowerInvariant() switch
    {
        "new" => Colors.Gold,
        "open" or "inprocess" or "none" => Colors.DodgerBlue,
        "onhold" => Colors.Orange,
        "closed" => Colors.Green,
        "cancelled" => Colors.Gray,
        _ => Colors.Gray,
    });

    public bool Matches(string q) =>
        Title.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Requestor.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Responsible.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Status.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Ticket.Id.ToString().Contains(q, StringComparison.OrdinalIgnoreCase);
}
