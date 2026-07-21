using FleetMate.Core.Config;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Serilog;

namespace FleetMate.Core.Services.Devices;

/// <summary>What the board sync did for a package.</summary>
public enum ReadinessSyncAction { Disabled, NotConfigured, Skipped, Created, Updated, Failed }

/// <summary>Outcome of a single package-readiness board sync.</summary>
public sealed class ReadinessSyncResult
{
    public ReadinessSyncAction Action { get; init; }
    public int? WorkItemId { get; init; }
    public string? State { get; init; }
    public string? Title { get; init; }
    public string? Error { get; init; }

    public static ReadinessSyncResult Of(ReadinessSyncAction a, string? error = null) => new() { Action = a, Error = error };
}

/// <summary>
/// Syncs QA outcomes to an Azure DevOps board as one upserted work item per package.
/// Idempotent: the item is found by title+tag and re-used across runs; only its State
/// and a History note change. Uses the generic <see cref="AzureDevOpsService"/> work-item
/// API, so it carries no board-specific assumptions beyond the (config-driven) mappings.
/// </summary>
public sealed class PackageReadinessService
{
    private readonly AzureDevOpsService _ado;
    private readonly PackageReadinessConfig _cfg;
    private readonly string _project;

    public PackageReadinessService(AzureDevOpsService ado, PackageReadinessConfig cfg, string project)
    {
        _ado = ado;
        _cfg = cfg;
        _project = project;
    }

    /// <summary>Map a checklist status to the configured board State.</summary>
    public string StateFor(ChecklistStatus status) => status switch
    {
        ChecklistStatus.Passed => _cfg.StatePassed,
        ChecklistStatus.Warning => _cfg.StateWarning,
        ChecklistStatus.Failed => _cfg.StateFailed,
        _ => _cfg.StateUntested,
    };

    private string TitleFor(string package) =>
        string.IsNullOrWhiteSpace(_cfg.TitlePrefix) ? package : $"{_cfg.TitlePrefix} {package}";

    /// <summary>
    /// Upsert the readiness work item for <paramref name="package"/> to reflect <paramref name="status"/>.
    /// </summary>
    /// <param name="version">Package version, for the History note (optional).</param>
    /// <param name="tester">Who ran the test, for the History note (optional).</param>
    /// <param name="timestamp">When the test ran; defaults to now.</param>
    public async Task<ReadinessSyncResult> SyncAsync(
        string package, string? version, ChecklistStatus status, string? tester, DateTime? timestamp = null)
    {
        if (!_cfg.Enabled) return ReadinessSyncResult.Of(ReadinessSyncAction.Disabled);
        if (string.IsNullOrWhiteSpace(_project)) return ReadinessSyncResult.Of(ReadinessSyncAction.NotConfigured);

        var title = TitleFor(package);
        var state = StateFor(status);
        var ts = (timestamp ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm");
        var who = string.IsNullOrWhiteSpace(tester) ? "" : $" by {tester}";
        var ver = string.IsNullOrWhiteSpace(version) ? "" : $" v{version}";
        var comment =
            $"QA result: <b>{status}</b>{ver} &rarr; <b>{state}</b> ({ts}{who})<br/><em>Synced by fleetmate</em>";

        try
        {
            var existing = await FindExistingAsync(title);
            if (existing != null)
            {
                var updated = await _ado.UpdateWorkItemAsync(existing.Id, new UpdateWorkItemRequest
                {
                    State = state,
                    Comment = comment,
                });
                if (updated == null)
                    return ReadinessSyncResult.Of(ReadinessSyncAction.Failed, "update returned null");
                Log.Information("Readiness: updated #{Id} '{Title}' -> {State}", existing.Id, title, state);
                return new ReadinessSyncResult { Action = ReadinessSyncAction.Updated, WorkItemId = existing.Id, State = state, Title = title };
            }

            var created = await _ado.CreateWorkItemAsync(new CreateWorkItemRequest
            {
                Title = title,
                Type = _cfg.WorkItemType,
                Description = $"Package readiness tracked by fleetmate for <b>{package}</b>.",
                AreaPath = string.IsNullOrWhiteSpace(_cfg.AreaPath) ? null : _cfg.AreaPath,
                IterationPath = string.IsNullOrWhiteSpace(_cfg.IterationPath) ? null : _cfg.IterationPath,
                Tags = new List<string> { _cfg.Tag, package },
            });
            if (created == null)
                return ReadinessSyncResult.Of(ReadinessSyncAction.Failed, "create returned null");

            // New items start in the process's initial state; move to the mapped state
            // and record the first result as a History note.
            await _ado.UpdateWorkItemAsync(created.Id, new UpdateWorkItemRequest { State = state, Comment = comment });
            Log.Information("Readiness: created #{Id} '{Title}' -> {State}", created.Id, title, state);
            return new ReadinessSyncResult { Action = ReadinessSyncAction.Created, WorkItemId = created.Id, State = state, Title = title };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Readiness sync failed for {Package}", package);
            return ReadinessSyncResult.Of(ReadinessSyncAction.Failed, ex.Message);
        }
    }

    private async Task<WorkItem?> FindExistingAsync(string title)
    {
        var wiql =
            "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project " +
            $"AND [System.Tags] CONTAINS '{Escape(_cfg.Tag)}' " +
            $"AND [System.Title] = '{Escape(title)}' " +
            "ORDER BY [System.ChangedDate] DESC";
        var items = await _ado.QueryWorkItemsAsync(wiql, orgLevel: false);
        return items.FirstOrDefault();
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
