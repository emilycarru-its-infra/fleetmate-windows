namespace FleetMate.Core.Models.Projects;

/// <summary>The Projects strip's figures, each of which doubles as a filter on the page's lists.</summary>
public enum ProjectsTile
{
    AssignedToMe,
    InProgress,
    NewThisWeek,
    UpdatedThisWeek,
    Blocked,
    Overdue,
    Stale,
    GitHubIssues,
}

/// <summary>
/// The tile picked on the Projects strip, if any. Clicking a tile narrows the
/// queries and items lists to the set it counts; clicking it again, or Clear,
/// removes the filter. One tile at a time, shared by the strip and the page.
/// </summary>
public sealed class ProjectsTileFilter
{
    /// <summary>The one the app uses; the strip and the Projects page both read it.</summary>
    public static ProjectsTileFilter Shared { get; } = new();

    public ProjectsTile? Selected { get; private set; }

    public bool IsActive => Selected != null;

    /// <summary>Raised whenever <see cref="Selected"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Pick <paramref name="tile"/>, or drop it when it is already picked.</summary>
    public void Toggle(ProjectsTile tile) => Set(Selected == tile ? null : tile);

    public void Clear() => Set(null);

    private void Set(ProjectsTile? tile)
    {
        if (Selected == tile) return;
        Selected = tile;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static string Title(ProjectsTile tile) => tile switch
    {
        ProjectsTile.AssignedToMe => "Assigned to Me",
        ProjectsTile.InProgress => "In Progress",
        ProjectsTile.NewThisWeek => "New This Week",
        ProjectsTile.UpdatedThisWeek => "Updated This Week",
        ProjectsTile.Blocked => "Blocked",
        ProjectsTile.Overdue => "Overdue",
        ProjectsTile.Stale => $"No Change in {WorkItemStats.StaleDays} Days",
        ProjectsTile.GitHubIssues => "GitHub Issues",
        _ => tile.ToString(),
    };

    /// <summary>
    /// The Azure DevOps ids a work-item tile counts, from the user's open
    /// items — the same items and rules the tile's figure uses. Empty for
    /// <see cref="ProjectsTile.GitHubIssues"/>, which selects by provider instead.
    /// </summary>
    public static HashSet<int> Ids(IEnumerable<WorkItem> mine, ProjectsTile tile, DateTime nowUtc) =>
        tile == ProjectsTile.GitHubIssues
            ? new HashSet<int>()
            : mine.Where(w => WorkItemStats.Matches(w, tile, nowUtc)).Select(w => w.Id).ToHashSet();

    /// <summary>
    /// Whether a list row belongs to the picked tile's set. With nothing
    /// picked every row passes. GitHub Issues keeps GitHub rows; every other
    /// tile keeps the Azure DevOps rows whose id is in <paramref name="ids"/>.
    /// </summary>
    public static bool Passes(UnifiedTask task, ProjectsTile? tile, IReadOnlySet<int> ids)
    {
        if (tile is not { } t) return true;
        if (t == ProjectsTile.GitHubIssues) return task.Provider == "github";
        return task.Provider == "azdevops" && int.TryParse(task.Id, out var id) && ids.Contains(id);
    }
}
