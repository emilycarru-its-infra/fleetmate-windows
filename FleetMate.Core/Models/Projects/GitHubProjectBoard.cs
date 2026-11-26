namespace FleetMate.Core.Models.Projects;

/// <summary>One column of a GitHub Project board: a Status option and its items.</summary>
public sealed record GitHubProjectColumn(string Name, string ColorHex, IReadOnlyList<GitHubProjectItem> Items);

/// <summary>
/// The Board view's GitHub Project source, shared by both Windows apps: a
/// column per Status option in the project's order, then "(No Status)" when
/// anything has none, filtered by the search text.
/// </summary>
public static class GitHubProjectBoard
{
    public const string NoStatus = "(No Status)";

    public static string Title(GitHubProjectItem item) =>
        item.Content?.Title is { Length: > 0 } title ? title
        : item.DraftContent?.Title is { Length: > 0 } draft ? draft
        : "(untitled)";

    public static List<GitHubProjectColumn> Columns(IEnumerable<GitHubProjectItem> items, GitHubProjectField? status, string? search)
    {
        var filtered = items;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            filtered = filtered.Where(i =>
                (i.Content?.Title?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (i.DraftContent?.Title?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var columns = (status?.Options ?? new()).Select(o => (o.Name, Items: new List<GitHubProjectItem>())).ToList();
        var none = new List<GitHubProjectItem>();
        foreach (var item in filtered)
        {
            var value = StatusOf(item);
            var column = columns.FirstOrDefault(c => c.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
            (column.Items ?? none).Add(item);
        }
        var result = columns.Select(c => new GitHubProjectColumn(c.Name, StatusColorHex(c.Name), c.Items)).ToList();
        if (none.Count > 0) result.Add(new GitHubProjectColumn(NoStatus, "#718096", none));
        return result;
    }

    public static string? StatusOf(GitHubProjectItem item) => item.FieldValues
        .FirstOrDefault(v => v.FieldName.Equals("Status", StringComparison.OrdinalIgnoreCase))?.SingleSelectValue;

    public static string StatusColorHex(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("done") || lower.Contains("closed")) return "#38A169";
        if (lower.Contains("progress") || lower.Contains("active")) return "#3182CE";
        if (lower.Contains("review")) return "#805AD5";
        if (lower.Contains("backlog")) return "#A0AEC0";
        if (lower.Contains("todo")) return "#ED8936";
        return "#D69E2E";
    }

    /// <summary>The kind of item a card shows: Issue, Pull Request or Draft.</summary>
    public static string Kind(GitHubProjectItem item) => item.Type switch
    {
        "ISSUE" => "Issue",
        "PULL_REQUEST" => "Pull Request",
        "DRAFT_ISSUE" => "Draft",
        _ => "Item",
    };

    /// <summary>"12 items across 4 columns".</summary>
    public static string Summary(IReadOnlyCollection<GitHubProjectColumn> columns) =>
        $"{columns.Sum(c => c.Items.Count)} items across {columns.Count} columns";
}
