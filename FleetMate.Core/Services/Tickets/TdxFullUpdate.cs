using System.Text.Json;

namespace FleetMate.Core.Services.Tickets;

/// <summary>
/// The body for TDX's full ticket update, <c>POST /api/{appId}/tickets/{id}</c>.
///
/// That endpoint replaces the ticket: a field left out of the body is cleared.
/// So the body echoes every editable field from the ticket as fetched, with
/// only the overrides changed. Used where a sparse PATCH is not enough, such as
/// setting the parent. FleetMate for Mac sends the same field set.
/// </summary>
public static class TdxFullUpdate
{
    internal static readonly string[] Fields =
    {
        "TypeID", "Classification", "Title", "Description", "AccountID", "SourceID",
        "StatusID", "PriorityID", "UrgencyID", "ImpactID", "FormID", "ServiceID",
        "ServiceOfferingID", "RequestorUid", "ResponsibleUid", "ResponsibleGroupID",
        "ParentID", "LocationID", "LocationRoomID",
    };

    public static Dictionary<string, object?> Build(JsonElement ticket, IDictionary<string, object?>? overrides = null)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            if (ticket.ValueKind == JsonValueKind.Object && TryGetCaseInsensitive(ticket, field, out var value))
                body[field] = value.ValueKind == JsonValueKind.Null ? null : value.Clone();
        }

        // TDX descriptions are HTML; echoing one back without this flattens it.
        body["IsRichHtml"] = true;

        if (overrides != null)
        {
            foreach (var (key, value) in overrides)
                body[key] = value;
        }
        return body;
    }

    private static bool TryGetCaseInsensitive(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
