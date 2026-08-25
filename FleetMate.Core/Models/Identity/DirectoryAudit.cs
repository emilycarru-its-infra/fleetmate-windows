using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Identity;

/// <summary>
/// One entry from the Entra directory audit log.
///
/// This is the only record of who or what changed a directory object. Group
/// membership and lifecycle are increasingly driven by automation, so when a
/// group disappears the useful question is not whether it exists — that is a
/// plain lookup — but which service principal removed it and when.
/// </summary>
public class DirectoryAuditEvent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("activityDateTime")]
    public DateTime ActivityDateTime { get; set; }

    [JsonPropertyName("activityDisplayName")]
    public string ActivityDisplayName { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("operationType")]
    public string? OperationType { get; set; }

    [JsonPropertyName("result")]
    public string? Result { get; set; }

    [JsonPropertyName("resultReason")]
    public string? ResultReason { get; set; }

    [JsonPropertyName("loggedByService")]
    public string? LoggedByService { get; set; }

    [JsonPropertyName("initiatedBy")]
    public AuditInitiator? InitiatedBy { get; set; }

    [JsonPropertyName("targetResources")]
    public List<AuditTargetResource> TargetResources { get; set; } = new();

    /// <summary>
    /// Who performed the change, as a single printable string.
    ///
    /// An audit entry carries either a user or an application, never both, and
    /// automation shows up as the application. Callers that print "the actor"
    /// should not have to know which half of the union was populated.
    /// </summary>
    [JsonIgnore]
    public string Actor =>
        InitiatedBy?.User?.UserPrincipalName
        ?? InitiatedBy?.User?.DisplayName
        ?? InitiatedBy?.App?.DisplayName
        ?? InitiatedBy?.App?.ServicePrincipalName
        ?? "unknown";

    /// <summary>True when the actor was an application rather than a person.</summary>
    [JsonIgnore]
    public bool ActorIsApplication => InitiatedBy?.User is null && InitiatedBy?.App is not null;

    /// <summary>The changed objects, as a single printable string.</summary>
    [JsonIgnore]
    public string Targets =>
        TargetResources.Count == 0
            ? "-"
            : string.Join(", ", TargetResources.Select(t => t.DisplayName ?? t.Id ?? "-"));
}

public class AuditInitiator
{
    [JsonPropertyName("user")]
    public AuditUser? User { get; set; }

    [JsonPropertyName("app")]
    public AuditApp? App { get; set; }
}

public class AuditUser
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("userPrincipalName")]
    public string? UserPrincipalName { get; set; }
}

public class AuditApp
{
    [JsonPropertyName("appId")]
    public string? AppId { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("servicePrincipalId")]
    public string? ServicePrincipalId { get; set; }

    [JsonPropertyName("servicePrincipalName")]
    public string? ServicePrincipalName { get; set; }
}

public class AuditTargetResource
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public class DirectoryAuditListResponse
{
    [JsonPropertyName("value")]
    public List<DirectoryAuditEvent> Value { get; set; } = new();

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }
}
