using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Serilog;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// The dashboard's unified work item table: DevOps work items and GitHub issues
/// in one list, switched by the dashboard's PR source filter — the macOS
/// DashboardTasksSection, ported. Rows open in the task lightbox so they can be
/// read without leaving the dashboard.
/// </summary>
public partial class DashboardTasksView : UserControl
{
    private const int RowLimit = 50;

    private static List<GitHubIssueSummary> _cachedIssues = new();
    private static DateTime _issuesLoadedAt = DateTime.MinValue;
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);

    private List<WorkItem> _workItems = new();
    private string _search = "";
    private string _sourceFilter = "devops";
    private bool _issuesLoading;

    public DashboardTasksView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadIssuesAsync();
    }

    /// <summary>Called by the dashboard whenever its work item cache updates.</summary>
    public void ShowWorkItems(List<WorkItem> items)
    {
        var closed = new HashSet<string> { "done", "closed", "removed", "completed", "resolved" };
        _workItems = items
            .Where(i => !closed.Contains((i.Fields?.State ?? "").ToLowerInvariant()))
            .OrderByDescending(i => i.Fields?.ChangedDate ?? DateTime.MinValue)
            .ToList();
        RenderRows();
    }

    /// <summary>Follows the dashboard's PR source chips: "all", "devops" or "github".</summary>
    public void SetSourceFilter(string filter)
    {
        _sourceFilter = filter;
        RenderRows();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _search = RowSearch.Text.Trim();
        RenderRows();
    }

    // ── GitHub issues ────────────────────────────────────────────────────

    private async Task LoadIssuesAsync(bool force = false)
    {
        if (_issuesLoading) return;
        if (!force && DateTime.UtcNow - _issuesLoadedAt < Freshness)
        {
            RenderRows();
            return;
        }

        var config = (Application.Current as App)?.Config?.Tasks?.Providers?.GitHub;
        if (config == null)
        {
            RenderRows();
            return;
        }

        _issuesLoading = true;
        try
        {
            using var service = new GitHubPullRequestService(config);
            _cachedIssues = await service.GetMyIssuesAsync();
            _issuesLoadedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Dashboard issues load failed");
        }
        finally
        {
            _issuesLoading = false;
        }
        RenderRows();
    }

    // ── Rendering ────────────────────────────────────────────────────────

    private sealed record TableRow(
        string Context, string Title, string TypeBadge, string StateBadge,
        DateTime UpdatedAt, UnifiedTask Task);

    private void RenderRows()
    {
        RowsPanel.Children.Clear();

        var rows = new List<TableRow>();
        if (_sourceFilter is "all" or "devops")
        {
            rows.AddRange(_workItems.Select(item => new TableRow(
                (item.Fields?.AreaPath ?? "").Replace("\\", " › "),
                item.Fields?.Title ?? "",
                item.Fields?.WorkItemType ?? "",
                item.Fields?.State ?? "",
                item.Fields?.ChangedDate ?? DateTime.MinValue,
                item.AsUnifiedTask())));
        }
        if (_sourceFilter is "all" or "github")
        {
            rows.AddRange(_cachedIssues.Select(issue => new TableRow(
                issue.Repository,
                issue.Title,
                "Issue",
                issue.State,
                issue.UpdatedAt,
                new UnifiedTask
                {
                    Id = issue.Number.ToString(),
                    Provider = "github",
                    Title = issue.Title,
                    State = issue.State == "closed" ? TaskState.Closed : TaskState.Open,
                    ExternalUrl = issue.WebUrl,
                    UpdatedAt = issue.UpdatedAt
                })));
        }

        if (_sourceFilter == "all") rows = rows.OrderByDescending(r => r.UpdatedAt).ToList();

        var visible = rows.Where(r =>
                _search.Length == 0
                || r.Task.Id.Contains(_search, StringComparison.OrdinalIgnoreCase)
                || r.Title.Contains(_search, StringComparison.OrdinalIgnoreCase)
                || r.Context.Contains(_search, StringComparison.OrdinalIgnoreCase)
                || r.TypeBadge.Contains(_search, StringComparison.OrdinalIgnoreCase))
            .ToList();

        CountText.Text = $"({visible.Count})";

        if (visible.Count == 0)
        {
            RowsPanel.Children.Add(new TextBlock
            {
                Text = _search.Length > 0 ? "No matching work items." : "No active work items.",
                FontSize = 12,
                Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
                Margin = new Thickness(12, 6, 12, 8)
            });
            return;
        }

        foreach (var row in visible.Take(RowLimit))
            RowsPanel.Children.Add(Row(row));
    }

    private void OpenLightbox(UnifiedTask task)
    {
        new TaskLightboxWindow(task, Window.GetWindow(this)).ShowDialog();
    }

    private UIElement Row(TableRow row)
    {
        var border = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(12, 4, 12, 4),
            Cursor = Cursors.Hand
        };
        border.MouseEnter += (_, _) => border.Background = (Brush)FindResource("SubtleFillBrush");
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        border.MouseLeftButtonUp += (_, _) => OpenLightbox(row.Task);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var context = new TextBlock
        {
            Text = row.Context,
            FontSize = 11,
            Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(context, 0);
        grid.Children.Add(context);

        var title = new TextBlock
        {
            Text = row.Title,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        if (row.TypeBadge.Length > 0)
        {
            var type = Badge(row.TypeBadge, (Brush)FindResource("SubtleFillBrush"),
                (Brush)FindResource("SystemControlForegroundBaseMediumBrush"));
            Grid.SetColumn(type, 2);
            grid.Children.Add(type);
        }

        if (row.StateBadge.Length > 0)
        {
            var stateColor = row.StateBadge.ToLowerInvariant() switch
            {
                "new" or "to do" or "proposed" or "open" => "#27ae60",
                "active" or "in progress" or "doing" or "committed" => "#3182CE",
                _ => "#8b5cf6"
            };
            var state = Badge(row.StateBadge,
                new SolidColorBrush((Color)ColorConverter.ConvertFromString(stateColor)), Brushes.White);
            Grid.SetColumn(state, 3);
            grid.Children.Add(state);
        }

        border.Child = grid;
        return border;
    }

    private static Border Badge(string text, Brush background, Brush foreground) => new()
    {
        Background = background,
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(7, 1, 7, 1),
        Margin = new Thickness(0, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = foreground
        }
    };
}
