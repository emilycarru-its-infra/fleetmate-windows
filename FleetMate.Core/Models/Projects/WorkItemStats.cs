namespace FleetMate.Core.Models.Projects;

/// <summary>
/// The figures behind the Projects strip's tiles, from the signed-in user's
/// open work items as already loaded — no queries of their own. The macOS
/// app counts the same way.
/// </summary>
public sealed record WorkItemStats
{
    public const int StaleDays = 30;

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
        var week = nowUtc.AddDays(-7);
        var stale = nowUtc.AddDays(-StaleDays);
        int open = 0, inProgress = 0, created = 0, updated = 0, blocked = 0, overdue = 0, old = 0;
        foreach (var item in items)
        {
            open++;
            if (!string.IsNullOrEmpty(item.State) && !NotStarted.Contains(item.State)) inProgress++;
            if (Utc(item.CreatedDate) is { } c && c >= week) created++;
            if (Utc(item.ChangedDate) is { } ch)
            {
                if (ch >= week) updated++;
                if (ch < stale) old++;
            }
            if (IsBlocked(item)) blocked++;
            if (Utc(item.Fields.DueDate) is { } due && due < nowUtc) overdue++;
        }
        return new WorkItemStats
        {
            Open = open, InProgress = inProgress, NewThisWeek = created, UpdatedThisWeek = updated,
            Blocked = blocked, Overdue = overdue, Stale = old,
        };
    }

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

    private static DateTime? Utc(DateTime? value) => value is not { } v ? null
        : v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc);
}
