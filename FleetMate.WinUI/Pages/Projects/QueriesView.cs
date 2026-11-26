using FleetMate.Core.Config;
using FleetMate.Core.Models.Projects;
using FleetMate.WinUI.Shell.Widgets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FleetMate.WinUI.Pages.Projects;

/// <summary>
/// The Projects Queries view: every Shared Query rendered like the Azure
/// DevOps results grid — tree-indented rows under a header per query —
/// sectioned by area-path bucket, in the Sort Queries By order. The macOS
/// QueriesListView and FleetMate.GUI's QueriesListControl; the ordering and
/// filtering are <see cref="QueryRuns"/>'.
/// </summary>
public sealed partial class QueriesView : UserControl
{
    /// <summary>Queries start collapsed; deliberate expansions survive reloads and tab switches, not restarts.</summary>
    private static readonly HashSet<string> ExpandedQueryIds = new();

    private readonly StackPanel _sections = new() { Spacing = 2 };
    private readonly TextBlock _titleGlyph = Glyph();
    private readonly TextBlock _changedGlyph = Glyph();
    private List<QueryRunDisplay> _runs = new();
    private string _search = "";
    private bool _showClosed;
    private Func<UnifiedTask, bool>? _rowFilter;
    private string? _message;

    public QueriesView()
    {
        AutomationProperties.SetAutomationId(this, "ProjectsQueries");
        var header = BuildHeader();
        var scroller = new ScrollViewer
        {
            Content = _sections,
            HorizontalScrollMode = ScrollMode.Disabled,
            Padding = new Thickness(0, 0, 12, 0),
        };
        Grid.SetRow(scroller, 1);
        Content = new Grid
        {
            RowDefinitions = { new() { Height = GridLength.Auto }, new() { Height = new GridLength(1, GridUnitType.Star) } },
            Children = { header, scroller },
        };
        UpdateHeaderGlyphs();
    }

    /// <summary>A row was picked: open it in the detail sidebar.</summary>
    public event EventHandler<UnifiedTask>? TaskSelected;

    /// <summary>A row asked for its menu; the page builds the card menu for it.</summary>
    public event EventHandler<(UnifiedTask Task, FrameworkElement Target, Windows.Foundation.Point? At)>? TaskMenuRequested;

    /// <summary>A query header's globe: open the results page.</summary>
    public event EventHandler<AdoSharedQuery>? OpenQueryRequested;

    public void ShowRuns(List<QueryRunDisplay> runs, string search, bool showClosed, Func<UnifiedTask, bool>? rowFilter)
    {
        _runs = runs;
        _search = search.Trim();
        _showClosed = showClosed;
        _rowFilter = rowFilter;
        _message = null;
        Rebuild();
    }

    public void ShowMessage(string message)
    {
        _message = message;
        _sections.Children.Clear();
        _sections.Children.Add(Message(message));
    }

    /// <summary>Expand every query, for the QA capture.</summary>
    public void ExpandAll()
    {
        foreach (var run in _runs) ExpandedQueryIds.Add(run.Query.Id);
        Rebuild();
    }

    /// <summary>The current Sort Queries By order; saved, and the list redrawn.</summary>
    public void SetOrder(QueryListOrder order, bool ascending)
    {
        UserPreferences.SetQueriesOrder(order, ascending);
        UpdateHeaderGlyphs();
        Rebuild();
    }

    // MARK: - Column headers

    private Grid BuildHeader()
    {
        var grid = Columns(new Thickness(12, 4, 16, 6));
        grid.Children.Add(HeaderText("ID", 0));
        grid.Children.Add(HeaderButton("Title", 1, _titleGlyph, QueryListOrder.Name, "Sort queries by name"));
        grid.Children.Add(HeaderText("State", 2));
        grid.Children.Add(HeaderText("Tags", 3));
        grid.Children.Add(HeaderText("Assigned To", 4));
        grid.Children.Add(HeaderButton("Changed", 5, _changedGlyph, QueryListOrder.LastChanged, "Sort queries by last change"));
        return grid;
    }

    private static Grid Columns(Thickness padding) => new()
    {
        Padding = padding,
        ColumnSpacing = 8,
        ColumnDefinitions =
        {
            new() { Width = new GridLength(64) },
            new() { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 },
            new() { Width = new GridLength(96) },
            new() { Width = new GridLength(120) },
            new() { Width = new GridLength(140) },
            new() { Width = new GridLength(72) },
        },
    };

    private static TextBlock HeaderText(string text, int column)
    {
        var block = new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = GraphTheme.Resource("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(block, column);
        return block;
    }

    /// <summary>
    /// Title and Changed order the queries, so the headers do something
    /// visible while every query is collapsed. A second click flips the
    /// direction, as in any Windows list.
    /// </summary>
    private Button HeaderButton(string text, int column, TextBlock glyph, QueryListOrder order, string help)
    {
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { HeaderText(text, 0), glyph },
            },
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(-4, 0, 0, 0),
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
        };
        AutomationProperties.SetName(button, help);
        ToolTipService.SetToolTip(button, help);
        button.Click += (_, _) => SetOrder(order,
            UserPreferences.QueriesOrder == order ? !UserPreferences.QueriesOrderAscending : order.DefaultAscending());
        Grid.SetColumn(button, column);
        return button;
    }

    private static TextBlock Glyph() => new()
    {
        FontFamily = new FontFamily("Segoe Fluent Icons"),
        FontSize = 10,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = GraphTheme.Resource("TextFillColorSecondaryBrush"),
    };

    private void UpdateHeaderGlyphs()
    {
        var order = UserPreferences.QueriesOrder;
        var glyph = UserPreferences.QueriesOrderAscending ? "" : "";
        _titleGlyph.Text = order == QueryListOrder.Name ? glyph : "";
        _changedGlyph.Text = order == QueryListOrder.LastChanged ? glyph : "";
    }

    // MARK: - Rendering

    private bool RowVisible(QueryRowDisplay row) => QueryRuns.RowVisible(row, _search, _showClosed, _rowFilter);

    private void Rebuild()
    {
        if (_message != null) return;
        _sections.Children.Clear();
        var any = false;
        foreach (var section in QueryRuns.Ordered(_runs, UserPreferences.QueriesOrder, UserPreferences.QueriesOrderAscending,
                     r => r.Rows.Count(RowVisible)))
        {
            // Under a tile filter, a query with nothing in the tile's set drops out, and so does an emptied section.
            var runs = _rowFilter == null ? section.Runs : section.Runs.Where(r => r.Rows.Any(RowVisible)).ToList();
            if (runs.Count == 0) continue;
            var heading = new TextBlock
            {
                Text = section.Key,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                Margin = new Thickness(4, any ? 16 : 4, 0, 4),
            };
            AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
            _sections.Children.Add(heading);
            foreach (var run in runs) _sections.Children.Add(BuildQuery(run));
            any = true;
        }
        if (!any) _sections.Children.Add(Message(QueryRuns.EmptyMessage(_rowFilter != null, _search)));
    }

    private static TextBlock Message(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
        Foreground = GraphTheme.Resource("TextFillColorSecondaryBrush"),
        HorizontalAlignment = HorizontalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        Margin = new Thickness(0, 48, 0, 0),
    };

    private Expander BuildQuery(QueryRunDisplay run)
    {
        var visible = run.Rows.Where(RowVisible).ToList();
        var expander = new Expander
        {
            IsExpanded = ExpandedQueryIds.Contains(run.Query.Id),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        expander.Expanding += (_, _) => ExpandedQueryIds.Add(run.Query.Id);
        expander.Collapsed += (_, _) => ExpandedQueryIds.Remove(run.Query.Id);
        AutomationProperties.SetName(expander, $"{QueryRuns.Name(run.Query)}, {visible.Count} {(visible.Count == 1 ? "row" : "rows")}");

        var name = new TextBlock
        {
            Text = QueryRuns.Name(run.Query),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };
        ToolTipService.SetToolTip(name, QueryRuns.Name(run.Query));
        var count = new TextBlock
        {
            Text = run.Truncated ? $"{visible.Count}+" : visible.Count.ToString(),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = GraphTheme.Resource("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(count, 1);
        var open = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Padding = new Thickness(6),
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
        };
        AutomationProperties.SetName(open, $"Open {run.Query.Name} in Azure DevOps");
        ToolTipService.SetToolTip(open, "Open query in Azure DevOps");
        open.Click += (_, _) => OpenQueryRequested?.Invoke(this, run.Query);
        Grid.SetColumn(open, 2);
        expander.Header = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto }, new() { Width = GridLength.Auto } },
            Children = { name, count, open },
        };

        var rows = new StackPanel { Spacing = 0 };
        if (visible.Count == 0) rows.Children.Add(new TextBlock
        {
            Text = "No matching items.",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = GraphTheme.Resource("TextFillColorSecondaryBrush"),
            Margin = new Thickness(0, 4, 0, 4),
        });
        foreach (var row in visible) rows.Children.Add(BuildRow(row));
        expander.Content = rows;
        return expander;
    }

    private FrameworkElement BuildRow(QueryRowDisplay row)
    {
        var task = row.Task;
        var grid = Columns(new Thickness(0, 6, 0, 6));

        var id = Cell(task.Id, 0, "CaptionTextBlockStyle");
        id.Foreground = GraphTheme.Resource("AccentTextFillColorPrimaryBrush");
        grid.Children.Add(id);

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(row.Depth * 18, 0, 0, 0) };
        if (row.Depth > 0) title.Children.Add(new FontIcon { Glyph = "", FontSize = 8, Foreground = GraphTheme.Resource("TextFillColorSecondaryBrush") });
        var titleText = Cell(task.Title, 0, "BodyTextBlockStyle");
        ToolTipService.SetToolTip(titleText, task.Title);
        title.Children.Add(titleText);
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        var state = QueryRuns.State(task);
        var stateText = Cell(state, 2, "CaptionTextBlockStyle");
        stateText.Foreground = GraphTheme.Tone(TaskBoard.StateTone(state));
        grid.Children.Add(stateText);
        grid.Children.Add(Secondary(Cell(string.Join(", ", task.Labels), 3, "CaptionTextBlockStyle")));
        grid.Children.Add(Cell(task.Assignees.FirstOrDefault() ?? "", 4, "CaptionTextBlockStyle"));
        grid.Children.Add(Secondary(Cell(TaskCardText.Relative(task.UpdatedAt, DateTime.UtcNow), 5, "CaptionTextBlockStyle")));

        // A button, so a row is a keyboard stop with a name and Enter opens it.
        var button = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Tag = task,
        };
        AutomationProperties.SetName(button, $"{task.Id} {task.Title}, {state}");
        button.Click += (_, _) => TaskSelected?.Invoke(this, task);
        button.ContextRequested += (sender, e) =>
        {
            e.Handled = true;
            TaskMenuRequested?.Invoke(this, (task, (FrameworkElement)sender, e.TryGetPosition(button, out var at) ? at : null));
        };
        return button;
    }

    private static TextBlock Cell(string text, int column, string style)
    {
        var block = new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.Resources[style],
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            MaxLines = 1,
        };
        Grid.SetColumn(block, column);
        return block;
    }

    private static TextBlock Secondary(TextBlock block)
    {
        block.Foreground = GraphTheme.Resource("TextFillColorSecondaryBrush");
        return block;
    }
}
