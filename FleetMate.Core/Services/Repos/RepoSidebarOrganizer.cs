namespace FleetMate.Core.Services.Repos;

/// <summary>How the Repos sidebar orders repositories within a group, or the whole list when it is not grouped.</summary>
public enum RepoSidebarSort
{
    Name,
    /// <summary>Most recent commit on HEAD first.</summary>
    RecentlyChanged,
    /// <summary>Most uncommitted paths first.</summary>
    MostChanges,
    /// <summary>Furthest behind its upstream first.</summary>
    MostBehind,
}

public static class RepoSidebarSortExtensions
{
    public static string Title(this RepoSidebarSort sort) => sort switch
    {
        RepoSidebarSort.RecentlyChanged => "Recently Changed",
        RepoSidebarSort.MostChanges => "Most Changes",
        RepoSidebarSort.MostBehind => "Most Behind",
        _ => "Name",
    };
}

/// <summary>
/// One host in the sidebar — Azure DevOps, GitHub, or another — holding its
/// projects or owners, the way checkouts are laid out on disk.
/// </summary>
public sealed record RepoSidebarSection(RepoProvider Provider, IReadOnlyList<RepoRecordGroup> Groups)
{
    public string Id => Provider.Code();
    public string Title => Provider.Title();
    public int RepositoryCount => Groups.Sum(g => g.Records.Count);
}

/// <summary>
/// Builds the Repos sidebar: filter, group by host ▸ project/owner, sort.
/// Pure, so the ordering rules are tested without a view.
/// </summary>
public static class RepoSidebarOrganizer
{
    /// <summary>
    /// Host sections, each with its projects or owners alphabetically, and
    /// repositories inside each ordered by <paramref name="sort"/>. Empty groups are dropped.
    /// </summary>
    public static List<RepoSidebarSection> Sections(IEnumerable<RepoRecord> records,
        IReadOnlyDictionary<string, RepoStatus> statuses, RepoSidebarSort sort, string? query = null)
    {
        var sections = new List<RepoSidebarSection>();
        foreach (var group in RepoRecordGroup.Groups(records, query))
        {
            var sorted = group with { Records = Sorted(group.Records, statuses, sort) };
            if (sections.Count > 0 && sections[^1].Provider == group.Provider)
                sections[^1] = sections[^1] with { Groups = sections[^1].Groups.Append(sorted).ToList() };
            else
                sections.Add(new RepoSidebarSection(group.Provider, new[] { sorted }));
        }
        return sections;
    }

    /// <summary>Every match in one list ordered by <paramref name="sort"/>, for the ungrouped sidebar.</summary>
    public static List<RepoRecord> Flat(IEnumerable<RepoRecord> records,
        IReadOnlyDictionary<string, RepoStatus> statuses, RepoSidebarSort sort, string? query = null) =>
        Sorted(RepoRecordGroup.Groups(records, query).SelectMany(g => g.Records).ToList(), statuses, sort);

    /// <summary>
    /// Orders by the chosen key, falling back to the name so the order is
    /// stable while statuses load.
    /// </summary>
    public static List<RepoRecord> Sorted(IReadOnlyList<RepoRecord> records,
        IReadOnlyDictionary<string, RepoStatus> statuses, RepoSidebarSort sort)
    {
        RepoStatus? S(RepoRecord r) => statuses.GetValueOrDefault(r.Id);
        var ordered = sort switch
        {
            RepoSidebarSort.RecentlyChanged => records.OrderByDescending(r => S(r)?.LastCommitAt ?? DateTimeOffset.MinValue),
            RepoSidebarSort.MostChanges => records.OrderByDescending(r => S(r)?.ChangedCount ?? 0),
            RepoSidebarSort.MostBehind => records.OrderByDescending(r => S(r)?.Behind ?? 0),
            _ => records.OrderBy(_ => 0),
        };
        return ordered
            .ThenBy(r => r.Key.Name, NaturalComparer.Instance)
            .ThenBy(r => r.Key.DisplayName, StringComparer.Ordinal)
            .ToList();
    }
}
