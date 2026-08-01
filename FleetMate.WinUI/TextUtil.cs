using System.Net;
using System.Text.RegularExpressions;

namespace FleetMate.WinUI;

/// <summary>Small text helpers shared across pages.</summary>
public static class TextUtil
{
    /// <summary>Flatten an HTML fragment (TDX / Azure DevOps descriptions) to readable plain text.</summary>
    public static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "—";
        var text = Regex.Replace(html, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "</p>", "\n\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "</div>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<li>", "• ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, "\n{3,}", "\n\n").Trim();
        return text.Length == 0 ? "—" : text;
    }
}
