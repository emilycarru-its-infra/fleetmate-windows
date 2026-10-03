using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Tickets;

/// <summary>
/// A TeamDynamix person, as returned by <c>api/people/lookup</c>.
/// </summary>
public class TdxPerson
{
    [JsonPropertyName("UID")]
    public Guid? Uid { get; set; }

    [JsonPropertyName("FullName")]
    public string? FullName { get; set; }

    [JsonPropertyName("FirstName")]
    public string? FirstName { get; set; }

    [JsonPropertyName("LastName")]
    public string? LastName { get; set; }

    [JsonPropertyName("PrimaryEmail")]
    public string? PrimaryEmail { get; set; }

    /// <summary>Name for display, falling back to the email.</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(FullName) ? FullName!
        : string.Join(" ", new[] { FirstName, LastName }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } joined ? joined
        : PrimaryEmail ?? "(unknown)";
}
