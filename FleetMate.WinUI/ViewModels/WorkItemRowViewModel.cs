using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using FleetMate.Core.Models.Projects;

namespace FleetMate.WinUI.ViewModels;

/// <summary>
/// Row display model for the Projects (Azure DevOps work items) list. Wraps a
/// <see cref="WorkItem"/> with formatted columns + a state colour.
/// </summary>
public sealed class WorkItemRowViewModel
{
    public WorkItem Item { get; }

    public WorkItemRowViewModel(WorkItem item) => Item = item;

    public string IdText => $"#{Item.Id}";
    public string Title => string.IsNullOrEmpty(Item.Fields.Title) ? "(untitled)" : Item.Fields.Title!;
    public string Type => Item.Fields.WorkItemType ?? "—";
    public string State => Item.Fields.State ?? "—";
    public string Assignee => Item.Fields.AssignedTo?.DisplayName ?? "Unassigned";
    public string Changed => Item.Fields.ChangedDate?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";

    public Brush StateBrush => new SolidColorBrush(State.ToLowerInvariant() switch
    {
        "new" or "proposed" or "planned" or "to do" => Colors.Gold,
        "active" or "doing" or "in progress" or "committed" or "open" => Colors.DodgerBlue,
        "resolved" => Colors.Orange,
        "closed" or "done" or "completed" => Colors.Green,
        "removed" => Colors.Gray,
        _ => Colors.Gray,
    });

    public bool Matches(string q) =>
        Title.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Type.Contains(q, StringComparison.OrdinalIgnoreCase)
        || State.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Assignee.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Item.Id.ToString().Contains(q, StringComparison.OrdinalIgnoreCase);
}
