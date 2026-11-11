using FleetMate.Core.Models.Projects;
using FleetMate.Core.Shared;
using Serilog;

namespace FleetMate.Core.Services.Projects;

/// <summary>
/// Pending approvals holding active Azure Pipelines runs, pinned to their run.
/// Approvals are read only for projects that have active runs. Environment
/// approvals name their build; ManualValidation ones are found in the active
/// runs' timelines, and the approval id → build id match is cached because
/// neither id ever moves. Mirrors the Mac app's DevelopmentModel approvals.
/// </summary>
public sealed class PipelineApprovalTracker
{
    private readonly object _gate = new();
    private Dictionary<string, List<PipelineApproval>> _pending = new();
    private readonly Dictionary<string, long> _approvalBuilds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after a refresh or a response changed what is pending.</summary>
    public event EventHandler? Changed;

    internal static string Key(string project, long buildId) => $"{project}#{buildId}";

    private static bool Applies(PipelineRun run) =>
        run.Source == PullRequestSource.AzureDevOps && run.Status.IsActive();

    public IReadOnlyList<PipelineApproval> For(PipelineRun run)
    {
        if (!Applies(run)) return Array.Empty<PipelineApproval>();
        lock (_gate)
            return _pending.TryGetValue(Key(run.Container, run.RunId), out var list) ? list.ToList() : Array.Empty<PipelineApproval>();
    }

    public bool IsAwaiting(PipelineRun run) => For(run).Count > 0;

    /// <summary>
    /// Re-read pending approvals for <paramref name="projects"/> (default: every
    /// project with an active run). Pins for other projects are kept while their
    /// run is still active. A project whose read fails keeps what was known, so a
    /// blip does not drop the badge. Returns true when anything changed.
    /// </summary>
    public async Task<bool> RefreshAsync(IReadOnlyCollection<PipelineRun> runs, IPipelineApprovalSource source,
        IReadOnlyCollection<string>? projects = null, CancellationToken ct = default)
    {
        var active = runs.Where(Applies).ToList();
        var byProject = active.GroupBy(r => r.Container).ToDictionary(g => g.Key, g => g.ToList());
        var targets = (projects ?? byProject.Keys).ToHashSet();
        var activeKeys = active.Select(r => Key(r.Container, r.RunId)).ToHashSet();

        Dictionary<string, List<PipelineApproval>> known;
        lock (_gate) known = _pending;

        var next = known
            .Where(pair => !targets.Any(project => pair.Key.StartsWith(project + "#", StringComparison.Ordinal)) && activeKeys.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        foreach (var project in targets.OrderBy(p => p, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (!byProject.TryGetValue(project, out var projectRuns) || projectRuns.Count == 0) continue;

            List<PipelineApproval> approvals;
            try { approvals = await source.GetPendingApprovalsAsync(project); }
            catch (Exception ex)
            {
                ct.ThrowIfCancellationRequested();
                Log.Debug(ex, "[approvals] pending approvals unavailable for {Project}", project);
                foreach (var run in projectRuns)
                    if (known.TryGetValue(Key(project, run.RunId), out var kept)) next[Key(project, run.RunId)] = kept;
                continue;
            }

            var resolved = new List<PipelineApproval>();
            var unresolved = new List<PipelineApproval>();
            foreach (var approval in approvals)
            {
                long? build = approval.BuildId;
                if (build == null) lock (_gate) if (_approvalBuilds.TryGetValue(approval.Id, out var cached)) build = cached;
                if (build is { } b) resolved.Add(approval.AssignedTo(b));
                else unresolved.Add(approval);
            }

            if (unresolved.Count > 0)
            {
                foreach (var run in projectRuns)
                {
                    if (unresolved.Count == 0) break;
                    HashSet<string> open;
                    try { open = await source.GetOpenTimelineIdentifiersAsync(project, run.RunId); }
                    catch (Exception ex)
                    {
                        ct.ThrowIfCancellationRequested();
                        Log.Debug(ex, "[approvals] timeline unavailable for {Project} build {Build}", project, run.RunId);
                        continue;
                    }
                    foreach (var approval in unresolved.Where(a => open.Contains(a.Id.ToLowerInvariant())).ToList())
                    {
                        lock (_gate) _approvalBuilds[approval.Id] = run.RunId;
                        resolved.Add(approval.AssignedTo(run.RunId));
                        unresolved.Remove(approval);
                    }
                }
            }

            foreach (var approval in resolved)
            {
                var key = Key(project, approval.BuildId!.Value);
                if (!next.TryGetValue(key, out var list)) next[key] = list = new List<PipelineApproval>();
                list.Add(approval);
            }
        }

        ct.ThrowIfCancellationRequested();
        bool changed;
        lock (_gate)
        {
            changed = !Same(_pending, next);
            if (changed) _pending = next;
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    /// <summary>Drop an approval once it has been answered.</summary>
    public void Remove(PipelineApproval approval)
    {
        if (approval.BuildId is not { } build) return;
        var key = Key(approval.Project, build);
        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out var list)) return;
            var next = new Dictionary<string, List<PipelineApproval>>(_pending);
            var remaining = list.Where(a => !string.Equals(a.Id, approval.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (remaining.Count == 0) next.Remove(key); else next[key] = remaining;
            _pending = next;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool Same(Dictionary<string, List<PipelineApproval>> a, Dictionary<string, List<PipelineApproval>> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && pair.Value.Select(x => x.Id).SequenceEqual(other.Select(x => x.Id)));

    /// <summary>"1 approval needs your review…" / "2 approvals need your review…".</summary>
    public static string BannerText(int count) => count == 1
        ? "1 approval needs your review before this run can continue"
        : $"{count} approvals need your review before this run can continue";
}
