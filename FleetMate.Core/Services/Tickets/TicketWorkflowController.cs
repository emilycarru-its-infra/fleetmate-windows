using FleetMate.Core.Models.Tickets;

namespace FleetMate.Core.Services.Tickets;

/// <summary>What the Workflow section shows for one ticket.</summary>
public enum TicketWorkflowPhase
{
    Loading,
    /// <summary>TeamDynamix answered null: the ticket has no workflow.</summary>
    None,
    Loaded,
    Failed,
}

/// <summary>An active step and what the signed-in user may do on it.</summary>
public sealed record TicketWorkflowStepView(
    TdxWorkflowStep Step,
    IReadOnlyList<TdxWorkflowStepAction> Actions,
    string? ActionsError)
{
    /// <summary>
    /// What to say when there is nothing to press: why the actions could not
    /// be read, or that the step has none for this user.
    /// </summary>
    public string? ActionsNote => ActionsError
        ?? (Actions.Count == 0 ? "No actions available to you on this step." : null);
}

/// <summary>One immutable reading of a ticket's workflow.</summary>
public sealed record TicketWorkflowSnapshot(
    int TicketId,
    long Generation,
    TicketWorkflowPhase Phase,
    TdxTicketWorkflow? Workflow,
    IReadOnlyList<TicketWorkflowStepView> ActiveSteps,
    string? Error);

/// <summary>How a workflow action ended, from the operator's point of view.</summary>
public enum TicketWorkflowSubmitStatus
{
    Succeeded,
    /// <summary>TeamDynamix answered and said no (IsSuccessful false), or the request failed.</summary>
    Failed,
    /// <summary>
    /// Not sent: another action is in flight, or the choice no longer matches
    /// the loaded workflow. Only this status leaves the loaded actions usable;
    /// after the other two the workflow has to be loaded again.
    /// </summary>
    NotSent,
}

public sealed record TicketWorkflowSubmitOutcome(
    TicketWorkflowSubmitStatus Status,
    int TicketId,
    string Message,
    TdxWorkflowActionResult? Result = null)
{
    public bool Succeeded => Status == TicketWorkflowSubmitStatus.Succeeded;
}

/// <summary>
/// Loads a ticket's workflow and performs step actions on it for the signed-in
/// user, with the guarding the UI needs: a stale load (the operator moved to
/// another ticket) is discarded, only one action is in flight at a time, and
/// an action is only sent when it is one TeamDynamix offered for a step that
/// is still active in the loaded workflow. Nothing is guessed — no action is
/// ever offered that the actions route did not return.
/// </summary>
public sealed class TicketWorkflowController
{
    private readonly ITicketWorkflowApi _api;
    private readonly object _gate = new();
    private long _generation;
    private CancellationTokenSource? _loadCts;
    private TicketWorkflowSnapshot? _current;
    private int _submitting;

    public TicketWorkflowController(ITicketWorkflowApi api) => _api = api;

    /// <summary>The newest snapshot for the ticket last asked for, or null.</summary>
    public TicketWorkflowSnapshot? Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>True while an action is being sent.</summary>
    public bool IsSubmitting => Volatile.Read(ref _submitting) != 0;

    /// <summary>True when <paramref name="snapshot"/> is still the newest load.</summary>
    public bool IsCurrent(TicketWorkflowSnapshot snapshot)
    {
        lock (_gate) return snapshot.Generation == _generation;
    }

    /// <summary>Forget the ticket and abandon any load in flight.</summary>
    public void Clear() => Invalidate();

    private void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _loadCts?.Cancel();
            _loadCts = null;
            _current = null;
        }
    }

    /// <summary>
    /// Read the workflow and, for every active step, the actions available to
    /// the signed-in user. Returns null when a newer load or a Clear overtook
    /// this one, so a slow answer for the previous ticket is never shown on
    /// the next.
    /// </summary>
    public async Task<TicketWorkflowSnapshot?> LoadAsync(int ticketId)
    {
        long generation;
        CancellationToken ct;
        lock (_gate)
        {
            generation = ++_generation;
            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            ct = _loadCts.Token;
            _current = new TicketWorkflowSnapshot(ticketId, generation, TicketWorkflowPhase.Loading, null,
                Array.Empty<TicketWorkflowStepView>(), null);
        }

        TicketWorkflowSnapshot snapshot;
        try
        {
            var workflow = await _api.GetTicketWorkflowAsync(ticketId, ct);
            if (workflow == null)
            {
                snapshot = new(ticketId, generation, TicketWorkflowPhase.None, null,
                    Array.Empty<TicketWorkflowStepView>(), null);
            }
            else
            {
                // Parallel branches each carry their own actions, so every
                // active step is asked about, concurrently, and one step's
                // failure does not hide the others.
                var steps = await Task.WhenAll(workflow.ActiveSteps().Select(step => LoadStepAsync(ticketId, step, ct)));
                snapshot = new(ticketId, generation, TicketWorkflowPhase.Loaded, workflow, steps, null);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            snapshot = new(ticketId, generation, TicketWorkflowPhase.Failed, null,
                Array.Empty<TicketWorkflowStepView>(), ex.Message);
        }

        lock (_gate)
        {
            if (generation != _generation) return null;
            _current = snapshot;
            return snapshot;
        }
    }

    private async Task<TicketWorkflowStepView> LoadStepAsync(int ticketId, TdxWorkflowStep step, CancellationToken ct)
    {
        try
        {
            var actions = await _api.GetWorkflowStepActionsAsync(ticketId, step.Id, ct);
            return new(step, actions, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TdxWorkflowException ex) when (ex.StatusCode == 403)
        {
            return new(step, Array.Empty<TdxWorkflowStepAction>(), "You do not have permission to act on this step.");
        }
        catch (Exception ex)
        {
            return new(step, Array.Empty<TdxWorkflowStepAction>(), $"Could not read this step's actions: {ex.Message}");
        }
    }

    /// <summary>
    /// Perform <paramref name="actionId"/> on <paramref name="stepId"/>. Sent
    /// only when the loaded snapshot for <paramref name="ticketId"/> offers
    /// exactly that action on that active step, and no other action is in
    /// flight. Success means TeamDynamix said <c>IsSuccessful</c>; a 200 alone
    /// is not success.
    /// </summary>
    public async Task<TicketWorkflowSubmitOutcome> SubmitAsync(
        int ticketId, string stepId, string actionId, string? comments)
    {
        if (Interlocked.CompareExchange(ref _submitting, 1, 0) != 0)
            return new(TicketWorkflowSubmitStatus.NotSent, ticketId, "Another workflow action is still being sent.");

        try
        {
            var snapshot = Current;
            if (snapshot is not { Phase: TicketWorkflowPhase.Loaded } || snapshot.TicketId != ticketId)
                return new(TicketWorkflowSubmitStatus.NotSent, ticketId,
                    "The workflow changed before the action was sent. Review it and try again.");

            var key = TdxTicketWorkflow.NormalizeStepId(stepId);
            var step = snapshot.ActiveSteps.FirstOrDefault(s => TdxTicketWorkflow.NormalizeStepId(s.Step.Id) == key);
            // Ordinal: action IDs are opaque and matched exactly as sent.
            var action = step?.Actions.FirstOrDefault(a => string.Equals(a.Id, actionId, StringComparison.Ordinal));
            if (step == null || action == null)
                return new(TicketWorkflowSubmitStatus.NotSent, ticketId,
                    "That action is no longer offered on this step. Refresh the workflow and try again.");

            var request = new TdxWorkflowApproveRequest
            {
                StepId = step.Step.Id,
                ActionId = action.Id,
                Comments = string.IsNullOrWhiteSpace(comments) ? null : comments.Trim(),
            };

            // Whatever happens next, the loaded actions are spent. A timeout
            // or refusal can still follow a change TeamDynamix committed, so
            // the old choice must not be sendable again: a retry needs a fresh
            // load, fresh actions and a fresh confirmation.
            Invalidate();

            TdxWorkflowActionResult result;
            try
            {
                // Not cancelled by a selection change: once sent, the action
                // happens or not regardless of what the operator looks at next.
                result = await _api.PerformWorkflowActionAsync(ticketId, request);
            }
            catch (Exception ex)
            {
                return new(TicketWorkflowSubmitStatus.Failed, ticketId, ex.Message);
            }

            return result.IsSuccessful
                ? new(TicketWorkflowSubmitStatus.Succeeded, ticketId,
                    string.IsNullOrWhiteSpace(result.Message)
                        ? $"{action.DisplayName} done on {step.Step.DisplayName}."
                        : result.Message!.Trim(),
                    result)
                : new(TicketWorkflowSubmitStatus.Failed, ticketId, result.FailureSummary(), result);
        }
        finally
        {
            Volatile.Write(ref _submitting, 0);
        }
    }
}
