namespace FleetMate.Core.Services.Terminal;

/// <summary>What the terminal's session list knows about one session for filtering, searching and sorting.</summary>
/// <param name="IsAgent">An agent CLI, not a bare shell, is running.</param>
/// <param name="IsRunning">Output is streaming.</param>
/// <param name="IsPaused">An agent is waiting on the person.</param>
/// <param name="Searchable">Name, folder, section and program, matched by the search box.</param>
/// <param name="LastActivity">When the session last printed or rang its bell.</param>
public sealed record TerminalSessionTraits(
    bool IsAgent, bool IsRunning, bool IsPaused,
    IReadOnlyList<string> Searchable, DateTime? LastActivity)
{
    /// <summary>Paused: the agent rang its bell out of sight, or sits idle at its prompt.</summary>
    public static bool Paused(ActivityState state, bool isAgent) =>
        state == ActivityState.Attention || (state == ActivityState.Idle && isAgent);
}

/// <summary>How the session list is ordered.</summary>
public enum TerminalSessionSort { Manual, RecentFirst, RecentLast }

public static class TerminalSessionSortNames
{
    public static string Title(this TerminalSessionSort sort) => sort switch
    {
        TerminalSessionSort.RecentFirst => "Recently Active on Top",
        TerminalSessionSort.RecentLast => "Recently Active at Bottom",
        _ => "By Section",
    };

    /// <summary>The stored value, the same strings the macOS app keeps.</summary>
    public static string Key(this TerminalSessionSort sort) => sort switch
    {
        TerminalSessionSort.RecentFirst => "recentFirst",
        TerminalSessionSort.RecentLast => "recentLast",
        _ => "manual",
    };

    public static TerminalSessionSort Parse(string? value) => value switch
    {
        "recentFirst" => TerminalSessionSort.RecentFirst,
        "recentLast" => TerminalSessionSort.RecentLast,
        _ => TerminalSessionSort.Manual,
    };
}

/// <summary>
/// The session list's status toggles and search. Toggles that are on and the
/// search combine as an intersection, so a search can be narrowed to its
/// running or paused matches.
/// </summary>
public sealed record TerminalSessionFilter(bool Active = false, bool Running = false, bool Paused = false, string Query = "")
{
    public string TrimmedQuery => (Query ?? "").Trim();

    /// <summary>Any toggle is on or the search has text.</summary>
    public bool IsFiltering => Active || Running || Paused || TrimmedQuery.Length > 0;

    public bool Matches(TerminalSessionTraits traits)
    {
        if (Active && !traits.IsAgent) return false;
        if (Running && !traits.IsRunning) return false;
        if (Paused && !traits.IsPaused) return false;
        var words = TrimmedQuery.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // Every word must appear somewhere, so "fleet main" finds a session
        // named fleet in a folder called main.
        return words.All(word => traits.Searchable.Any(text =>
            text?.Contains(word, StringComparison.CurrentCultureIgnoreCase) == true));
    }

    /// <summary>
    /// The indices of <paramref name="items"/> in <paramref name="sort"/> order.
    /// Ties keep list order; sessions never active sit at the far end from the
    /// recent ones.
    /// </summary>
    public static IReadOnlyList<int> Order(IReadOnlyList<TerminalSessionTraits> items, TerminalSessionSort sort)
    {
        var indices = Enumerable.Range(0, items.Count).ToList();
        if (sort == TerminalSessionSort.Manual) return indices;
        var recentFirst = indices
            .OrderBy(i => items[i].LastActivity.HasValue ? 0 : 1)
            .ThenByDescending(i => items[i].LastActivity ?? DateTime.MinValue)
            .ThenBy(i => i)
            .ToList();
        if (sort == TerminalSessionSort.RecentLast) recentFirst.Reverse();
        return recentFirst;
    }
}
