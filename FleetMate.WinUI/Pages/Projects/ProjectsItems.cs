using FleetMate.Core.Development;
using FleetMate.Core.Models.Projects;
using FleetMate.WinUI.Shell.Widgets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FleetMate.WinUI.Pages.Projects;

/// <summary>A board card, or a Mine or Recent row, for one task.</summary>
public sealed class TaskCardItem
{
    public TaskCardItem(UnifiedTask task) => Task = task;

    public UnifiedTask Task { get; }

    public string Title => Task.Title;
    public string Key => Task.CompositeKey;
    public string ProviderName => TaskCardText.ProviderName(Task.Provider);
    public string TypeName => TaskCardText.TypeName(Task);
    public string AreaPath => TaskCardText.AreaPath(Task);
    public string Iteration => TaskCardText.Iteration(Task);
    public string Assignees => TaskCardText.Assignees(Task);
    public string Priority => TaskCardText.Priority(Task);
    public string RowDetail => TaskCardText.RowDetail(Task);
    public string Updated => TaskCardText.Relative(Task.UpdatedAt, DateTime.UtcNow);
    public string SpokenName => TaskCardText.Description(Task);

    public Visibility TypeVisibility => Show(TypeName);
    public Visibility AreaVisibility => Show(AreaPath);
    public Visibility IterationVisibility => Show(Iteration);
    public Visibility AssigneesVisibility => Show(Assignees);
    public Visibility PriorityVisibility => Show(Priority);
    public Visibility LabelsVisibility => Task.Labels.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Labels => string.Join("  ·  ", Task.Labels.Take(3));

    /// <summary>Closed work reads dimmer, as on the Mac.</summary>
    public double CardOpacity => Task.State == TaskState.Closed ? 0.7 : 1.0;

    public Brush PriorityBrush => GraphTheme.Tone(Task.Priority switch
    {
        1 => ChartTone.Pink,
        2 => ChartTone.Orange,
        3 => ChartTone.Yellow,
        _ => ChartTone.Gray,
    });

    private static Visibility Show(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>One board column: a drop target holding its cards.</summary>
public sealed class BoardColumnItem
{
    public BoardColumnItem(TaskBoardGroup group)
    {
        Title = group.Title;
        Tone = group.Tone;
        Cards = group.Tasks.Select(t => new TaskCardItem(t)).ToList();
    }

    public string Title { get; }
    public ChartTone Tone { get; }
    public List<TaskCardItem> Cards { get; }
    public Brush Dot => GraphTheme.Tone(Tone);
    public string CountLabel => Cards.Count.ToString();
    public string SpokenName => $"{Title}, {Cards.Count} {(Cards.Count == 1 ? "item" : "items")}";
    public Visibility EmptyVisibility => Cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>A GitHub Project card.</summary>
public sealed class GitHubCardItem
{
    public GitHubCardItem(GitHubProjectItem item) => Item = item;

    public GitHubProjectItem Item { get; }
    public string Title => GitHubProjectBoard.Title(Item);
    public string Kind => GitHubProjectBoard.Kind(Item);
    public string Detail => Item.Content is { } c && !string.IsNullOrEmpty(c.Repository) ? $"{c.Repository}#{c.Number}" : Kind;
    public string Assignees => Item.Content?.Assignees.Count > 0 ? string.Join(", ", Item.Content.Assignees.Take(2)) : "";
    public Visibility AssigneesVisibility => Assignees.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Labels => string.Join("  ·  ", Item.Content?.Labels.Take(3) ?? Enumerable.Empty<string>());
    public Visibility LabelsVisibility => Labels.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string SpokenName => $"{Title}, {Kind}";

    public string Glyph => Item.Type switch
    {
        "PULL_REQUEST" => "",
        "DRAFT_ISSUE" => "",
        _ => "",
    };

    public Brush KindBrush => GraphTheme.Tone(Item.Type switch
    {
        "ISSUE" => ChartTone.Green,
        "PULL_REQUEST" => ChartTone.Purple,
        _ => ChartTone.Gray,
    });
}

/// <summary>A GitHub Project column: one Status option.</summary>
public sealed class GitHubColumnItem
{
    public GitHubColumnItem(GitHubProjectColumn column)
    {
        Title = column.Name;
        Cards = column.Items.Select(i => new GitHubCardItem(i)).ToList();
        Dot = new SolidColorBrush(Hex(column.ColorHex));
    }

    public string Title { get; }
    public List<GitHubCardItem> Cards { get; }
    public Brush Dot { get; }
    public string CountLabel => Cards.Count.ToString();
    public string SpokenName => $"{Title}, {Cards.Count} {(Cards.Count == 1 ? "item" : "items")}";

    private static Windows.UI.Color Hex(string hex)
    {
        var v = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return Windows.UI.Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}

/// <summary>A row of the Queries fallback list when no stored queries are available.</summary>
public sealed class WorkItemRowItem
{
    public WorkItemRowItem(WorkItem item) => Item = item;

    public WorkItem Item { get; }
    public string Id => Item.Id.ToString();
    public string Type => Item.WorkItemType;
    public string Title => Item.Title;
    public string State => Item.State;
    public string Priority => Item.Priority?.ToString() ?? "";
    public string AssignedTo => Item.AssignedTo ?? "";
    public string SpokenName => $"#{Item.Id} {Item.Title}, {Item.State}";
}
