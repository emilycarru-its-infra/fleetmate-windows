using FleetMate.Core.Development;
using FleetMate.Core.Links;
using FleetMate.Core.Models.Projects;
using FleetMate.WinUI.Shell.Widgets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FleetMate.WinUI.Pages.Projects;

/// <summary>
/// The selected item beside the Projects view: its title, its fields and
/// description, and Open in Browser. It shows what the board already loaded;
/// the editable work item and issue detail (FleetMate.GUI's TaskDetailPanel)
/// replaces it in the next part of the port.
/// </summary>
public sealed partial class TaskSummaryPane : UserControl
{
    public TaskSummaryPane(UnifiedTask task)
    {
        Task = task;
        AutomationProperties.SetAutomationId(this, "ProjectsTaskDetail");
        AutomationProperties.SetName(this, $"Detail: {task.Title}");

        var close = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Padding = new Thickness(8),
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            VerticalAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetName(close, "Close detail");
        ToolTipService.SetToolTip(close, "Close (Esc)");
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        close.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape });

        var id = new TextBlock
        {
            Text = task.Provider == "azdevops" ? $"{TaskCardText.TypeName(task)} {task.Id}".Trim() : $"{TaskCardText.ProviderName(task.Provider)} #{task.Id}",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = GraphTheme.Resource("TextFillColorSecondaryBrush"),
        };
        var title = new TextBlock
        {
            Text = task.Title,
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        var heading = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } },
            Children = { new StackPanel { Spacing = 2, Children = { id, title } }, close },
        };
        Grid.SetColumn(close, 1);

        var fields = new Grid { ColumnSpacing = 12, RowSpacing = 6, ColumnDefinitions = { new() { Width = GridLength.Auto }, new() { Width = new GridLength(1, GridUnitType.Star) } } };
        var state = TaskBoard.Meta(task, "state", task.State.ToString());
        AddField(fields, "State", state, GraphTheme.Tone(TaskBoard.StateTone(state)));
        AddField(fields, "Assigned To", task.Assignees.Count > 0 ? TaskCardText.Assignees(task) : "Unassigned");
        AddField(fields, "Priority", task.Priority is int p and > 0 ? TaskBoard.Priorities.First(x => x.Key == p).Label : "None");
        AddField(fields, "Area", TaskCardText.AreaPath(task));
        AddField(fields, "Iteration", TaskCardText.Iteration(task));
        AddField(fields, "Tags", string.Join(", ", task.Labels));
        AddField(fields, "Due", task.DueDate?.ToLocalTime().ToString("d MMM yyyy") ?? "");
        AddField(fields, "Changed", task.UpdatedAt > DateTime.MinValue ? task.UpdatedAt.ToLocalTime().ToString("d MMM yyyy, HH:mm") : "");

        var description = new TextBlock
        {
            Text = PullRequestPresentation.StripHtml(task.Description) is { Length: > 0 } text ? text : "No description.",
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };

        var open = new Button { Content = "Open in Browser", IsEnabled = !string.IsNullOrEmpty(task.ExternalUrl) };
        open.Click += (_, _) => { if (task.ExternalUrl is { Length: > 0 } url) ExternalBrowser.Open(url); };

        Content = new Border
        {
            Style = (Style)Application.Current.Resources["CardStyle"],
            Child = new ScrollViewer
            {
                Content = new StackPanel
                {
                    Spacing = 16,
                    Children =
                    {
                        heading,
                        fields,
                        new StackPanel { Spacing = 6, Children = { Section("Description"), description } },
                        open,
                    },
                },
            },
        };
    }

    public UnifiedTask Task { get; }

    public event EventHandler? CloseRequested;

    private static TextBlock Section(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };

    private static void AddField(Grid grid, string label, string value, Microsoft.UI.Xaml.Media.Brush? tone = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var name = new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = GraphTheme.Resource("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var text = new TextBlock
        {
            Text = value,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        if (tone != null) text.Foreground = tone;
        Grid.SetRow(name, row);
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 1);
        grid.Children.Add(name);
        grid.Children.Add(text);
    }
}
