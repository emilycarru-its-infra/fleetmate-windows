namespace FleetMate.Core.Models.Projects;

/// <summary>
/// The figures behind the Projects strip's tiles, from the signed-in user's
/// open work items as already loaded — no queries of their own. The macOS
/// app counts the same way.
/// </summary>
public sealed record WorkItemStats
{
    public const int StaleDays = 30;
    public const int InitiativeMaxDepth = 16;

    private static readonly HashSet<string> NotStarted = new(StringComparer.OrdinalIgnoreCase) { "New", "To Do", "Proposed" };

    public int Open { get; init; }
    /// <summary>In a state other than New, To Do or Proposed.</summary>
    public int InProgress { get; init; }
    public int NewThisWeek { get; init; }
    public int UpdatedThisWeek { get; init; }
    /// <summary>Tagged Blocked, or on a board column or in a state containing "blocked".</summary>
    public int Blocked { get; init; }
    public int Overdue { get; init; }
    /// <summary>No change in <see cref="StaleDays"/> days.</summary>
    public int Stale { get; init; }

    public static WorkItemStats From(IEnumerable<WorkItem> items, DateTime nowUtc)
    {
        var list = items as IReadOnlyCollection<WorkItem> ?? items.ToList();
        int Count(ProjectsTile tile) => list.Count(w => Matches(w, tile, nowUtc));
        return new WorkItemStats
        {
            Open = list.Count,
            InProgress = Count(ProjectsTile.InProgress),
            NewThisWeek = Count(ProjectsTile.NewThisWeek),
            UpdatedThisWeek = Count(ProjectsTile.UpdatedThisWeek),
            Blocked = Count(ProjectsTile.Blocked),
            Overdue = Count(ProjectsTile.Overdue),
            Stale = Count(ProjectsTile.Stale),
        };
    }

    /// <summary>
    /// Whether <paramref name="item"/> counts toward <paramref name="tile"/>: the one
    /// rule behind both a figure and the filter its tile applies. GitHub Issues
    /// counts no work item.
    /// </summary>
    public static bool Matches(WorkItem item, ProjectsTile tile, DateTime nowUtc) => tile switch
    {
        ProjectsTile.AssignedToMe => true,
        ProjectsTile.InProgress => !string.IsNullOrEmpty(item.State) && !NotStarted.Contains(item.State),
        ProjectsTile.NewThisWeek => Utc(item.CreatedDate) is { } c && c >= nowUtc.AddDays(-7),
        ProjectsTile.UpdatedThisWeek => Utc(item.ChangedDate) is { } ch && ch >= nowUtc.AddDays(-7),
        ProjectsTile.Blocked => IsBlocked(item),
        ProjectsTile.Overdue => Utc(item.Fields.DueDate) is { } due && due < nowUtc,
        ProjectsTile.Stale => Utc(item.ChangedDate) is { } old && old < nowUtc.AddDays(-StaleDays),
        _ => false,
    };

    public static bool IsBlocked(WorkItem item) =>
        item.Tags.Any(t => t.Equals("Blocked", StringComparison.OrdinalIgnoreCase))
        || item.State.Contains("blocked", StringComparison.OrdinalIgnoreCase)
        || (item.Fields.BoardColumn?.Contains("blocked", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Counts by a field, most first, ties by name; items without the field are left out.</summary>
    public static List<(string Label, int Count)> Ranked(IEnumerable<WorkItem> items, Func<WorkItem, string?> key) => items
        .Select(key)
        .Where(k => !string.IsNullOrWhiteSpace(k))
        .GroupBy(k => k!)
        .Select(g => (g.Key, g.Count()))
        .OrderByDescending(x => x.Item2).ThenBy(x => x.Key, StringComparer.Ordinal)
        .ToList();

    /// <summary>Count only the supplied items; ancestors supply labels, never extra work.</summary>
    public static List<(string Label, int Count)> Initiatives(IEnumerable<WorkItem> items, IEnumerable<WorkItem> ancestors)
    {
        var original = items.ToList();
        var index = ancestors.Concat(original).GroupBy(w => w.Id).ToDictionary(g => g.Key, g => g.Last());
        return Ranked(original, item =>
        {
            var current = item;
            var seen = new HashSet<int>();
            for (var depth = 0; depth <= InitiativeMaxDepth && seen.Add(current.Id); depth++)
            {
                if (current.WorkItemType.Equals("Initiative", StringComparison.OrdinalIgnoreCase))
                {
                    var title = current.Title.Trim();
                    return title.Length == 0 ? $"Initiative #{current.Id}" : $"{title} (#{current.Id})";
                }
                if (current.HierarchyParentId is not { } parent) return "Unassigned";
                if (depth == InitiativeMaxDepth || !index.TryGetValue(parent, out var next)) return "Unavailable";
                current = next;
            }
            return "Unavailable";
        });
    }

    private static DateTime? Utc(DateTime? value) => value is not { } v ? null
        : v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc);
}
