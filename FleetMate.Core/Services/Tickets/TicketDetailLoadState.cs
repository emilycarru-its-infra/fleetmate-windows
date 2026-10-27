namespace FleetMate.Core.Services.Tickets;

/// <summary>A request identity survives selecting the same ticket again without accepting an older response.</summary>
public sealed class TicketDetailLoadState
{
    private long _generation;
    public readonly record struct Request(int TicketId, long Generation);

    public Request Begin(int ticketId) => new(ticketId, ++_generation);
    public bool IsCurrent(Request request) => request.Generation == _generation;
    public void Invalidate() => ++_generation;
}

/// <summary>Comment text, visibility and recipients belong to the same ticket draft.</summary>
public sealed record TicketCommentDraft(string Text, bool IsPrivate, string[] NotifyEmails)
{
    public static TicketCommentDraft Empty => new("", false, []);
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text) && !IsPrivate && NotifyEmails.Length == 0;

    public bool Matches(TicketCommentDraft other) => Text == other.Text && IsPrivate == other.IsPrivate
        && NotifyEmails.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(other.NotifyEmails);

    public static string? NotificationEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        return System.Net.Mail.MailAddress.TryCreate(text, out var address)
            && address.Address.Equals(text, StringComparison.OrdinalIgnoreCase) ? address.Address : null;
    }
}
