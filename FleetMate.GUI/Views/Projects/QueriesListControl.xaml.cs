using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Shared;
using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Projects;

/// <summary>
/// The Projects List view: every Shared Query rendered like the Azure DevOps
/// query results grid — expanded rows with tree indentation — sectioned by
/// area-path bucket. The macOS QueriesListView, ported.
/// </summary>
public partial class QueriesListControl : UserControl
{
    public event EventHandler<UnifiedTask>? TaskSelected;
    public event EventHandler<AdoSharedQuery>? OpenQueryRequested;

    /// <summary>
    /// The block "Copy for Agent" hands over for a query and its row count.
    /// The page sets it to add the project and the query's web link.
    /// </summary>
    public Func<AdoSharedQuery, int, AgentContext> QueryContext { get; set; } =
        (query, count) => AgentContexts.Query(query, null, null, count);

    /// <summary>Queries start collapsed; deliberate expansions survive reloads and tab switches, not restarts.</summary>
    private static readonly HashSet<string> ExpandedQueryIds = new();

    private List<QueryRunDisplay> _runs = new();
    private string _search = "";
    private bool _showClosed;
    private Func<UnifiedTask, bool>? _rowFilter;

    public QueriesListControl()
    {
        InitializeComponent();
        AgentContextMenu.Attach(SectionsPanel, o => o is QueryRunDisplay run ? QueryContext(run.Query, run.Rows.Count) : null);
        UpdateHeaderGlyphs();
    }

    // ── Column headers ───────────────────────────────────────────────────

    /// <summary>
    /// Title and Changed order the queries, so the headers do something
    /// visible while every query is collapsed. A second click flips the
    /// direction, as in any Windows list.
    /// </summary>
    private void OnTitleHeaderClick(object sender, RoutedEventArgs e) => ToggleHeader(QueryListOrder.Name);

    private void OnChangedHeaderClick(object sender, RoutedEventArgs e) => ToggleHeader(QueryListOrder.LastChanged);

    private void ToggleHeader(QueryListOrder order)
    {
        var ascending = UserPreferences.QueriesOrder == order
            ? !UserPreferences.QueriesOrderAscending
            : order.DefaultAscending();
        SetOrder(order, ascending);
    }

    private void UpdateHeaderGlyphs()
    {
        var order = UserPreferences.QueriesOrder;
        var glyph = UserPreferences.QueriesOrderAscending ? " \u25B2" : " \u25BC";
        TitleSortGlyph.Text = order == QueryListOrder.Name ? glyph : "";
        ChangedSortGlyph.Text = order == QueryListOrder.LastChanged ? glyph : "";
    }

    /// <summary>The current Sort Queries By order; saved, and the list redrawn.</summary>
    public void SetOrder(QueryListOrder order, bool ascending)
    {
        UserPreferences.SetQueriesOrder(order, ascending);
        UpdateHeaderGlyphs();
        Rebuild();
    }

    /// <param name="rowFilter">An extra test every row must pass, such as a Projects strip tile's set; null for none.</param>
    public void ShowRuns(List<QueryRunDisplay> runs, string search, bool showClosed, Func<UnifiedTask, bool>? rowFilter = null)
    {
        _rowFilter = rowFilter;
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

        var any = false;
        foreach (var section in QueryRuns.Ordered(_runs, UserPreferences.QueriesOrder, UserPreferences.QueriesOrderAscending,
                     r => r.Rows.Count(RowVisible)))
        {
            // Under a tile filter, a query with nothing in the tile's set drops out, and so does an emptied section.
            var runs = _rowFilter == null ? section.Runs : section.Runs.Where(r => r.Rows.Any(RowVisible)).ToList();
            if (runs.Count == 0) continue;

            var header = new TextBlock
            {
                Text = section.Key,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(12, 18, 0, 6)
            };
            SectionsPanel.Children.Add(header);

            foreach (var run in runs)
            {
                SectionsPanel.Children.Add(BuildQuery(run));
                any = true;
            }
        }

        if (!any)
        {
            ShowMessage(QueryRuns.EmptyMessage(_rowFilter != null, _search));
        }
    }

    private UIElement BuildQuery(QueryRunDisplay run)
    {
        var visible = run.Rows.Where(RowVisible).ToList();

        var expander = new Expander
        {
            IsExpanded = ExpandedQueryIds.Contains(run.Query.Id),
            Margin = new Thickness(0, 0, 0, 2)
        };
        expander.Expanded += (_, _) => ExpandedQueryIds.Add(run.Query.Id);
        expander.Collapsed += (_, _) => ExpandedQueryIds.Remove(run.Query.Id);

        // Tagged with its run, so the header offers Copy and Send to Agent for the query.
        var headerPanel = new DockPanel { LastChildFill = true, Tag = run };
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
            Text = QueryRuns.Name(run.Query),
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

    private bool RowVisible(QueryRowDisplay row) => QueryRuns.RowVisible(row, _search, _showClosed, _rowFilter);

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

        var state = QueryRuns.State(row.Task);
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

    private static string FormatRelative(DateTime when) => TaskCardText.Relative(when, DateTime.UtcNow);
}
