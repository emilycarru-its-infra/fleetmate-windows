using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace FleetMate.Core.Models.Tickets;

/// <summary>Which entries the ticket's Activity shows.</summary>
public enum TicketFeedFilter
{
    /// <summary>People's comments: no system entries, nothing without a body.</summary>
    Comments,

    /// <summary>Everything people did, including edits that carry no text.</summary>
    Activity,

    /// <summary>Every entry, system ones too.</summary>
    All,
}

/// <summary>
/// How a ticket's description and activity read in the detail pane: plain
/// text from TeamDynamix's HTML, the dates, and which entries show.
/// </summary>
public static partial class TicketFeedView
{
    /// <summary>The newest entries shown; older ones stay a click away in TeamDynamix.</summary>
    public const int MaxShown = 20;

    public static List<TdxFeedEntry> Filter(IEnumerable<TdxFeedEntry> feed, TicketFeedFilter filter) => filter switch
    {
        TicketFeedFilter.Activity => feed.Where(f => f.CreatedFullName != "System").ToList(),
        TicketFeedFilter.All => feed.ToList(),
        _ => feed.Where(f => f.CreatedFullName != "System" && !string.IsNullOrWhiteSpace(f.Body)).ToList(),
    };

    /// <summary>"Activity (4)", or "Activity" when nothing shows.</summary>
    public static string Header(int shown) => shown > 0 ? $"Activity ({shown})" : "Activity";

    /// <summary>"Oct 15 at 7:01 PM", in local time.</summary>
    public static string FormatDate(DateTime? value, IFormatProvider? culture = null)
    {
        if (value is not { } date) return "-";
        var local = date.Kind == DateTimeKind.Utc ? date.ToLocalTime() : date;
        return local.ToString("MMM d 'at' h:mm tt", culture ?? CultureInfo.CurrentCulture);
    }

    /// <summary>"Notified: …", or nothing when TeamDynamix sent no names.</summary>
    public static string NotifiedLine(string? notifiedList) =>
        string.IsNullOrWhiteSpace(notifiedList) ? "" : $"Notified: {WebUtility.HtmlDecode(notifiedList.Trim())}";

    /// <summary>
    /// TeamDynamix HTML as plain text: line and paragraph breaks become new
    /// lines, the tags go, the common entities decode and runs of blank lines
    /// shrink to one.
    /// </summary>
    public static string PlainText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var result = LineBreak().Replace(html, "\n");
        result = ParagraphEnd().Replace(result, "\n");
        result = Tag().Replace(result, "");
        result = result.Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'")
            .Replace("&apos;", "'");
        result = BlankLines().Replace(result, "\n\n");
        return result.Trim();
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex("</p>", RegexOptions.IgnoreCase)]
    private static partial Regex ParagraphEnd();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
