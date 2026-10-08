using FleetMate.Core.Services.Projects;

namespace FleetMate.Core.Models.Projects;

/// <summary>
/// Values a New Work Item form starts from. Create New Alike fills it from the
/// card it was opened on (macOS BoardsView createAlikeSource).
/// </summary>
public sealed record WorkItemPrefill(
    string? Type = null,
    string? AssignedTo = null,
    int? Priority = null,
    string? AreaPath = null,
    string? IterationPath = null,
    IReadOnlyList<string>? Tags = null,
    string? Description = null)
{
    /// <summary>The fields Create New Alike carries over from a work item card.</summary>
    public static WorkItemPrefill FromTask(UnifiedTask task) => new(
        Type: NonEmpty(task.Metadata.GetValueOrDefault("workItemType")),
        AssignedTo: NonEmpty(task.Assignees.FirstOrDefault()),
        Priority: task.Priority,
        AreaPath: NonEmpty(task.Metadata.GetValueOrDefault("areaPath")),
        IterationPath: NonEmpty(task.Metadata.GetValueOrDefault("iterationPath")) ?? NonEmpty(task.Bucket),
        Tags: task.Labels.Count > 0 ? task.Labels.ToList() : null,
        Description: NonEmpty(task.Description));

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>Pure rules behind the New Work Item and New Issue forms.</summary>
public static class NewItemForm
{
    /// <summary>
    /// The picker is keyed by uniqueName, but Create New Alike carries the
    /// source's assignee by display name; translate it so the assignee carries
    /// over instead of coming up empty. Anything unmatched is returned as is.
    /// </summary>
    public static string ResolveAssignee(string? assignedTo, IEnumerable<DevOpsMember> members)
    {
        if (string.IsNullOrWhiteSpace(assignedTo)) return "";
        var list = members.ToList();
        if (list.Any(m => string.Equals(m.UniqueName, assignedTo, StringComparison.OrdinalIgnoreCase)))
            return list.First(m => string.Equals(m.UniqueName, assignedTo, StringComparison.OrdinalIgnoreCase)).UniqueName;
        var byName = list.FirstOrDefault(m => string.Equals(m.DisplayName, assignedTo, StringComparison.OrdinalIgnoreCase));
        return byName?.UniqueName ?? assignedTo;
    }

    /// <summary>Tags typed with commas or semicolons, trimmed, without case-insensitive repeats.</summary>
    public static List<string> SplitTags(string? text)
    {
        var tags = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return tags;
        foreach (var part in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!tags.Contains(part, StringComparer.OrdinalIgnoreCase)) tags.Add(part);
        return tags;
    }

    /// <summary>
    /// The type to select once the allowed list is known: the prefill when the
    /// board allows it, else the current choice when still allowed, else the first.
    /// </summary>
    public static string PickType(IReadOnlyList<string> allowed, string? prefill, string? current)
    {
        if (allowed.Count == 0) return "";
        string? Match(string? value) =>
            value == null ? null : allowed.FirstOrDefault(t => string.Equals(t, value, StringComparison.OrdinalIgnoreCase));
        return Match(prefill) ?? Match(current) ?? allowed[0];
    }
}
