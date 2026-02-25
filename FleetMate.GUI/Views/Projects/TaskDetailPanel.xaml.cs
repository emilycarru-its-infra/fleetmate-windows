using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects.Tasks;
using FleetMate.GUI.Views.Shared;
using Serilog;

namespace FleetMate.GUI.Views.Projects;

/// <summary>
/// Unified task detail sidebar that routes to provider-specific content.
/// </summary>
public partial class TaskDetailPanel : UserControl
{
    private UnifiedTask? _task;
    private ITaskProvider? _provider;

    public event EventHandler? CloseRequested;
    public event EventHandler? TaskUpdated;

    public TaskDetailPanel()
    {
        InitializeComponent();
    }

    public void ShowTask(UnifiedTask task, ITaskProvider? provider)
    {
        _task = task;
        _provider = provider;

        ProviderBadge.Text = task.Provider.ToUpperInvariant();
        TaskTitle.Text = task.Title;
        TaskId.Text = $"#{task.Id}";

        ContentPanel.Children.Clear();

        switch (task.Provider.ToLowerInvariant())
        {
            case "azdevops":
                RenderAzDoDetail(task);
                break;
            case "github":
                RenderGitHubDetail(task);
                break;
            case "gitea":
                RenderGiteaDetail(task);
                break;
            default:
                RenderGenericDetail(task);
                break;
        }
    }

    // ── Helpers to extract provider-specific fields from ProviderData ────

    private static string GetProviderField(UnifiedTask task, string fieldName, string fallback = "-")
    {
        if (task.ProviderData is JsonElement data && data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty(fieldName, out var val) && val.ValueKind == JsonValueKind.String)
                return val.GetString() ?? fallback;
        }
        return fallback;
    }

    private static string StateToString(TaskState state) => state switch
    {
        TaskState.Open => "Open",
        TaskState.InProgress => "In Progress",
        TaskState.Closed => "Closed",
        _ => state.ToString()
    };

    private string AssigneesDisplay(UnifiedTask task) =>
        task.Assignees.Count > 0 ? string.Join(", ", task.Assignees) : "(unassigned)";

    // ── Azure DevOps Detail ──────────────────────────────────────────────

    private void RenderAzDoDetail(UnifiedTask task)
    {
        AddSection("Status");
        AddStateBadge(task.State);
        AddMetadataRow("Type", GetProviderField(task, "WorkItemType"));
        AddMetadataRow("Priority", task.Priority?.ToString() ?? "-");

        AddSection("Assignment");
        AddMetadataRow("Assigned To", AssigneesDisplay(task));
        AddMetadataRow("Iteration", GetProviderField(task, "IterationPath"));
        AddMetadataRow("Area", GetProviderField(task, "AreaPath"));

        if (task.Labels.Count > 0)
        {
            AddSection("Tags");
            AddTagsPanel(task.Labels);
        }

        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            AddSection("Description");
            var viewer = new MarkdownViewer { MarkdownText = task.Description, MinHeight = 100, MaxHeight = 300 };
            ContentPanel.Children.Add(viewer);
        }

        AddSection("Dates");
        AddMetadataRow("Created", task.CreatedAt.ToString("yyyy-MM-dd HH:mm"));
        AddMetadataRow("Modified", task.UpdatedAt.ToString("yyyy-MM-dd HH:mm"));

        if (!string.IsNullOrWhiteSpace(task.ExternalUrl))
        {
            AddSection("Actions");
            AddOpenInBrowserButton(task.ExternalUrl);
        }

        AddEditSection(task);
    }

    // ── GitHub Detail ────────────────────────────────────────────────────

    private void RenderGitHubDetail(UnifiedTask task)
    {
        AddSection("Status");
        AddStateBadge(task.State);
        AddMetadataRow("Number", $"#{task.Id}");

        AddSection("Assignment");
        AddMetadataRow("Assigned To", AssigneesDisplay(task));

        if (task.Labels.Count > 0)
        {
            AddSection("Labels");
            AddTagsPanel(task.Labels);
        }

        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            AddSection("Body");
            var viewer = new MarkdownViewer { MarkdownText = task.Description, MinHeight = 100, MaxHeight = 300 };
            ContentPanel.Children.Add(viewer);
        }

        AddSection("Dates");
        AddMetadataRow("Created", task.CreatedAt.ToString("yyyy-MM-dd HH:mm"));
        AddMetadataRow("Updated", task.UpdatedAt.ToString("yyyy-MM-dd HH:mm"));

        if (!string.IsNullOrWhiteSpace(task.ExternalUrl))
        {
            AddSection("Actions");
            AddOpenInBrowserButton(task.ExternalUrl);
        }

        AddEditSection(task);
    }

    // ── Gitea Detail ─────────────────────────────────────────────────────

    private void RenderGiteaDetail(UnifiedTask task)
    {
        AddSection("Status");
        AddStateBadge(task.State);
        AddMetadataRow("Number", $"#{task.Id}");

        AddSection("Assignment");
        AddMetadataRow("Assigned To", AssigneesDisplay(task));

        if (task.Labels.Count > 0)
        {
            AddSection("Labels");
            AddTagsPanel(task.Labels);
        }

        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            AddSection("Body");
            var viewer = new MarkdownViewer { MarkdownText = task.Description, MinHeight = 100, MaxHeight = 300 };
            ContentPanel.Children.Add(viewer);
        }

        AddSection("Dates");
        AddMetadataRow("Created", task.CreatedAt.ToString("yyyy-MM-dd HH:mm"));
        AddMetadataRow("Updated", task.UpdatedAt.ToString("yyyy-MM-dd HH:mm"));

        if (!string.IsNullOrWhiteSpace(task.ExternalUrl))
        {
            AddSection("Actions");
            AddOpenInBrowserButton(task.ExternalUrl);
            ContentPanel.Children.Add(new TextBlock
            {
                Text = "Open in Gitea to edit",
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
                Margin = new Thickness(0, 4, 0, 0)
            });
        }
    }

    // ── Generic Detail ───────────────────────────────────────────────────

    private void RenderGenericDetail(UnifiedTask task)
    {
        AddSection("Status");
        AddStateBadge(task.State);
        AddMetadataRow("Priority", task.Priority?.ToString() ?? "-");
        AddMetadataRow("Assigned To", AssigneesDisplay(task));

        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            AddSection("Description");
            ContentPanel.Children.Add(new TextBlock
            {
                Text = task.Description,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
        }

        AddSection("Dates");
        AddMetadataRow("Created", task.CreatedAt.ToString("yyyy-MM-dd HH:mm"));
        AddMetadataRow("Updated", task.UpdatedAt.ToString("yyyy-MM-dd HH:mm"));
    }

    // ── Edit Section ─────────────────────────────────────────────────────

    private void AddEditSection(UnifiedTask task)
    {
        if (_provider == null) return;

        AddSection("Quick Edit");

        var statePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var stateCombo = new ComboBox { MinWidth = 140, Margin = new Thickness(0, 0, 8, 0) };

        foreach (var s in Enum.GetValues<TaskState>())
            stateCombo.Items.Add(s);

        stateCombo.SelectedItem = task.State;
        statePanel.Children.Add(stateCombo);

        var updateBtn = new Button { Content = "Update State", MinWidth = 80 };
        updateBtn.Click += async (_, _) =>
        {
            if (stateCombo.SelectedItem is not TaskState newState || newState == task.State) return;

            updateBtn.IsEnabled = false;
            try
            {
                await _provider.UpdateTaskAsync(task.Id, new UpdateTaskRequest { State = newState });
                task.State = newState;
                TaskUpdated?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to update task state");
            }
            finally
            {
                updateBtn.IsEnabled = true;
            }
        };
        statePanel.Children.Add(updateBtn);
        ContentPanel.Children.Add(statePanel);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void AddSection(string title)
    {
        ContentPanel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 12, 0, 4)
        });
    }

    private void AddMetadataRow(string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
            FontSize = 12
        };
        Grid.SetColumn(labelBlock, 0);

        var valueBlock = new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(valueBlock, 1);

        grid.Children.Add(labelBlock);
        grid.Children.Add(valueBlock);
        ContentPanel.Children.Add(grid);
    }

    private void AddStateBadge(TaskState state)
    {
        var color = state switch
        {
            TaskState.Open => "#27ae60",
            TaskState.InProgress => "#3182CE",
            TaskState.Closed => "#8b5cf6",
            _ => "#666"
        };

        var border = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 4)
        };
        border.Child = new TextBlock
        {
            Text = StateToString(state),
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        };
        ContentPanel.Children.Add(border);
    }

    private void AddTagsPanel(List<string> tags)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var tag in tags)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(30, 77, 166, 255)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 4, 4)
            };
            border.Child = new TextBlock { Text = tag, FontSize = 11 };
            panel.Children.Add(border);
        }
        ContentPanel.Children.Add(panel);
    }

    private void AddOpenInBrowserButton(string url)
    {
        var btn = new Button { Content = "Open in Browser", MinWidth = 120, Margin = new Thickness(0, 0, 0, 8) };
        btn.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to open URL");
            }
        };
        ContentPanel.Children.Add(btn);
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
