using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FleetMate.Core.Models.Tickets;

namespace FleetMate.Core.Services.Tickets;

/// <summary>
/// The task-template list behind Apply Template. The documented TDWebApi can
/// apply a task template (<c>POST /tasks/applytemplate</c>) but has no route
/// that lists them; the list TeamDynamix itself offers is the drop-down of a
/// ticket's Add Task Template page,
/// <c>/TDNext/Apps/{appId}/Tickets/TicketAddTaskTemplate?TicketID={id}</c>.
/// The page is read in the operator's own signed-in web session and never
/// submitted: the template is still applied through the API.
///
/// This class is the framework-free half: the page's address, the check that a
/// loaded document is that page, the read-only script and the reading of its
/// answer. The GUI owns the browser.
/// </summary>
public static class TdxTaskTemplatePicker
{
    /// <summary>The picker page for <paramref name="ticketId"/>, or null when no HTTPS web address is configured.</summary>
    public static Uri? PageUrl(TdxConfig config, int ticketId)
    {
        var root = config.GetWebRootUrl();
        if (root == null || ticketId <= 0) return null;
        var appId = config.TicketingAppId ?? config.AppId;
        return Uri.TryCreate($"{root}/TDNext/Apps/{appId}/Tickets/TicketAddTaskTemplate?TicketID={ticketId}",
            UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps ? url : null;
    }

    /// <summary>
    /// True only when <paramref name="actual"/> is exactly the page that was
    /// asked for: same scheme, host, port and path, and the same single
    /// ticket. A sign-in page, an error page or another ticket's page is never
    /// read.
    /// </summary>
    public static bool IsPickerPage(Uri expected, string? actual)
    {
        if (!Uri.TryCreate(actual, UriKind.Absolute, out var url)) return false;
        if (!string.Equals(url.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(url.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
            || url.Port != expected.Port
            || !string.Equals(url.AbsolutePath, expected.AbsolutePath, StringComparison.OrdinalIgnoreCase))
            return false;
        return TicketIdOf(url) is { } id && id == TicketIdOf(expected);
    }

    private static int? TicketIdOf(Uri url)
    {
        int? found = null;
        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (!string.Equals(Uri.UnescapeDataString(parts[0]), "TicketID", StringComparison.OrdinalIgnoreCase)) continue;
            // Two TicketIDs are ambiguous, so neither is trusted.
            if (found != null) return null;
            if (parts.Length < 2 || !int.TryParse(Uri.UnescapeDataString(parts[1]), out var id) || id <= 0) return null;
            found = id;
        }
        return found;
    }

    /// <summary>
    /// The script run in the page. It only reads the options of the
    /// <c>#ddlTaskTemplate</c> select — value, text and whether each is
    /// disabled — and answers with a JSON string so nothing else of the page
    /// leaves it.
    /// </summary>
    public const string ReadOptionsScript = """
        (() => {
          const select = document.getElementById('ddlTaskTemplate');
          if (!select || select.tagName !== 'SELECT') return JSON.stringify({ found: false });
          return JSON.stringify({
            found: true,
            options: Array.from(select.options).map(o => ({ value: o.value, text: o.textContent, disabled: o.disabled }))
          });
        })()
        """;

    /// <summary>
    /// The templates in the script's answer, as <c>ExecuteScriptAsync</c>
    /// returns it (the script's string, JSON-encoded once more). Blank,
    /// disabled and non-positive options are dropped, and each ID is kept
    /// once. A page without the drop-down throws, so a sign-in or error page
    /// is never mistaken for a ticket with no templates to offer.
    /// </summary>
    public static IReadOnlyList<TaskTemplateChoice> ParseOptions(string? scriptResult)
    {
        PickerAnswer? answer;
        try
        {
            var inner = string.IsNullOrWhiteSpace(scriptResult) ? null : JsonSerializer.Deserialize<string>(scriptResult);
            answer = inner == null ? null : JsonSerializer.Deserialize<PickerAnswer>(inner);
        }
        catch (JsonException)
        {
            answer = null;
        }
        if (answer is not { Found: true, Options: { } options })
            throw new TdxTicketTaskException(
                "TeamDynamix did not show its task template list for this ticket. You may not have permission to add task templates, or the page has changed.");

        var seen = new HashSet<int>();
        var choices = new List<TaskTemplateChoice>();
        foreach (var option in options)
        {
            if (option.Disabled) continue;
            if (!int.TryParse(option.Value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0) continue;
            var name = string.Join(" ", (option.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (name.Length == 0 || !seen.Add(id)) continue;
            choices.Add(new TaskTemplateChoice(id, name));
        }
        return choices;
    }

    private sealed class PickerAnswer
    {
        [JsonPropertyName("found")]
        public bool Found { get; set; }

        [JsonPropertyName("options")]
        public List<PickerOption>? Options { get; set; }
    }

    private sealed class PickerOption
    {
        [JsonPropertyName("value")]
        public string? Value { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("disabled")]
        public bool Disabled { get; set; }
    }
}
