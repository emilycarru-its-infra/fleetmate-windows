using System.Net;
using System.Globalization;
using System.Text;

namespace FleetMate.Core.Shared;

/// <summary>Plain and HTML clipboard representations of a titled record link.</summary>
public sealed record RecordLinkPayload(string Text, string Html)
{
    public static RecordLinkPayload Create(string title, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo)))
            throw new ArgumentException("A record web link is required.", nameof(url));
        var fragment = $"<a href=\"{WebUtility.HtmlEncode(url)}\">{WebUtility.HtmlEncode(title)}</a>";
        const string before = "<html><body><!--StartFragment-->";
        const string after = "<!--EndFragment--></body></html>";
        const string template = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        var start = Encoding.UTF8.GetByteCount(string.Format(CultureInfo.InvariantCulture, template, 0, 0, 0, 0));
        var fragmentStart = start + Encoding.UTF8.GetByteCount(before);
        var fragmentEnd = fragmentStart + Encoding.UTF8.GetByteCount(fragment);
        var end = fragmentEnd + Encoding.UTF8.GetByteCount(after);
        return new(title + "\n" + url, string.Format(CultureInfo.InvariantCulture, template, start, end, fragmentStart, fragmentEnd) + before + fragment + after);
    }
}
