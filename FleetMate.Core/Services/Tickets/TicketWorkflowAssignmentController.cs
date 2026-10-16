using FleetMate.Core.Models.Tickets;

namespace FleetMate.Core.Services.Tickets;

/// <summary>What a workflow change does to the ticket.</summary>
public enum TicketWorkflowChangeKind
{
    /// <summary>Put the ticket's current workflow on it again, from the first step.</summary>
    Restart,
    /// <summary>Replace the current workflow with a different one.</summary>
    Replace,
    /// <summary>Give a ticket that has no workflow its first one.</summary>
    Assign,
}

/// <summary>
/// The ticket's workflow as the operator saw it when choosing, reduced to what
/// says whether it is still the same one in the same place: the workflow, its
/// state and its active steps. Any difference means someone else moved it, and
/// the choice has to be made again. A restart of the same definition is seen
/// too: it starts a new instance, with its own ID and start time.
/// </summary>
public sealed record TicketWorkflowExpectation(
    int? InstanceId, int? ConfigurationId, string? WorkflowName, string Fingerprint)
{
    public bool HasWorkflow => InstanceId != null;

    public static TicketWorkflowExpectation From(TdxTicketWorkflow? workflow) =>
        new(workflow?.Id, workflow?.WorkflowConfigurationId, workflow == null ? null : DisplayName(workflow),
            FingerprintOf(workflow));

    public bool Matches(TdxTicketWorkflow? live) => string.Equals(Fingerprint, FingerprintOf(live), StringComparison.Ordinal);

    public static string DisplayName(TdxTicketWorkflow workflow) =>
        string.IsNullOrWhiteSpace(workflow.Name) ? "Unnamed workflow" : workflow.Name!.Trim();

    private static string FingerprintOf(TdxTicketWorkflow? workflow)
    {
        if (workflow == null) return "none";
        var steps = (workflow.CurrentStepIds ?? new())
            .Concat((workflow.Steps ?? new()).Where(s => s.IsCurrent).Select(s => s.Id))
            .Select(TdxTicketWorkflow.NormalizeStepId)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal);
        var started = workflow.StartDateUtc?.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        return $"{workflow.Id}|{workflow.WorkflowConfigurationId}|{started}|{workflow.IsComplete}|{workflow.Status}|{string.Join(",", steps)}";
    }
}

/// <summary>
/// One workflow change the operator has chosen but not yet confirmed: which
/// ticket, the workflow it had when they looked, and the one to put on it.
/// </summary>
public sealed record TicketWorkflowChangeRequest(
    int TicketId,
    TicketWorkflowChangeKind Kind,
    TicketWorkflowExpectation Expected,
    int TargetWorkflowId,
    string TargetName)
{
    /// <summary>
    /// True for a restart or a replacement. A first assignment sends false, so
    /// TeamDynamix refuses it rather than overwrite a workflow someone else
    /// put on the ticket after it was read.
    /// </summary>
    public bool AllowRemoveExisting => Kind != TicketWorkflowChangeKind.Assign;

    public string Title => Kind switch
    {
        TicketWorkflowChangeKind.Restart => "Restart Workflow",
        TicketWorkflowChangeKind.Assign => "Assign Workflow",
        _ => "Change Workflow",
    };

    /// <summary>
    /// What the confirmation says: the ticket, the current workflow, the
    /// target, and — whenever a workflow is removed — what that costs.
    /// </summary>
    public string ConfirmationText()
    {
        var current = Expected.WorkflowName ?? "none";
        var lead = Kind switch
        {
            TicketWorkflowChangeKind.Restart =>
                $"Restart the workflow “{TargetName}” on ticket #{TicketId}?",
            TicketWorkflowChangeKind.Assign =>
                $"Assign the workflow “{TargetName}” to ticket #{TicketId}?",
            _ => $"Replace the workflow on ticket #{TicketId} with “{TargetName}”?",
        };
        var lines = new List<string>
        {
            lead,
            "",
            $"Ticket: #{TicketId}",
            $"Current workflow: {current}",
            $"New workflow: {TargetName}",
        };
        if (AllowRemoveExisting)
        {
            lines.Add("");
            lines.Add("The current workflow is removed and the new one starts from its first step. "
                + "Its progress, including any approvals already given, is reset, and its notifications, "
                + "tasks and automations may run again.");
        }
        return string.Join("\n", lines);
    }

    /// <summary>A restart of the workflow loaded for the ticket, or the reason there can be none.</summary>
    public static (TicketWorkflowChangeRequest? Request, string? Reason) Restart(int ticketId, TdxTicketWorkflow? current)
    {
        if (current == null) return (null, $"Ticket #{ticketId} has no workflow to restart.");
        // TicketWorkflow.ID is the running instance; the PUT takes the
        // definition, WorkflowConfigurationID. Sending the instance ID would
        // put an unrelated workflow on the ticket, so without the definition
        // there is no restart.
        if (current.WorkflowConfigurationId <= 0)
            return (null, "TeamDynamix did not say which workflow definition this is, so it cannot be restarted.");
        return (new(ticketId, TicketWorkflowChangeKind.Restart, TicketWorkflowExpectation.From(current),
            current.WorkflowConfigurationId, TicketWorkflowExpectation.DisplayName(current)), null);
    }

    /// <summary>
    /// Replace the loaded workflow with <paramref name="target"/>, or assign it
    /// when there is none; or the reason it cannot be done.
    /// </summary>
    public static (TicketWorkflowChangeRequest? Request, string? Reason) Change(
        int ticketId, TdxTicketWorkflow? current, TdxWorkflowChoice target)
    {
        if (target.Id <= 0) return (null, "That workflow has no ID TeamDynamix can use.");
        if (current != null && current.WorkflowConfigurationId == target.Id)
            return (null, $"“{target.DisplayName}” is already the workflow on ticket #{ticketId}. Use Restart Workflow to start it over.");
        var kind = current == null ? TicketWorkflowChangeKind.Assign : TicketWorkflowChangeKind.Replace;
        return (new(ticketId, kind, TicketWorkflowExpectation.From(current), target.Id, target.DisplayName), null);
    }
}

/// <summary>How a workflow change ended, from the operator's point of view.</summary>
public enum TicketWorkflowChangeStatus
{
    Succeeded,
    /// <summary>TeamDynamix answered with an error; nothing should have changed.</summary>
    Failed,
    /// <summary>The request may or may not have been applied: a timeout, a dropped connection, an unreadable or mismatched answer.</summary>
    Uncertain,
    /// <summary>Nothing was sent: another change is in flight, or the workflow or the choices moved since the operator chose.</summary>
    NotSent,
}

public sealed record TicketWorkflowChangeOutcome(
    TicketWorkflowChangeStatus Status,
    int TicketId,
    string Message,
    TdxTicket? Ticket = null,
    bool WorkflowChanged = false)
{
    public bool Succeeded => Status == TicketWorkflowChangeStatus.Succeeded;

    /// <summary>
    /// Whether the ticket, its feed and its workflow must be read again.
    /// Always after a request was sent — a failure is no proof nothing
    /// changed — and when the workflow was found to have moved.
    /// </summary>
    public bool RequiresReload => Status != TicketWorkflowChangeStatus.NotSent || WorkflowChanged;
}

/// <summary>
/// Restarts, replaces or assigns a ticket's workflow for the signed-in user.
/// Before anything is sent, the ticket's workflow is read again and must
/// still be the one the operator chose against; a change also needs its
/// target to still be among the workflows the catalog offers. One change is
/// in flight at a time, and none is ever retried.
/// </summary>
public sealed class TicketWorkflowAssignmentController
{
    private readonly ITicketWorkflowAssignmentApi _api;
    private readonly ITicketWorkflowCatalog? _catalog;
    private int _submitting;

    public TicketWorkflowAssignmentController(ITicketWorkflowAssignmentApi api, ITicketWorkflowCatalog? catalog = null)
    {
        _api = api;
        _catalog = catalog;
    }

    /// <summary>False when there is no workflow list, so Change Workflow cannot be offered.</summary>
    public bool CanChange => _catalog != null;

    /// <summary>True while a change is being checked or sent.</summary>
    public bool IsSubmitting => Volatile.Read(ref _submitting) != 0;

    /// <summary>
    /// The workflows that could replace <paramref name="current"/> on the
    /// ticket: everything the catalog offers except the current definition,
    /// which is Restart's job. Read fresh on every call. Throws when there is
    /// no catalog or it could not be read.
    /// </summary>
    public async Task<IReadOnlyList<TdxWorkflowChoice>> GetChoicesAsync(
        int ticketId, TdxTicketWorkflow? current, CancellationToken ct = default)
    {
        if (_catalog == null)
            throw new TdxWorkflowException("No list of workflows is available, so the workflow cannot be changed here.");
        var all = await _catalog.GetAssignableWorkflowsAsync(ticketId, ct);
        return all
            .Where(c => c.Id > 0 && c.Id != current?.WorkflowConfigurationId)
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// Check <paramref name="request"/> against the ticket as it is now and,
    /// only if nothing moved, send it once.
    /// </summary>
    public async Task<TicketWorkflowChangeOutcome> SubmitAsync(TicketWorkflowChangeRequest request)
    {
        var ticketId = request.TicketId;
        if (Interlocked.CompareExchange(ref _submitting, 1, 0) != 0)
            return new(TicketWorkflowChangeStatus.NotSent, ticketId, "Another workflow change is still being sent.");

        try
        {
            if (Invalid(request) is { } invalid)
                return new(TicketWorkflowChangeStatus.NotSent, ticketId, invalid);

            // The authoritative current workflow, read now: the screen may be
            // minutes old, and a restart or replacement throws away whatever
            // is on the ticket at the moment it lands.
            TdxTicketWorkflow? live;
            try
            {
                live = await _api.GetTicketWorkflowAsync(ticketId);
            }
            catch (Exception ex)
            {
                return new(TicketWorkflowChangeStatus.NotSent, ticketId,
                    $"Could not confirm the current workflow, so nothing was changed. {ex.Message}");
            }

            if (!request.Expected.Matches(live))
                return new(TicketWorkflowChangeStatus.NotSent, ticketId,
                    $"The workflow on ticket #{ticketId} changed since you looked at it, so nothing was sent. "
                    + "Review it and choose again.", WorkflowChanged: true);

            if (request.Kind == TicketWorkflowChangeKind.Restart
                && (live == null || live.WorkflowConfigurationId != request.TargetWorkflowId))
                return new(TicketWorkflowChangeStatus.NotSent, ticketId,
                    $"Ticket #{ticketId} no longer has that workflow to restart.", WorkflowChanged: true);

            if (request.Kind != TicketWorkflowChangeKind.Restart && await RevalidateTargetAsync(request, live) is { } stale)
                return new(TicketWorkflowChangeStatus.NotSent, ticketId, stale);

            TdxTicket? ticket;
            try
            {
                // Not cancellable: once sent, the change happens or not
                // whatever the operator looks at next.
                ticket = await _api.AssignTicketWorkflowAsync(ticketId, request.TargetWorkflowId, request.AllowRemoveExisting);
            }
            catch (TdxWorkflowException ex) when (ex.OutcomeUnknown)
            {
                return new(TicketWorkflowChangeStatus.Uncertain, ticketId, Uncertain(ex.Message));
            }
            catch (TdxWorkflowException ex)
            {
                return new(TicketWorkflowChangeStatus.Failed, ticketId, ex.Message);
            }
            catch (Exception)
            {
                return new(TicketWorkflowChangeStatus.Uncertain, ticketId,
                    Uncertain($"Something went wrong while changing the workflow of ticket #{ticketId}."));
            }

            // The answer is the ticket that changed. Anything else — nothing,
            // or another ticket — leaves it unknown what TeamDynamix did.
            if (ticket == null || ticket.Id != ticketId)
                return new(TicketWorkflowChangeStatus.Uncertain, ticketId,
                    Uncertain($"TeamDynamix's answer did not describe ticket #{ticketId}."));

            return new(TicketWorkflowChangeStatus.Succeeded, ticketId, request.Kind switch
            {
                TicketWorkflowChangeKind.Restart => $"Restarted “{request.TargetName}” on ticket #{ticketId}.",
                TicketWorkflowChangeKind.Assign => $"Assigned “{request.TargetName}” to ticket #{ticketId}.",
                _ => $"Changed the workflow on ticket #{ticketId} to “{request.TargetName}”.",
            }, ticket);
        }
        finally
        {
            Volatile.Write(ref _submitting, 0);
        }
    }

    private static string? Invalid(TicketWorkflowChangeRequest request)
    {
        if (request.TicketId <= 0 || request.TargetWorkflowId <= 0) return "That workflow change is incomplete.";
        return request.Kind switch
        {
            TicketWorkflowChangeKind.Restart when request.Expected.ConfigurationId != request.TargetWorkflowId =>
                "A restart has to name the workflow already on the ticket.",
            TicketWorkflowChangeKind.Replace when !request.Expected.HasWorkflow =>
                "There is no workflow to replace.",
            TicketWorkflowChangeKind.Replace when request.Expected.ConfigurationId == request.TargetWorkflowId =>
                "That is already the ticket's workflow. Use Restart Workflow to start it over.",
            TicketWorkflowChangeKind.Assign when request.Expected.HasWorkflow =>
                "The ticket already has a workflow, so it would be replaced, not assigned.",
            _ => null,
        };
    }

    /// <summary>The target must still be offered for this ticket; null when it is.</summary>
    private async Task<string?> RevalidateTargetAsync(TicketWorkflowChangeRequest request, TdxTicketWorkflow? live)
    {
        IReadOnlyList<TdxWorkflowChoice> choices;
        try
        {
            choices = await GetChoicesAsync(request.TicketId, live);
        }
        catch (Exception ex)
        {
            return $"Could not confirm that “{request.TargetName}” can still be assigned, so nothing was changed. {ex.Message}";
        }
        return choices.Any(c => c.Id == request.TargetWorkflowId)
            ? null
            : $"“{request.TargetName}” is no longer offered for ticket #{request.TicketId}, so nothing was changed. Choose again.";
    }

    private static string Uncertain(string reason) =>
        $"{reason} The change may or may not have been made; the ticket is being read again. Check it before trying again.";
}
