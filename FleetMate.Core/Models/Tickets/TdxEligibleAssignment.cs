using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Tickets;

/// <summary>
/// A person or group the ticketing application allows as a ticket's
/// responsible: what <c>GET /api/{appId}/tickets/resources</c> returns, and the
/// same set the TDX web UI's Responsible picker offers.
/// </summary>
public sealed class TdxEligibleAssignment
{
    /// <summary>A person's UID, or a group's numeric ID as a string.</summary>
    [JsonPropertyName("Value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Email")]
    public string? Email { get; set; }

    [JsonPropertyName("IsUser")]
    public bool IsUser { get; set; } = true;

    public string Key => $"{(IsUser ? "user" : "group")}:{Value}";

    /// <summary>The person's UID, when this is a person.</summary>
    public Guid? Uid => IsUser && Guid.TryParse(Value, out var uid) && uid != Guid.Empty ? uid : null;

    /// <summary>The group ID, when this is a group.</summary>
    public int? GroupId => !IsUser && int.TryParse(Value, out var id) ? id : null;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Value : Name.Trim();

    /// <summary>The email for a person, "Group" for a group.</summary>
    public string Subtitle => IsUser ? Email ?? "" : "Group";

    /// <summary>
    /// Whether this candidate is already the ticket's responsible: the same
    /// person, or the same group with no person set.
    /// </summary>
    public bool IsCurrent(Guid? responsibleUid, int? responsibleGroupId) => IsUser
        ? Uid is { } uid && uid == responsibleUid
        : GroupId is { } gid && gid == responsibleGroupId && (responsibleUid is null || responsibleUid == Guid.Empty);

    /// <summary>
    /// The ticket fields that make this candidate responsible. A group takes
    /// the ticket on its own, as TDX's picker does: the group becomes
    /// responsible and the responsible person is cleared. Null when the value
    /// is neither a UID nor a group ID.
    /// </summary>
    public Dictionary<string, object?>? ResponsibleUpdates() => Uid is { } uid
        ? new Dictionary<string, object?> { ["ResponsibleUid"] = uid.ToString() }
        : GroupId is { } gid
            ? new Dictionary<string, object?> { ["ResponsibleGroupID"] = gid, ["ResponsibleUid"] = "" }
            : null;

    /// <summary>People first, then groups, each in the order TDX ranked them.</summary>
    public static List<TdxEligibleAssignment> Ordered(IEnumerable<TdxEligibleAssignment> found)
    {
        var list = found.ToList();
        return list.Where(a => a.IsUser).Concat(list.Where(a => !a.IsUser)).ToList();
    }
}
