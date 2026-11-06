using System.Net;
using System.Text;
using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Tickets;

/// <summary>
/// The body of <c>POST /api/feed/{id}/replies</c>, the threaded-reply route
/// TeamDynamix added in 12.3 (OpenAPI operation <c>Feed_AddReply</c>,
/// schema <c>FeedReplyRequest</c>). <c>Body</c> is the only field: a reply,
/// unlike a ticket comment, has no Notify list and no Private flag of its
/// own, and TDX reads the body as rich HTML.
/// </summary>
public sealed class TdxFeedReplyRequest
{
    [JsonPropertyName("Body")]
    public string Body { get; init; } = "";

    /// <summary>A reply typed as plain text, escaped so it posts exactly as typed.</summary>
    public static TdxFeedReplyRequest FromPlainText(string text) => new() { Body = Html(text) };

    /// <summary>The Web API path that deletes one reply in a comment's thread, as the Mac sends it.</summary>
    public static string DeletePath(int entryId, int replyId) => $"feed/{entryId}/replies/{replyId}";

    /// <summary>
    /// Whether the signed-in person may delete a reply: only their own, as on
    /// the Mac. Unknown on either side is no.
    /// </summary>
    public static bool CanDelete(Guid? replyAuthor, Guid? me) =>
        replyAuthor is { } author && author != Guid.Empty && me is { } self && author == self;

    /// <summary>
    /// Plain text as the HTML TDX expects: the markup characters escaped and
    /// each line break as <c>&lt;br /&gt;</c>, as FleetMate for Mac sends it.
    /// </summary>
    public static string Html(string? plainText)
    {
        var text = (plainText ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        var html = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': html.Append("&amp;"); break;
                case '<': html.Append("&lt;"); break;
                case '>': html.Append("&gt;"); break;
                case '"': html.Append("&quot;"); break;
                case '\'': html.Append("&#39;"); break;
                case '\n': html.Append("<br />"); break;
                default: html.Append(c); break;
            }
        }
        return html.ToString();
    }
}

/// <summary>A threaded reply TeamDynamix did not take, with a message the operator can act on.</summary>
public sealed class TdxFeedReplyException : Exception
{
    public int EntryId { get; }
    public int? StatusCode { get; }

    public TdxFeedReplyException(int entryId, int? statusCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        EntryId = entryId;
        StatusCode = statusCode;
    }

    /// <summary>Why a reply failed, in words: the Mac's wording for each status.</summary>
    public static string Describe(HttpStatusCode? status) => (int?)status switch
    {
        null => "TeamDynamix could not be reached. Check the connection and try again.",
        401 => "Your TeamDynamix session has expired. Sign in again.",
        403 => "You don't have permission to reply to this comment.",
        404 => "That comment is no longer on the ticket, or this TeamDynamix site predates threaded replies (12.3). Refresh the ticket.",
        var code => $"TeamDynamix couldn't post the reply (HTTP {code}). Refresh and try again.",
    };

    /// <summary>Why deleting a reply failed, in words.</summary>
    public static string DescribeDelete(HttpStatusCode? status) => (int?)status switch
    {
        null => "TeamDynamix could not be reached. Check the connection and try again.",
        401 => "Your TeamDynamix session has expired. Sign in again.",
        403 => "You don't have permission to delete this reply.",
        404 => "That reply is no longer on the ticket, or this TeamDynamix site predates threaded replies (12.3). Refresh the ticket.",
        var code => $"TeamDynamix couldn't delete the reply (HTTP {code}). Refresh and try again.",
    };
}

/// <summary>
/// A reply being written under one feed entry. Kept per entry, so closing the
/// composer or replying in another thread never loses what was typed.
/// </summary>
public static class FeedReplyDrafts
{
    /// <summary>Store <paramref name="text"/> as the draft for <paramref name="entryId"/>, or drop it when blank.</summary>
    public static void Keep(IDictionary<int, string> drafts, int entryId, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) drafts.Remove(entryId);
        else drafts[entryId] = text!;
    }

    /// <summary>
    /// After a reply posted: drop the draft only when it still reads as what
    /// was sent, so anything typed while the request ran is kept.
    /// </summary>
    public static void Posted(IDictionary<int, string> drafts, int entryId, string submitted)
    {
        if (drafts.TryGetValue(entryId, out var current) && current == submitted) drafts.Remove(entryId);
    }
}
