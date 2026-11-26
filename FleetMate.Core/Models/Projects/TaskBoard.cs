using FleetMate.Core.Development;

namespace FleetMate.Core.Models.Projects;

/// <summary>One board column: its title, accent and cards, newest change first.</summary>
public sealed record TaskBoardGroup(string Title, string ColorHex, ChartTone Tone, IReadOnlyList<UnifiedTask> Tasks);

/// <summary>
/// The Projects board's rules, shared by both Windows apps and matching the
/// macOS BoardsView: the group-by dimensions and the columns each makes, the
/// search, bucket and closed filters, what a card dropped on a column changes,
/// the Mine set and the Reschedule dates.
/// </summary>
public static class TaskBoard
{
    /// <summary>The Group By choices, in the macOS menu's order.</summary>
    public static readonly IReadOnlyList<string> GroupByOptions = new[]
    {
        "State", "Area Path", "Iteration", "Type", "Priority", "Assigned To",
    };

    public const string Unassigned = "Unassigned";
    public const string NoArea = "No Area";
    public const string NoIteration = "No Iteration";
    public const string UnknownType = "Unknown";

    private static readonly string[] ClosedStateNames = { "closed", "done", "removed", "completed", "resolved" };

    /// <summary>The priority columns: key, label, colour.</summary>
    public static readonly IReadOnlyList<(int Key, string Label, string Color, ChartTone Tone)> Priorities = new[]
    {
        (1, "Critical", "#C05050", ChartTone.Pink), (2, "High", "#DD8844", ChartTone.Orange),
        (3, "Medium", "#C9A83A", ChartTone.Yellow), (4, "Low", "#4C9A60", ChartTone.Green),
        (0, "None", "#808080", ChartTone.Gray),
    };

    /// <summary>
    /// Search, strip-tile, bucket and closed filters, shared by the board and
    /// the Mine and Recent lists. State columns handle closed items
    /// themselves; every other dimension, and the flat lists, drop closed
    /// tasks unless Show Closed is on, like the Mac board.
    /// </summary>
    public static List<UnifiedTask> Filter(IEnumerable<UnifiedTask> source, string? search, string? bucket,
        bool showClosed, string groupBy, bool flat, Func<UnifiedTask, bool>? tilePasses = null)
    {
        var filtered = source;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            filtered = filtered.Where(t =>
                t.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (t.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        if (tilePasses != null) filtered = filtered.Where(tilePasses);
        if (!string.IsNullOrEmpty(bucket) && bucket != AllBuckets) filtered = filtered.Where(t => t.Bucket == bucket);
        if (!showClosed && (groupBy != "State" || flat)) filtered = filtered.Where(t => t.State != TaskState.Closed);
        return filtered.ToList();
    }

    /// <summary>The bucket filter's first entry, which filters nothing.</summary>
    public const string AllBuckets = "(All Buckets)";

    /// <summary>The columns <paramref name="groupBy"/> makes from <paramref name="tasks"/>.</summary>
    public static List<TaskBoardGroup> Columns(IReadOnlyCollection<UnifiedTask> tasks, string groupBy, bool showClosed)
    {
        switch (groupBy)
        {
            case "Priority":
            {
                var grouped = tasks.GroupBy(t => t.Priority ?? 0).ToDictionary(g => g.Key, g => g.ToList());
                return Priorities.Select(p => Make(p.Label, p.Color, p.Tone, grouped.GetValueOrDefault(p.Key) ?? new())).ToList();
            }
            case "Assigned To":
                return tasks.GroupBy(t => t.Assignees.FirstOrDefault() ?? Unassigned)
                    .OrderBy(g => g.Key == Unassigned ? 0 : 1)
                    .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(g => Make(g.Key, "#5B6BAE", ChartTone.Indigo, g.ToList()))
                    .ToList();
            case "Area Path":
                return Alphabetical(tasks, t => Meta(t, "areaPath", NoArea), NoArea, "#8E6BA5", ChartTone.Purple);
            case "Iteration":
                return Alphabetical(tasks, t => Meta(t, "iterationPath", NoIteration), NoIteration, "#A5783A", ChartTone.Brown);
            case "Type":
                return Alphabetical(tasks, t => Meta(t, "workItemType", UnknownType), UnknownType, "#4C8C8C", ChartTone.Teal);
            default:
            {
                var grouped = tasks.GroupBy(t => Meta(t, "state", "New")).ToDictionary(g => g.Key, g => g.ToList());
                return grouped.Keys
                    .Where(k => showClosed || !IsClosedState(k))
                    .OrderBy(StateOrder)
                    .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .Select(s => Make(s, StateColorHex(s), StateTone(s), grouped[s]))
                    .ToList();
            }
        }
    }

    public static bool IsClosedState(string state) => ClosedStateNames.Contains(state.ToLowerInvariant());

    private static List<TaskBoardGroup> Alphabetical(IEnumerable<UnifiedTask> tasks, Func<UnifiedTask, string> key,
        string fallback, string color, ChartTone tone) => tasks.GroupBy(key)
        .OrderBy(g => g.Key == fallback ? 1 : 0)
        .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
        .Select(g => Make(g.Key, color, tone, g.ToList()))
        .ToList();

    private static TaskBoardGroup Make(string title, string color, ChartTone tone, List<UnifiedTask> tasks) =>
        new(title, color, tone, tasks.OrderByDescending(t => t.UpdatedAt).ToList());

    public static string Meta(UnifiedTask task, string key, string fallback = "") =>
        task.Metadata.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : fallback;

    public static int StateOrder(string state) => state.ToLowerInvariant() switch
    {
        "new" or "to do" or "proposed" or "open" => 0,
        "active" or "in progress" or "doing" or "committed" => 1,
        "resolved" or "done" or "completed" => 2,
        "closed" => 3,
        "removed" => 4,
        _ => 2,
    };

    public static string StateColorHex(string state) => state.ToLowerInvariant() switch
    {
        "new" or "to do" or "proposed" or "open" => "#4C9A60",
        "active" or "in progress" or "doing" or "committed" => "#3A6EA5",
        "resolved" or "done" or "completed" => "#8E6BA5",
        "closed" => "#707070",
        "removed" => "#C05050",
        _ => "#3A6EA5",
    };

    public static ChartTone StateTone(string state) => state.ToLowerInvariant() switch
    {
        "new" or "to do" or "proposed" or "open" => ChartTone.Green,
        "active" or "in progress" or "doing" or "committed" => ChartTone.Blue,
        "resolved" or "done" or "completed" => ChartTone.Purple,
        "closed" => ChartTone.Gray,
        "removed" => ChartTone.Pink,
        _ => ChartTone.Blue,
    };

    public static int? PriorityFromLabel(string label) => label switch
    {
        "Critical" => 1, "High" => 2, "Medium" => 3, "Low" => 4, _ => null,
    };

    /// <summary>
    /// What dropping a card on <paramref name="column"/> changes, grouped by
    /// <paramref name="groupBy"/>; null when the drop changes nothing (Type,
    /// or a column such as Unassigned that names no value). Drops update Azure
    /// DevOps work items only, as on the Mac.
    /// </summary>
    public static UpdateWorkItemRequest? DropRequest(string groupBy, string column) => groupBy switch
    {
        "State" => new UpdateWorkItemRequest { State = column },
        "Priority" => PriorityFromLabel(column) is int p ? new UpdateWorkItemRequest { Priority = p } : null,
        "Assigned To" => column == Unassigned ? null : new UpdateWorkItemRequest { AssignedTo = column },
        "Iteration" => column == NoIteration ? null : new UpdateWorkItemRequest { IterationPath = column },
        "Area Path" => column == NoArea ? null : new UpdateWorkItemRequest { AreaPath = column },
        _ => null,
    };

    /// <summary>Whether a drag of <paramref name="task"/> may land on <paramref name="column"/>.</summary>
    public static bool CanDrop(UnifiedTask task, string groupBy, string column) =>
        task.Provider == "azdevops" && int.TryParse(task.Id, out _)
        && DropRequest(groupBy, column) != null
        && !string.Equals(CurrentValue(task, groupBy), column, StringComparison.OrdinalIgnoreCase);

    /// <summary>The column <paramref name="task"/> sits in under <paramref name="groupBy"/>.</summary>
    public static string CurrentValue(UnifiedTask task, string groupBy) => groupBy switch
    {
        "Priority" => Priorities.FirstOrDefault(p => p.Key == (task.Priority ?? 0)).Label ?? "None",
        "Assigned To" => task.Assignees.FirstOrDefault() ?? Unassigned,
        "Area Path" => Meta(task, "areaPath", NoArea),
        "Iteration" => Meta(task, "iterationPath", NoIteration),
        "Type" => Meta(task, "workItemType", UnknownType),
        _ => Meta(task, "state", "New"),
    };

    /// <summary>The drop's caption: "Set State to Active".</summary>
    public static string DropCaption(string groupBy, string column) => groupBy switch
    {
        "Assigned To" => $"Assign to {column}",
        _ => $"Set {groupBy} to {column}",
    };

    /// <summary>Patch <paramref name="task"/> from the work item an update returned.</summary>
    public static void ApplyUpdate(UnifiedTask task, WorkItem updated)
    {
        task.Metadata["state"] = updated.State;
        task.Metadata["areaPath"] = updated.AreaPath ?? "";
        task.Metadata["iterationPath"] = updated.IterationPath ?? "";
        task.Metadata["workItemType"] = updated.WorkItemType;
        task.Priority = updated.Priority;
        task.Assignees = updated.AssignedTo != null ? new List<string> { updated.AssignedTo } : new List<string>();
        task.State = updated.State.ToLowerInvariant() switch
        {
            "new" or "to do" or "proposed" or "open" => TaskState.Open,
            "active" or "in progress" or "doing" or "committed" => TaskState.InProgress,
            _ => TaskState.Closed,
        };
    }

    /// <summary>The Mine rows: @Me work items plus GitHub items assigned to the viewer, newest change first.</summary>
    public static List<UnifiedTask> MineTasks(
        IEnumerable<UnifiedTask> loaded, IEnumerable<UnifiedTask> extra, IReadOnlySet<int> myIds, string? viewerLogin) =>
        loaded.Concat(extra)
            .GroupBy(t => (t.Provider, t.Id)).Select(g => g.First())
            .Where(t => t.Provider == "azdevops"
                ? int.TryParse(t.Id, out var id) && myIds.Contains(id)
                : !string.IsNullOrEmpty(viewerLogin)
                  && t.Assignees.Any(a => string.Equals(a, viewerLogin, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(t => t.UpdatedAt)
            .ToList();

    /// <summary>Tomorrow / Next Monday / End of Month, sent as the due date (yyyy-MM-dd).</summary>
    public static DateTime RescheduleDate(string choice, DateTime today) => choice switch
    {
        "tomorrow" => today.AddDays(1),
        // The coming Monday; on a Monday, the next one.
        "monday" => today.AddDays(((int)DayOfWeek.Monday - (int)today.DayOfWeek + 6) % 7 + 1),
        _ => new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)),
    };

    /// <summary>Replace the task with <paramref name="fresh"/>'s provider and id in <paramref name="list"/>.</summary>
    public static void Replace(List<UnifiedTask> list, UnifiedTask old, UnifiedTask fresh)
    {
        var index = list.FindIndex(t => t.Provider == old.Provider && t.Id == old.Id);
        if (index >= 0) list[index] = fresh;
    }
}

/// <summary>The text a board card and a Mine or Recent row show for a task.</summary>
public static class TaskCardText
{
    public static string ProviderName(string provider) => provider switch
    {
        "azdevops" => "DevOps",
        "github" => "GitHub",
        "gitea" => "Gitea",
        "tdx" => "TDX",
        _ => provider,
    };

    public static string TypeName(UnifiedTask task) => TaskBoard.Meta(task, "workItemType");

    public static string AreaPath(UnifiedTask task) => TaskBoard.Meta(task, "areaPath");

    public static string Iteration(UnifiedTask task) => TaskBoard.Meta(task, "iterationPath", task.Bucket ?? "");

    public static string Assignees(UnifiedTask task) =>
        task.Assignees.Count > 0 ? string.Join(", ", task.Assignees) : "";

    /// <summary>Mine/Recent row second line: "#123 · Bug · Active · Projects › Devices".</summary>
    public static string RowDetail(UnifiedTask task) => string.Join(" · ", new[]
    {
        task.Provider == "azdevops" ? $"#{task.Id}" : ProviderName(task.Provider),
        TypeName(task),
        TaskBoard.Meta(task, "state"),
        AreaPath(task).Replace("\\", " › "),
    }.Where(x => !string.IsNullOrEmpty(x)));

    /// <summary>What Copy ID puts on the clipboard: a work item's number, or "#n" for an issue.</summary>
    public static string CopyId(UnifiedTask task) => task.Provider == "azdevops" ? task.Id : $"#{task.Id}";

    /// <summary>"P1".."P4" for a set priority, empty for none.</summary>
    public static string Priority(UnifiedTask task) => task.Priority is int p and > 0 ? $"P{p}" : "";

    /// <summary>A card's accessible name: title, then type, state and assignee.</summary>
    public static string Description(UnifiedTask task) => string.Join(", ", new[]
    {
        task.Title,
        TypeName(task),
        TaskBoard.Meta(task, "state"),
        task.Assignees.Count > 0 ? $"assigned to {Assignees(task)}" : "",
    }.Where(x => !string.IsNullOrEmpty(x)));

    /// <summary>"5m", "3h", "2d", or the date past a month, as the lists show a change.</summary>
    public static string Relative(DateTime when, DateTime nowUtc)
    {
        if (when <= DateTime.MinValue) return "";
        var span = nowUtc - when.ToUniversalTime();
        return span.TotalMinutes < 60 ? $"{Math.Max(1, (int)span.TotalMinutes)}m"
            : span.TotalHours < 24 ? $"{(int)span.TotalHours}h"
            : span.TotalDays < 30 ? $"{(int)span.TotalDays}d"
            : when.ToLocalTime().ToString("MMM d");
    }
}
