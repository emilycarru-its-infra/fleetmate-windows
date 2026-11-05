namespace FleetMate.Core.Services.Tickets;

/// <summary>
/// Which ticket detail load may still paint. Every open, refresh and close
/// starts a new generation, so going from ticket A to B and back to A never
/// lets the first, slower answer for A replace the newer one.
/// </summary>
public sealed class TicketDetailLoadState
{
    private long _generation;

    public readonly record struct Request(int TicketId, long Generation);

    /// <summary>The load now allowed to paint, or null after a close.</summary>
    public Request? Current { get; private set; }

    public Request Begin(int ticketId)
    {
        var request = new Request(ticketId, ++_generation);
        Current = request;
        return request;
    }

    public bool IsCurrent(Request request) => Current is { } current && current == request;

    /// <summary>Drop every load in flight, as when the ticket is closed.</summary>
    public void Invalidate()
    {
        ++_generation;
        Current = null;
    }
}

/// <summary>
/// An unsent comment and the choices made with it: whether it is private and
/// which notify boxes are ticked, by recipient id. All three belong to the
/// ticket the comment was written on.
/// </summary>
public sealed record TicketCommentDraft(string Text, bool IsPrivate, IReadOnlyCollection<string> NotifyIds)
{
    public static TicketCommentDraft Empty { get; } = new("", false, Array.Empty<string>());

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text) && !IsPrivate && NotifyIds.Count == 0;

    /// <summary>The same text, visibility and recipients, in any order.</summary>
    public bool Matches(TicketCommentDraft other) =>
        Text == other.Text && IsPrivate == other.IsPrivate
        && NotifyIds.ToHashSet(StringComparer.Ordinal).SetEquals(other.NotifyIds);
}
