using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Tickets;

/// <summary>
/// The workflow attached to a ticket, from
/// <c>GET /api/{appId}/tickets/{id}/workflow</c>. TeamDynamix answers a ticket
/// with no workflow with a JSON <c>null</c> body, not a 404.
/// </summary>
public sealed class TdxTicketWorkflow
{
    /// <summary>
    /// The running workflow instance on this ticket. Not the workflow that
    /// can be assigned again — that is <see cref="WorkflowConfigurationId"/>.
    /// </summary>
    [JsonPropertyName("ID")]
    public int Id { get; set; }

    /// <summary>
    /// The workflow definition the instance was started from: the ID the
    /// assignment PUT takes as <c>newWorkflowId</c>, and the one a restart
    /// sends. Zero when TeamDynamix did not say.
    /// </summary>
    [JsonPropertyName("WorkflowConfigurationID")]
    public int WorkflowConfigurationId { get; set; }

    /// <summary>When this instance started; a restart gives the ticket a new one.</summary>
    [JsonPropertyName("StartDateUtc")]
    public DateTime? StartDateUtc { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Description")]
    public string? Description { get; set; }

    [JsonPropertyName("IsComplete")]
    public bool IsComplete { get; set; }

    /// <summary>0 none / in process, 1 approved, 2 rejected.</summary>
    [JsonPropertyName("Status")]
    public int Status { get; set; }

    /// <summary>
    /// Every step active right now. A workflow with parallel branches has more
    /// than one, and each is acted on separately.
    /// </summary>
    [JsonPropertyName("CurrentStepIDs")]
    public List<string>? CurrentStepIds { get; set; }

    [JsonPropertyName("Steps")]
    public List<TdxWorkflowStep>? Steps { get; set; }

    /// <summary>What the workflow's state reads as in one word.</summary>
    [JsonIgnore]
    public string StatusLabel => Status switch
    {
        1 => "Approved",
        2 => "Rejected",
        _ => IsComplete ? "Complete" : "In process",
    };

    /// <summary>
    /// The active steps, in the workflow's own step order. A step counts as
    /// active when <c>CurrentStepIDs</c> names it or it says <c>IsCurrent</c>;
    /// the two are read together because either alone has been seen empty
    /// while the other was filled. Step IDs are GUIDs and compared as such, so
    /// a difference in case or braces does not hide a step.
    /// </summary>
    public IReadOnlyList<TdxWorkflowStep> ActiveSteps()
    {
        if (IsComplete) return Array.Empty<TdxWorkflowStep>();

        var current = new HashSet<string>(
            (CurrentStepIds ?? new()).Select(NormalizeStepId).Where(id => id.Length > 0),
            StringComparer.Ordinal);
        var steps = Steps ?? new();

        var active = steps
            .Where(s => s.IsCurrent || current.Contains(NormalizeStepId(s.Id)))
            .ToList();

        // A current ID with no matching step still has to be shown: it is a
        // step the operator may be able to act on, and dropping it would hide
        // the reason the ticket is waiting.
        var known = new HashSet<string>(steps.Select(s => NormalizeStepId(s.Id)), StringComparer.Ordinal);
        foreach (var id in CurrentStepIds ?? new())
        {
            if (NormalizeStepId(id) is { Length: > 0 } key && known.Add(key))
                active.Add(new TdxWorkflowStep { Id = id, IsCurrent = true });
        }

        return active;
    }

    /// <summary>A step ID in one comparable form: a GUID's "D" format when it parses, else trimmed.</summary>
    public static string NormalizeStepId(string? id) =>
        Guid.TryParse(id, out var guid) ? guid.ToString("D") : (id ?? "").Trim();
}

/// <summary>One step of a <see cref="TdxTicketWorkflow"/>.</summary>
public sealed class TdxWorkflowStep
{
    /// <summary>The step's GUID, kept as TeamDynamix sent it.</summary>
    [JsonPropertyName("ID")]
    public string Id { get; set; } = "";

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Description")]
    public string? Description { get; set; }

    [JsonPropertyName("TypeName")]
    public string? TypeName { get; set; }

    [JsonPropertyName("TypeDescription")]
    public string? TypeDescription { get; set; }

    [JsonPropertyName("IsCurrent")]
    public bool IsCurrent { get; set; }

    /// <summary>The step failed and needs someone to skip or retry it.</summary>
    [JsonPropertyName("IsInError")]
    public bool IsInError { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Unnamed step" : Name!;
}

/// <summary>
/// An action the signed-in user may take on a step, from
/// <c>GET /api/{appId}/tickets/{id}/workflow/actions?stepId=</c>. The ID is
/// opaque and goes back to TeamDynamix exactly as it came.
/// </summary>
public sealed class TdxWorkflowStepAction
{
    [JsonPropertyName("ID")]
    public string Id { get; set; } = "";

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Tooltip")]
    public string? Tooltip { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name!;
}

/// <summary>The body of <c>POST /api/{appId}/tickets/{id}/workflow/approve</c>.</summary>
public sealed class TdxWorkflowApproveRequest
{
    [JsonPropertyName("StepID")]
    public string StepId { get; set; } = "";

    [JsonPropertyName("ActionID")]
    public string ActionId { get; set; } = "";

    [JsonPropertyName("Comments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Comments { get; set; }
}

/// <summary>
/// What TeamDynamix made of a step action. It answers 200 for a rejected
/// action too, so <see cref="IsSuccessful"/> — not the status code — is what
/// says whether anything happened.
/// </summary>
public sealed class TdxWorkflowActionResult
{
    [JsonPropertyName("IsSuccessful")]
    public bool IsSuccessful { get; set; }

    [JsonPropertyName("Message")]
    public string? Message { get; set; }

    [JsonPropertyName("Errors")]
    public List<TdxWorkflowValidationMessage>? Errors { get; set; }

    [JsonPropertyName("WasWorkflowUpdated")]
    public bool WasWorkflowUpdated { get; set; }

    /// <summary>
    /// The reason an unsuccessful action gives, as one line: the message and
    /// every validation error, without repeats. Never empty, because "it
    /// failed" with no words is worse than a generic sentence.
    /// </summary>
    public string FailureSummary()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Message)) parts.Add(Message!.Trim());
        foreach (var error in Errors ?? new())
        {
            if (!string.IsNullOrWhiteSpace(error.Message) && !parts.Contains(error.Message!.Trim()))
                parts.Add(error.Message!.Trim());
        }
        return parts.Count == 0
            ? "TeamDynamix did not accept the action and gave no reason."
            : string.Join(" ", parts);
    }
}

/// <summary>A validation error on a workflow action, with the steps it concerns.</summary>
public sealed class TdxWorkflowValidationMessage
{
    [JsonPropertyName("Message")]
    public string? Message { get; set; }

    [JsonPropertyName("MemberNames")]
    public List<string>? MemberNames { get; set; }

    [JsonPropertyName("StepIDs")]
    public List<string>? StepIds { get; set; }
}

/// <summary>
/// A workflow request TeamDynamix refused or never answered. Thrown rather
/// than returned as an empty result, because an empty action list or a
/// missing workflow are real answers and must not be confused with a failure.
/// </summary>
public sealed class TdxWorkflowException : Exception
{
    /// <summary>The HTTP status, or null when the request never got one.</summary>
    public int? StatusCode { get; }

    public bool IsForbidden => StatusCode is 401 or 403;

    /// <summary>
    /// The request may have reached TeamDynamix and been applied: it timed
    /// out, the connection dropped, or the answer could not be read. Never
    /// true for a request that was refused or never sent.
    /// </summary>
    public bool OutcomeUnknown { get; }

    public TdxWorkflowException(string message, int? statusCode = null, Exception? inner = null, bool outcomeUnknown = false)
        : base(message, inner)
    {
        StatusCode = statusCode;
        OutcomeUnknown = outcomeUnknown;
    }
}

/// <summary>
/// The workflow calls the Tickets tab makes, separated from
/// <see cref="Services.Tickets.TdxService"/> so the panel's ordering and
/// guarding can be tested without a server.
/// </summary>
public interface ITicketWorkflowApi
{
    /// <summary>The ticket's workflow, or null when it has none.</summary>
    Task<TdxTicketWorkflow?> GetTicketWorkflowAsync(int ticketId, CancellationToken ct = default);

    /// <summary>The actions the signed-in user may take on one step; empty when there are none.</summary>
    Task<IReadOnlyList<TdxWorkflowStepAction>> GetWorkflowStepActionsAsync(int ticketId, string stepId, CancellationToken ct = default);

    /// <summary>Perform an action. The result's IsSuccessful decides whether it happened.</summary>
    Task<TdxWorkflowActionResult> PerformWorkflowActionAsync(int ticketId, TdxWorkflowApproveRequest request, CancellationToken ct = default);
}

/// <summary>
/// A workflow that can be put on a ticket, as the workflow list offers it.
/// The ID is the workflow definition's, the same one
/// <see cref="TdxTicketWorkflow.WorkflowConfigurationId"/> carries for the
/// workflow on a ticket — never the running instance's ID.
/// </summary>
public sealed record TdxWorkflowChoice(int Id, string Name, string? Description = null)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Workflow {Id}" : Name.Trim();
}

/// <summary>
/// The workflows the signed-in user may put on a ticket. The documented
/// TDWebApi has no route that lists them; the list comes from TeamDynamix's
/// own Reassign Workflow page (<see cref="TdxWorkflowPicker"/>), read fresh
/// every time. Without a catalog Change Workflow is not offered, and nothing
/// stands in for it — no built-in list and no typed-in ID.
/// </summary>
public interface ITicketWorkflowCatalog
{
    /// <summary>The workflows that can be assigned to <paramref name="ticketId"/>, in display order.</summary>
    Task<IReadOnlyList<TdxWorkflowChoice>> GetAssignableWorkflowsAsync(int ticketId, CancellationToken ct = default);
}

/// <summary>
/// Restarting, replacing or assigning a ticket's workflow, through
/// <c>PUT /api/{appId}/tickets/{id}/workflow?newWorkflowId=&amp;allowRemoveExisting=</c>.
/// </summary>
public interface ITicketWorkflowAssignmentApi
{
    /// <summary>The ticket's workflow, or null when it has none.</summary>
    Task<TdxTicketWorkflow?> GetTicketWorkflowAsync(int ticketId, CancellationToken ct = default);

    /// <summary>
    /// Put workflow <paramref name="newWorkflowId"/> on the ticket. With
    /// <paramref name="allowRemoveExisting"/> false TeamDynamix refuses when
    /// the ticket already has one, so a first assignment never overwrites a
    /// workflow someone else added meanwhile. Returns the updated ticket, or
    /// null when the answer had none.
    /// </summary>
    Task<TdxTicket?> AssignTicketWorkflowAsync(int ticketId, int newWorkflowId, bool allowRemoveExisting, CancellationToken ct = default);
}
