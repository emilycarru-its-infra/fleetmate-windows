using FleetMate.Core.Models.Tickets;

namespace FleetMate.Core.Services.Tickets;

public enum TicketTasksPhase
{
    Loading,
    Loaded,
    Failed,
}

/// <summary>One immutable reading of a ticket's tasks.</summary>
public sealed record TicketTasksSnapshot(
    int TicketId,
    long Generation,
    TicketTasksPhase Phase,
    IReadOnlyList<TdxTicketTask> Tasks,
    string? Error)
{
    public TdxTicketTask? Find(int taskId) => Tasks.FirstOrDefault(t => t.Id == taskId);
}

/// <summary>How a task change ended, from the operator's point of view.</summary>
public enum TicketTaskSubmitStatus
{
    /// <summary>TeamDynamix accepted it and the task reads back as asked.</summary>
    Succeeded,
    /// <summary>TeamDynamix refused it, or reads back as something else.</summary>
    Failed,
    /// <summary>
    /// The request went out but its result is unknown — a timeout, an
    /// unreadable answer, or a read-back that failed. It may have happened.
    /// </summary>
    Uncertain,
    /// <summary>
    /// Not sent: another change is in flight, the values are invalid, or the
    /// task changed since it was shown. Only this status leaves the loaded
    /// tasks usable; after the others they must be loaded again.
    /// </summary>
    NotSent,
}

public sealed record TicketTaskSubmitOutcome(
    TicketTaskSubmitStatus Status,
    int TicketId,
    int? TaskId,
    string Message)
{
    public bool Succeeded => Status == TicketTaskSubmitStatus.Succeeded;

    /// <summary>True when something may have changed on the server, so everything must be read again.</summary>
    public bool WasSent => Status != TicketTaskSubmitStatus.NotSent;
}

/// <summary>
/// Loads a ticket's tasks and changes them for the signed-in user, with the
/// guarding the UI needs: a stale load (the operator moved to another ticket)
/// is discarded; one change is in flight at a time; every change first reads
/// the task again and is not sent if it changed since it was shown; and once
/// a change is sent, whatever its outcome, the loaded tasks are spent, so a
/// retry needs a fresh load. Success is never assumed from a status code: the
/// task is read back and reported as TeamDynamix has it.
/// </summary>
public sealed class TicketTaskController
{
    private readonly ITicketTaskApi _api;
    private readonly object _gate = new();
    private long _generation;
    private CancellationTokenSource? _loadCts;
    private TicketTasksSnapshot? _current;
    private int _submitting;

    public TicketTaskController(ITicketTaskApi api) => _api = api;

    public ITicketTaskApi Api => _api;

    public TicketTasksSnapshot? Current
    {
        get { lock (_gate) return _current; }
    }

    public bool IsSubmitting => Volatile.Read(ref _submitting) != 0;

    public bool IsCurrent(TicketTasksSnapshot snapshot)
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

    /// <summary>Read the ticket's tasks. Null when a newer load or a Clear overtook this one.</summary>
    public async Task<TicketTasksSnapshot?> LoadAsync(int ticketId)
    {
        long generation;
        CancellationToken ct;
        lock (_gate)
        {
            generation = ++_generation;
            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            ct = _loadCts.Token;
            _current = new(ticketId, generation, TicketTasksPhase.Loading, Array.Empty<TdxTicketTask>(), null);
        }

        TicketTasksSnapshot snapshot;
        try
        {
            var tasks = await _api.GetTasksAsync(ticketId, false, ct);
            snapshot = new(ticketId, generation, TicketTasksPhase.Loaded, tasks, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            snapshot = new(ticketId, generation, TicketTasksPhase.Failed, Array.Empty<TdxTicketTask>(), Describe(ex));
        }

        lock (_gate)
        {
            if (generation != _generation) return null;
            _current = snapshot;
            return snapshot;
        }
    }

    /// <summary>
    /// The activity of one task in the current snapshot. Null when the
    /// snapshot was replaced while it loaded, so it is never shown against a
    /// different load.
    /// </summary>
    public async Task<(IReadOnlyList<TdxFeedEntry> Feed, string? Error)?> LoadFeedAsync(int ticketId, int taskId)
    {
        TicketTasksSnapshot? snapshot;
        CancellationToken ct;
        lock (_gate)
        {
            snapshot = _current;
            ct = _loadCts?.Token ?? CancellationToken.None;
        }
        if (snapshot is not { Phase: TicketTasksPhase.Loaded } || snapshot.TicketId != ticketId || snapshot.Find(taskId) == null)
            return null;

        (IReadOnlyList<TdxFeedEntry>, string?) result;
        try
        {
            result = (await _api.GetTaskFeedAsync(ticketId, taskId, ct), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            result = (Array.Empty<TdxFeedEntry>(), Describe(ex));
        }
        return IsCurrent(snapshot) ? result : null;
    }

    /// <summary>The tasks another task may follow, for the predecessor picker, without the task itself.</summary>
    public async Task<IReadOnlyList<TdxTicketTask>> LoadPredecessorChoicesAsync(int ticketId, int? excludingTaskId)
    {
        var tasks = await _api.GetTasksAsync(ticketId, true);
        return tasks.Where(t => t.Id != excludingTaskId).ToList();
    }

    // ── Changes ────────────────────────────────────────────────────────

    /// <summary>
    /// Set a task's progress and/or add a comment, through the task feed —
    /// the only route that can change PercentComplete. 100 completes the
    /// task. Progress is only sent when it differs from what TeamDynamix has.
    /// </summary>
    public Task<TicketTaskSubmitOutcome> UpdateProgressAsync(
        int ticketId, int taskId, int percentComplete, string? comments, bool isPrivate) =>
        RunAsync(ticketId, taskId, async shown =>
        {
            if (percentComplete is < 0 or > 100)
                return NotSent(ticketId, taskId, "Progress must be a whole number from 0 to 100.");

            var comment = string.IsNullOrWhiteSpace(comments) ? "" : comments.Trim();
            var progressChanged = percentComplete != shown!.PercentComplete;
            if (!progressChanged && comment.Length == 0)
                return NotSent(ticketId, taskId, $"The task is already at {percentComplete}%. Change the progress or add a comment.");

            var (latest, _) = await _api.GetTaskAsync(ticketId, taskId);
            if (Changed(shown, latest) is { } why) return Stale(ticketId, taskId, why);

            Spend();
            var request = new TdxTicketTaskFeedRequest
            {
                Comments = comment,
                PercentComplete = progressChanged ? percentComplete : null,
                IsPrivate = isPrivate,
            };
            try
            {
                await _api.PostTaskFeedAsync(ticketId, taskId, request);
            }
            catch (Exception ex)
            {
                return SendFailed(ticketId, taskId, ex);
            }

            var title = latest.DisplayTitle;
            return await ReadBackAsync(ticketId, taskId, after =>
            {
                if (progressChanged && after.PercentComplete != percentComplete)
                    return new(TicketTaskSubmitStatus.Failed, ticketId, taskId,
                        $"TeamDynamix accepted the update but shows “{title}” at {after.PercentComplete}%, not {percentComplete}%.");
                var what = !progressChanged ? "Comment added to"
                    : percentComplete == 100 ? "Completed"
                    : $"Set to {percentComplete}%:";
                return new(TicketTaskSubmitStatus.Succeeded, ticketId, taskId, $"{what} “{title}”.");
            });
        });

    /// <summary>Complete the task: progress 100 through the task feed.</summary>
    public Task<TicketTaskSubmitOutcome> CompleteAsync(int ticketId, int taskId, string? comments, bool isPrivate) =>
        UpdateProgressAsync(ticketId, taskId, 100, comments, isPrivate);

    /// <summary>Add a standard task. Workflow tasks and maintenance activities cannot be added this way.</summary>
    public Task<TicketTaskSubmitOutcome> CreateAsync(int ticketId, TicketTaskWrite values) =>
        RunAsync(ticketId, null, async _ =>
        {
            if (values.Validate() is { } invalid) return NotSent(ticketId, null, invalid);

            Spend();
            TdxTicketTask? created;
            try
            {
                created = await _api.CreateTaskAsync(ticketId, TicketTaskBody.ForCreate(values));
            }
            catch (Exception ex)
            {
                return SendFailed(ticketId, null, ex);
            }

            if (created is not { Id: > 0 })
                return new(TicketTaskSubmitStatus.Uncertain, ticketId, null,
                    "TeamDynamix answered without the new task. Check the refreshed list before adding it again.");
            return new(TicketTaskSubmitStatus.Succeeded, ticketId, created.Id, $"Added “{created.DisplayTitle}”.");
        });

    /// <summary>
    /// Save the fields the operator changed on a standard task. The task is
    /// read again first; if it changed since <paramref name="original"/> was
    /// taken, nothing is sent. The PUT carries the task's writable fields as
    /// just read, with only the changed ones replaced — a cleared field is
    /// sent as an explicit null.
    /// </summary>
    public Task<TicketTaskSubmitOutcome> EditAsync(int ticketId, int taskId, TicketTaskWrite original, TicketTaskWrite edited) =>
        RunAsync(ticketId, taskId, async shown =>
        {
            if (!shown!.IsStandard)
                return NotSent(ticketId, taskId, $"{TicketTaskFormat.TypeLabel(shown.Type)}s are managed by TeamDynamix and cannot be edited here.");
            if (edited.Validate(taskId) is { } invalid) return NotSent(ticketId, taskId, invalid);
            if (TicketTaskBody.Changes(original, edited).Count == 0) return NotSent(ticketId, taskId, "Nothing changed.");

            var (latest, raw) = await _api.GetTaskAsync(ticketId, taskId);
            if (Changed(shown, latest) is { } why) return Stale(ticketId, taskId, why);
            if (!latest.IsStandard) return Stale(ticketId, taskId, "it is no longer a standard task");

            Spend();
            try
            {
                await _api.UpdateTaskAsync(ticketId, taskId, TicketTaskBody.ForUpdate(raw, original, edited));
            }
            catch (Exception ex)
            {
                return SendFailed(ticketId, taskId, ex);
            }

            return await ReadBackAsync(ticketId, taskId, after =>
                TicketTaskBody.Changes(TicketTaskWrite.From(after), edited).Keys
                    .Where(k => k is "Title" or "EstimatedMinutes" or "PredecessorID" or "ResponsibleUid" or "ResponsibleGroupID")
                    .ToList() is { Count: > 0 } differ
                    ? new(TicketTaskSubmitStatus.Failed, ticketId, taskId,
                        $"TeamDynamix accepted the save but reads back differently ({string.Join(", ", differ)}).")
                    : new(TicketTaskSubmitStatus.Succeeded, ticketId, taskId, $"Saved “{after.DisplayTitle}”."));
        });

    /// <summary>
    /// Delete a standard task — irreversible, so the caller has confirmed it.
    /// Read again first; afterwards the ticket's tasks are read to prove it is gone.
    /// </summary>
    public Task<TicketTaskSubmitOutcome> DeleteAsync(int ticketId, int taskId) =>
        RunAsync(ticketId, taskId, async shown =>
        {
            if (!shown!.IsStandard)
                return NotSent(ticketId, taskId, $"{TicketTaskFormat.TypeLabel(shown.Type)}s cannot be deleted through TeamDynamix's task API.");

            var (latest, _) = await _api.GetTaskAsync(ticketId, taskId);
            if (Changed(shown, latest) is { } why) return Stale(ticketId, taskId, why);
            if (!latest.IsStandard) return Stale(ticketId, taskId, "it is no longer a standard task");

            Spend();
            try
            {
                await _api.DeleteTaskAsync(ticketId, taskId);
            }
            catch (Exception ex)
            {
                return SendFailed(ticketId, taskId, ex);
            }

            try
            {
                var remaining = await _api.GetTasksAsync(ticketId);
                return remaining.Any(t => t.Id == taskId)
                    ? new(TicketTaskSubmitStatus.Failed, ticketId, taskId, $"TeamDynamix answered, but “{latest.DisplayTitle}” is still on the ticket.")
                    : new(TicketTaskSubmitStatus.Succeeded, ticketId, taskId, $"Deleted “{latest.DisplayTitle}”.");
            }
            catch (Exception ex)
            {
                return new(TicketTaskSubmitStatus.Uncertain, ticketId, taskId,
                    $"The delete was accepted, but the tasks could not be read back to confirm it: {Describe(ex)}");
            }
        });

    /// <summary>
    /// Apply a task template. <paramref name="freshChoices"/> is the template
    /// list read again just now; the template is only applied when it is still
    /// offered. TeamDynamix ignores RemoveExistingTasks for anyone without
    /// permission to delete tasks, so the result says what actually happened
    /// to the tasks that were there, rather than what was asked.
    /// </summary>
    public Task<TicketTaskSubmitOutcome> ApplyTemplateAsync(
        int ticketId, TaskTemplateChoice template, IReadOnlyList<TaskTemplateChoice> freshChoices,
        bool removeExistingTasks, TaskTemplateStartDate startDate) =>
        RunAsync(ticketId, null, async _ =>
        {
            if (!freshChoices.Any(c => c.Id == template.Id))
                return NotSent(ticketId, null, $"“{template.Name}” is no longer offered for this ticket. Choose a template again.");

            var before = Current is { Phase: TicketTasksPhase.Loaded } s && s.TicketId == ticketId ? s.Tasks : Array.Empty<TdxTicketTask>();
            Spend();
            IReadOnlyList<TdxTicketTask> after;
            try
            {
                after = await _api.ApplyTaskTemplateAsync(ticketId, new TdxApplyTaskTemplateRequest
                {
                    TaskTemplateId = template.Id,
                    RemoveExistingTasks = removeExistingTasks,
                    StartDateSource = startDate,
                });
            }
            catch (Exception ex)
            {
                return SendFailed(ticketId, null, ex);
            }

            var afterIds = after.Select(t => t.Id).ToHashSet();
            var added = after.Count(t => !before.Any(b => b.Id == t.Id));
            var keptStandard = before.Count(b => b.IsStandard && afterIds.Contains(b.Id));
            var message = $"Applied “{template.Name}”: {added} task{(added == 1 ? "" : "s")} added.";
            if (removeExistingTasks && keptStandard > 0)
                message += $" {keptStandard} existing task{(keptStandard == 1 ? " was" : "s were")} kept — TeamDynamix removes them only for people allowed to delete tasks.";
            return new(TicketTaskSubmitStatus.Succeeded, ticketId, null, message);
        });

    // ── Guarding ───────────────────────────────────────────────────────

    /// <summary>
    /// Run a change: one at a time, only against the current loaded snapshot
    /// of <paramref name="ticketId"/>, and (with a task) only for a task that
    /// snapshot shows. Exceptions before anything was sent are NotSent.
    /// </summary>
    private async Task<TicketTaskSubmitOutcome> RunAsync(
        int ticketId, int? taskId, Func<TdxTicketTask?, Task<TicketTaskSubmitOutcome>> change)
    {
        if (Interlocked.CompareExchange(ref _submitting, 1, 0) != 0)
            return NotSent(ticketId, taskId, "Another task change is still being sent.");

        _spent = false;
        try
        {
            var snapshot = Current;
            if (snapshot is not { Phase: TicketTasksPhase.Loaded } || snapshot.TicketId != ticketId)
                return NotSent(ticketId, taskId, "The tasks changed before this was sent. Review them and try again.");

            TdxTicketTask? shown = null;
            if (taskId is int id)
            {
                shown = snapshot!.Find(id);
                if (shown == null)
                    return NotSent(ticketId, taskId, "That task is no longer on the ticket. Refresh and try again.");
            }

            try
            {
                return await change(shown);
            }
            catch (Exception ex) when (!_spent)
            {
                // Failed while reading the latest state: nothing was sent, but
                // what is on screen can no longer be trusted either.
                Invalidate();
                return new(TicketTaskSubmitStatus.NotSent, ticketId, taskId,
                    $"Nothing was sent: the task could not be read first. {Describe(ex)}");
            }
        }
        finally
        {
            Volatile.Write(ref _submitting, 0);
        }
    }

    private bool _spent;

    /// <summary>
    /// The change is about to go out. From here the loaded tasks are spent: a
    /// timeout or refusal can still follow a committed change, so nothing on
    /// screen may be sent again without a fresh load.
    /// </summary>
    private void Spend()
    {
        _spent = true;
        Invalidate();
    }

    /// <summary>Why <paramref name="latest"/> no longer matches what the operator saw, or null.</summary>
    internal static string? Changed(TdxTicketTask shown, TdxTicketTask latest)
    {
        if (latest.Id != shown.Id) return "it is a different task";
        if (latest.TypeId != shown.TypeId) return "its type changed";
        if (latest.PercentComplete != shown.PercentComplete) return $"its progress is now {latest.PercentComplete}%";
        if (latest.ModifiedDate != shown.ModifiedDate) return "someone changed it";
        return null;
    }

    private TicketTaskSubmitOutcome Stale(int ticketId, int taskId, string why)
    {
        Invalidate();
        return new(TicketTaskSubmitStatus.NotSent, ticketId, taskId,
            $"Not sent: the task changed since it was shown ({why}). The tasks are being refreshed; review and try again.");
    }

    private static TicketTaskSubmitOutcome NotSent(int ticketId, int? taskId, string message) =>
        new(TicketTaskSubmitStatus.NotSent, ticketId, taskId, message);

    /// <summary>
    /// A request that went out and failed: refused when TeamDynamix answered
    /// with a status, uncertain when it never answered or answered unreadably.
    /// </summary>
    private static TicketTaskSubmitOutcome SendFailed(int ticketId, int? taskId, Exception ex) =>
        ex is TdxTicketTaskException { StatusCode: >= 400 and < 500 } or ArgumentException
            ? new(TicketTaskSubmitStatus.Failed, ticketId, taskId, Describe(ex))
            : new(TicketTaskSubmitStatus.Uncertain, ticketId, taskId,
                $"{Describe(ex)} It may still have been applied; check the refreshed tasks before trying again.");

    private async Task<TicketTaskSubmitOutcome> ReadBackAsync(int ticketId, int taskId, Func<TdxTicketTask, TicketTaskSubmitOutcome> judge)
    {
        try
        {
            var (after, _) = await _api.GetTaskAsync(ticketId, taskId);
            return judge(after);
        }
        catch (Exception ex)
        {
            return new(TicketTaskSubmitStatus.Uncertain, ticketId, taskId,
                $"TeamDynamix accepted the change, but the task could not be read back to confirm it: {Describe(ex)}");
        }
    }

    /// <summary>FleetMate's own message for a failure; never raw transport text.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        TdxTicketTaskException or ArgumentException => ex.Message,
        _ => "TeamDynamix could not be reached.",
    };
}
