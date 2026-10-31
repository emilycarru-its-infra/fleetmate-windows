using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FleetMate.Core.Models.Tickets;

/// <summary>One comment, on any ticket, that notified the signed-in person.</summary>
public sealed class TicketInboxItem
{
    [JsonPropertyName("feedId")] public int FeedId { get; set; }
    [JsonPropertyName("ticketId")] public int TicketId { get; set; }
    [JsonPropertyName("ticketTitle")] public string? TicketTitle { get; set; }
    [JsonPropertyName("author")] public string? Author { get; set; }
    [JsonPropertyName("created")] public DateTime Created { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("notifiedList")] public string? NotifiedList { get; set; }
    [JsonPropertyName("isPrivate")] public bool IsPrivate { get; set; }
}

/// <summary>
/// The inbox as saved for one person: what was found, what was read, and how
/// far the scan got. TDX has no read state, so <see cref="ReadIds"/> is local.
/// </summary>
public sealed class TicketInboxState
{
    [JsonPropertyName("items")] public List<TicketInboxItem> Items { get; set; } = new();
    [JsonPropertyName("readIds")] public HashSet<int> ReadIds { get; set; } = new();

    /// <summary>Start of the last full pass. The next pass searches from ten minutes before it.</summary>
    [JsonPropertyName("scannedThrough")] public DateTime? ScannedThrough { get; set; }

    /// <summary>Each scanned ticket's ModifiedDate, so an unchanged ticket is not read again.</summary>
    [JsonPropertyName("ticketModified")] public Dictionary<int, DateTime> TicketModified { get; set; } = new();

    [JsonIgnore]
    public int UnreadCount => Items.Count(i => !ReadIds.Contains(i.FeedId));

    public bool IsRead(int feedId) => ReadIds.Contains(feedId);

    public void MarkRead(int feedId) => ReadIds.Add(feedId);

    public void MarkAllRead()
    {
        foreach (var item in Items) ReadIds.Add(item.FeedId);
    }

    /// <summary>Replace one ticket's items with what its feed holds now.</summary>
    public void ReplaceTicket(int ticketId, IEnumerable<TicketInboxItem> items)
    {
        Items.RemoveAll(i => i.TicketId == ticketId);
        Items.AddRange(items);
        Items.Sort((a, b) => b.Created.CompareTo(a.Created));
    }

    /// <summary>Drop what has aged out of the window, and read marks for items no longer held.</summary>
    public void Prune(DateTime cutoffUtc)
    {
        Items.RemoveAll(i => ToUtc(i.Created) < cutoffUtc);
        var held = Items.Select(i => i.FeedId).ToHashSet();
        ReadIds.RemoveWhere(id => !held.Contains(id));
        foreach (var stale in TicketModified.Where(kv => ToUtc(kv.Value) < cutoffUtc).Select(kv => kv.Key).ToList())
            TicketModified.Remove(stale);
    }

    internal static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}

/// <summary>Who the inbox is for: the signed-in TDX person.</summary>
public sealed record TicketInboxPerson(Guid Uid, string? FullName, string? Email);

/// <summary>Deciding which feed entries belong in a person's inbox.</summary>
public static class TicketInboxMatch
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(90);

    /// <summary>
    /// Whether a feed entry's NotifiedList names the person. The list is
    /// comma-separated display names, sometimes "Name &lt;email&gt;": a name
    /// matches in full, ignoring case, and an email matches anywhere in an entry.
    /// </summary>
    public static bool NamesPerson(string? notifiedList, TicketInboxPerson person)
    {
        if (string.IsNullOrWhiteSpace(notifiedList)) return false;
        var name = person.FullName?.Trim();
        var email = person.Email?.Trim();

        foreach (var raw in notifiedList.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = System.Net.WebUtility.HtmlDecode(raw).Trim();
            if (!string.IsNullOrEmpty(email) && part.Contains(email, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.IsNullOrEmpty(name)) continue;
            var display = AngleEmail.Replace(part, "").Trim();
            if (string.Equals(display, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// The entries of one ticket's feed that belong in the inbox: they notified
    /// the person, the person did not write them, and they are inside the window.
    /// </summary>
    public static List<TicketInboxItem> Select(
        TdxTicket ticket, IEnumerable<TdxFeedEntry> feed, TicketInboxPerson person, DateTime cutoffUtc) =>
        feed
            .Where(e => TicketInboxState.ToUtc(e.CreatedDate) >= cutoffUtc)
            .Where(e => !WrittenBy(e, person))
            .Where(e => NamesPerson(e.NotifiedList, person))
            .Select(e => new TicketInboxItem
            {
                FeedId = e.Id,
                TicketId = ticket.Id,
                TicketTitle = ticket.Title,
                Author = e.CreatedFullName,
                Created = TicketInboxState.ToUtc(e.CreatedDate),
                Body = Plain(e.Body),
                NotifiedList = e.NotifiedList,
                IsPrivate = e.IsPrivate,
            })
            .ToList();

    private static bool WrittenBy(TdxFeedEntry entry, TicketInboxPerson person) =>
        entry.CreatedUid is { } uid && uid != Guid.Empty
            ? uid == person.Uid
            : !string.IsNullOrWhiteSpace(person.FullName)
              && string.Equals(entry.CreatedFullName?.Trim(), person.FullName.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Feed HTML as plain text, for a preview line.</summary>
    public static string Plain(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = Regex.Replace(html, @"<br\s*/?>|</p>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
    }

    private static readonly Regex AngleEmail = new(@"<[^>]*>", RegexOptions.Compiled);
}
