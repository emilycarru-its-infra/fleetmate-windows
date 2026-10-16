using System.Text.Json;
using FleetMate.Core.Models.Tickets;

namespace FleetMate.Core.Services.Tickets;

/// <summary>
/// The workflow list behind Change Workflow. The documented TDWebApi has no
/// route that lists workflows; the list TeamDynamix itself offers is the
/// workflow drop-down of a ticket's Reassign Workflow page,
/// <c>/TDNext/Apps/{appId}/Tickets/TicketAssignWorkflow?TicketID={id}</c>,
/// which holds only the workflows that apply to that ticket. The page is read
/// in the operator's own signed-in web session and never submitted: the change
/// itself still goes through the API's PUT.
///
/// This class is the framework-free half: the page's address, the check that a
/// loaded document is that page, the read-only script and the reading of its
/// answer. The GUI owns the browser.
/// </summary>
public static class TdxWorkflowPicker
{
    /// <summary>The picker page for <paramref name="ticketId"/>, or null when no web address is configured.</summary>
    public static Uri? PageUrl(TdxConfig config, int ticketId)
    {
        var root = config.GetWebRootUrl();
        if (root == null || ticketId <= 0) return null;
        var appId = config.TicketingAppId ?? config.AppId;
        return Uri.TryCreate($"{root}/TDNext/Apps/{appId}/Tickets/TicketAssignWorkflow?TicketID={ticketId}",
            UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps ? url : null;
    }

    /// <summary>
    /// True only when <paramref name="actual"/> is exactly the picker page that
    /// was asked for: same scheme, host and port, same path, and the same
    /// ticket. A sign-in page, an error page or another ticket's picker is
    /// never read.
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
    /// The script run in the picker page. It only reads: the options of the
    /// <c>#ddlWorkflow</c> select, as value, text and whether each is disabled.
    /// It answers with a JSON string so nothing else of the page leaves it.
    /// </summary>
    public const string ReadOptionsScript = """
        (() => {
          const select = document.getElementById('ddlWorkflow');
          if (!select || select.tagName !== 'SELECT') return JSON.stringify({ found: false });
          return JSON.stringify({
            found: true,
            options: Array.from(select.options).map(o => ({ value: o.value, text: o.textContent, disabled: o.disabled }))
          });
        })()
        """;

    /// <summary>
    /// The workflows in the script's answer, as <c>ExecuteScriptAsync</c>
    /// returns it (the script's string, JSON-encoded once more). Blank,
    /// disabled and non-numeric options are dropped, and each ID is kept once.
    /// A page without the drop-down throws, so a sign-in or error page is
    /// never mistaken for a ticket with no workflows to offer.
    /// </summary>
    public static IReadOnlyList<TdxWorkflowChoice> ParseOptions(string? scriptResult)
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
            throw new TdxWorkflowException(
                "TeamDynamix did not show its workflow list for this ticket. You may not have permission to change its workflow, or the page has changed.");

        var seen = new HashSet<int>();
        var choices = new List<TdxWorkflowChoice>();
        foreach (var option in options)
        {
            if (option.Disabled) continue;
            if (!int.TryParse(option.Value?.Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0) continue;
            var name = string.Join(" ", (option.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (name.Length == 0 || !seen.Add(id)) continue;
            choices.Add(new TdxWorkflowChoice(id, name));
        }
        return choices;
    }

    private sealed class PickerAnswer
    {
        [System.Text.Json.Serialization.JsonPropertyName("found")]
        public bool Found { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("options")]
        public List<PickerOption>? Options { get; set; }
    }

    private sealed class PickerOption
    {
        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public string? Value { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("text")]
        public string? Text { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("disabled")]
        public bool Disabled { get; set; }
    }
}
