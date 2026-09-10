using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FleetMate.Core.Models.Projects;

namespace FleetMate.GUI.Views.Projects;

/// <summary>
/// The Projects List view: every Shared Query rendered like the Azure DevOps
/// query results grid — expanded rows with tree indentation — sectioned by
/// area-path bucket. The macOS QueriesListView, ported.
/// </summary>
public partial class QueriesListControl : UserControl
{
    /// <summary>A query with its materialized rows, ready to render.</summary>
    public record QueryRunDisplay(AdoSharedQuery Query, List<QueryRowDisplay> Rows, bool Truncated, string AreaBucket);

    public record QueryRowDisplay(UnifiedTask Task, int Depth, bool HasChildren);

    public event EventHandler<UnifiedTask>? TaskSelected;
    public event EventHandler<AdoSharedQuery>? OpenQueryRequested;

    /// <summary>Collapse state survives tab switches, not restarts.</summary>
    private static readonly HashSet<string> CollapsedQueryIds = new();

    private List<QueryRunDisplay> _runs = new();
    private string _search = "";
    private bool _showClosed;

    public QueriesListControl()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Dominant area-path bucket: the second path component when present
    /// ("Projects\Devices\Macintosh" → "Devices"), else the project itself.
    /// Empty queries fall back to the "Bucket - Query name" convention.
    /// </summary>
    public static string AreaBucket(List<QueryRowDisplay> rows, string queryName)
    {
        var counts = new Dictionary<string, int>();
        foreach (var row in rows)
        {
            if (!row.Task.Metadata.TryGetValue("areaPath", out var area) || area.Length == 0) continue;
            var comps = area.Split('\\');
            var bucket = comps.Length >= 2 ? comps[1] : comps[0];
            counts[bucket] = counts.GetValueOrDefault(bucket) + 1;
        }
        if (counts.Count > 0)
            return counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).First().Key;
        foreach (var separator in new[] { " - ", " — " })
        {
            var idx = queryName.IndexOf(separator, StringComparison.Ordinal);
            if (idx > 0) return queryName[..idx];
        }
        return "General";
    }

    public void ShowRuns(List<QueryRunDisplay> runs, string search, bool showClosed)
    {
        _runs = runs;
        _search = search.Trim();
        _showClosed = showClosed;
        Rebuild();
    }

    public void ShowMessage(string message)
    {
        SectionsPanel.Children.Clear();
        SectionsPanel.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 13,
            Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 48, 0, 0)
        });
    }

    // ── Rendering ────────────────────────────────────────────────────────

    private void Rebuild()
    {
        SectionsPanel.Children.Clear();

        // Buckets A→Z with "General" last; queries A→Z inside each bucket.
        var sections = _runs
            .GroupBy(r => r.AreaBucket)
            .OrderBy(g => g.Key == "General" ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        var any = false;
        foreach (var section in sections)
        {
            var header = new TextBlock
            {
                Text = section.Key,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(12, 18, 0, 6)
            };
            SectionsPanel.Children.Add(header);

            foreach (var run in section.OrderBy(r => r.Query.Name, StringComparer.OrdinalIgnoreCase))
            {
                SectionsPanel.Children.Add(BuildQuery(run));
                any = true;
            }
        }

        if (!any)
        {
            ShowMessage(_search.Length > 0 ? $"No rows match \"{_search}\"." : "No shared queries.");
        }
    }

    private UIElement BuildQuery(QueryRunDisplay run)
    {
        var visible = run.Rows.Where(RowVisible).ToList();

        var expander = new Expander
        {
            IsExpanded = !CollapsedQueryIds.Contains(run.Query.Id),
            Margin = new Thickness(0, 0, 0, 2)
        };
        expander.Expanded += (_, _) => CollapsedQueryIds.Remove(run.Query.Id);
        expander.Collapsed += (_, _) => CollapsedQueryIds.Add(run.Query.Id);

        var headerPanel = new DockPanel { LastChildFill = true };
        var openButton = new Button
        {
            Content = "",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 11,
            Width = 26,
            Height = 24,
            Padding = new Thickness(0),
            ToolTip = "Open query in Azure DevOps",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        openButton.Click += (_, e) =>
        {
            e.Handled = true;
            OpenQueryRequested?.Invoke(this, run.Query);
        };
        DockPanel.SetDock(openButton, Dock.Right);
        headerPanel.Children.Add(openButton);

        var count = new TextBlock
        {
            Text = run.Truncated ? $"{visible.Count}+" : visible.Count.ToString(),
            FontSize = 11,
            Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0)
        };
        DockPanel.SetDock(count, Dock.Right);
        headerPanel.Children.Add(count);

        var name = new TextBlock
        {
            Text = run.Query.FolderPath.Length > 0 ? $"{run.Query.FolderPath} / {run.Query.Name}" : run.Query.Name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        headerPanel.Children.Add(name);
        expander.Header = headerPanel;

        var rowsPanel = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
        if (visible.Count == 0)
        {
            rowsPanel.Children.Add(new TextBlock
            {
                Text = "No matching items.",
                FontSize = 12,
                Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
                Margin = new Thickness(28, 2, 0, 4)
            });
        }
        else
        {
            foreach (var row in visible)
                rowsPanel.Children.Add(BuildRow(row));
        }
        expander.Content = rowsPanel;
        return expander;
    }

    private bool RowVisible(QueryRowDisplay row)
    {
        if (!_showClosed && row.Task.State == TaskState.Closed) return false;
        if (_search.Length == 0) return true;
        return row.Task.Id.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || row.Task.Title.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || row.Task.Assignees.Any(a => a.Contains(_search, StringComparison.OrdinalIgnoreCase))
            || row.Task.Labels.Any(l => l.Contains(_search, StringComparison.OrdinalIgnoreCase));
    }

    private UIElement BuildRow(QueryRowDisplay row)
    {
        var border = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(12, 4, 4, 4),
            Cursor = Cursors.Hand,
            Tag = row.Task
        };
        border.MouseEnter += (_, _) => border.Background = (Brush)FindResource("SubtleFillBrush");
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        border.MouseLeftButtonUp += (_, _) => TaskSelected?.Invoke(this, row.Task);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });

        var id = new TextBlock
        {
            Text = row.Task.Id,
            FontSize = 12,
            Foreground = (Brush)FindResource("SystemControlForegroundAccentBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(id, 0);
        grid.Children.Add(id);

        var titlePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(row.Depth * 18, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (row.Depth > 0)
        {
            titlePanel.Children.Add(new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 8,
                Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            });
        }
        titlePanel.Children.Add(new TextBlock
        {
            Text = row.Task.Title,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(titlePanel, 1);
        grid.Children.Add(titlePanel);

        var state = row.Task.Metadata.TryGetValue("state", out var s) && s.Length > 0 ? s : row.Task.State.ToString();
        var stateBlock = new TextBlock
        {
            Text = state,
            FontSize = 11,
            Foreground = StateBrush(state),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(stateBlock, 2);
        grid.Children.Add(stateBlock);

        var tags = new TextBlock
        {
            Text = string.Join(", ", row.Task.Labels),
            FontSize = 11,
            Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(tags, 3);
        grid.Children.Add(tags);

        var assignee = new TextBlock
        {
            Text = row.Task.Assignees.FirstOrDefault() ?? "",
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(assignee, 4);
        grid.Children.Add(assignee);

        var changed = new TextBlock
        {
            Text = FormatRelative(row.Task.UpdatedAt),
            FontSize = 11,
            Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(changed, 5);
        grid.Children.Add(changed);

        border.Child = grid;
        return border;
    }

    private Brush StateBrush(string state) => state.ToLowerInvariant() switch
    {
        "new" or "to do" or "proposed" => new SolidColorBrush(Color.FromRgb(0x27, 0xae, 0x60)),
        "active" or "in progress" or "doing" or "committed" => new SolidColorBrush(Color.FromRgb(0x31, 0x82, 0xCE)),
        "closed" or "done" or "resolved" or "completed" or "removed" => new SolidColorBrush(Color.FromRgb(0x8b, 0x5c, 0xf6)),
        _ => (Brush)FindResource("SystemControlForegroundBaseHighBrush")
    };

    private static string FormatRelative(DateTime when)
    {
        if (when <= DateTime.MinValue) return "";
        var span = DateTime.UtcNow - when.ToUniversalTime();
        return span.TotalMinutes < 60 ? $"{Math.Max(1, (int)span.TotalMinutes)}m"
            : span.TotalHours < 24 ? $"{(int)span.TotalHours}h"
            : span.TotalDays < 30 ? $"{(int)span.TotalDays}d"
            : when.ToLocalTime().ToString("MMM d");
    }
}
