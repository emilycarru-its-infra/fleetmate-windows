using System.Text;

namespace FleetMate.Core.Models.Tickets;

/// <summary>
/// The ticket fields the detail pane edits in place, as FleetMate for Mac
/// does: each change is one sparse PATCH of the field TeamDynamix names, sent
/// only when the value really differs from the ticket as loaded.
/// </summary>
public static class TicketDetailFields
{
    public const string Status = "StatusID";
    public const string Priority = "PriorityID";
    public const string Classification = "Classification";
    public const string Form = "FormID";
    public const string Service = "ServiceID";
    public const string Type = "TypeID";
    public const string Group = "ResponsibleGroupID";
    public const string Requestor = "RequestorUid";
    public const string Description = "Description";

    /// <summary>
    /// The responsible person. Not a picker field: Assign to me, Reassign and
    /// Unassign set it, and clearing it is a real change.
    /// </summary>
    public const string Responsible = "ResponsibleUid";

    /// <summary>The single-value fields a picker in the detail pane changes.</summary>
    public static readonly IReadOnlyList<string> Pickable = new[]
    {
        Status, Priority, Classification, Form, Service, Type, Group, Requestor,
    };

    /// <summary>The field's name in a sentence: "Changed the {label} of #12".</summary>
    public static string Label(string field) => field switch
    {
        Status => "status",
        Priority => "priority",
        Classification => "classification",
        Form => "form",
        Service => "service",
        Type => "type",
        Group => "group",
        Requestor => "requestor",
        Description => "description",
        Responsible => "responsible",
        _ => field,
    };

    /// <summary>The ticket's current value of a pickable field.</summary>
    public static object? Current(TdxTicket ticket, string field) => field switch
    {
        Status => ticket.StatusId,
        Priority => ticket.PriorityId,
        Classification => ticket.Classification,
        Form => ticket.FormId,
        Service => ticket.ServiceId,
        Type => ticket.TypeId,
        Group => ticket.ResponsibleGroupId,
        Requestor => ticket.RequestorUid,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not a pickable ticket field"),
    };

    /// <summary>
    /// The PATCH for setting <paramref name="field"/> to <paramref name="value"/>,
    /// or null when the ticket already holds that value. An empty id or uid is
    /// never sent: no picker offers one, so it can only be a blank selection.
    /// </summary>
    public static Dictionary<string, object?>? Change(TdxTicket? ticket, string field, object? value)
    {
        if (!Pickable.Contains(field)) throw new ArgumentOutOfRangeException(nameof(field), field, "Not a pickable ticket field");
        switch (value)
        {
            case int id when id <= 0:
            case Guid uid when uid == Guid.Empty:
            case null:
                return null;
        }
        if (ticket != null && Equals(Current(ticket, field), value)) return null;
        return new Dictionary<string, object?> { [field] = value };
    }

    /// <summary>
    /// The PATCH for a description typed as plain text, or null when it reads
    /// the same as <paramref name="currentPlainText"/>. TeamDynamix stores the
    /// description as HTML, so each line becomes a paragraph, with the HTML
    /// characters escaped so typed text is never read as markup.
    /// </summary>
    public static Dictionary<string, object?>? DescriptionChange(string? currentPlainText, string? editedPlainText)
    {
        var edited = Normalize(editedPlainText);
        if (edited == Normalize(currentPlainText)) return null;
        return new Dictionary<string, object?>
        {
            [Description] = DescriptionHtml(edited),
            ["IsRichHtml"] = true,
        };
    }

    /// <summary>Plain text as TeamDynamix description HTML: one paragraph per line.</summary>
    public static string DescriptionHtml(string? plainText)
    {
        var text = Normalize(plainText);
        if (text.Length == 0) return "";
        var html = new StringBuilder();
        foreach (var line in text.Split('\n'))
            html.Append("<p>").Append(EscapeHtml(line)).Append("</p>");
        return html.ToString();
    }

    /// <summary>
    /// Picker choices sorted by name, with the ticket's own value kept as a
    /// choice when the list does not offer it, so the picker never reads blank.
    /// </summary>
    public static List<KeyValuePair<int, string>> Choices(
        IReadOnlyDictionary<int, string> options, int? currentId, string? currentName, string fallbackLabel)
    {
        var all = new Dictionary<int, string>(options);
        if (currentId is int id && id > 0 && !all.ContainsKey(id))
            all[id] = string.IsNullOrWhiteSpace(currentName) ? $"{fallbackLabel} {id}" : currentName!;
        return all.OrderBy(kv => kv.Value, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string Normalize(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    // Only the characters that change how HTML parses. Accented letters stay
    // as they are, because the detail pane decodes named entities only.
    private static string EscapeHtml(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
