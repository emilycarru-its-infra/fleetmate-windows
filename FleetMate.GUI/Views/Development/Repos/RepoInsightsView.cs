using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using FleetMate.Core.Services.Repos;
using ModernWpf.Controls;
using static FleetMate.GUI.Views.Development.Repos.RepoInsightsModel;

namespace FleetMate.GUI.Views.Development.Repos;

/// <summary>
/// Charts of a repository's history, or of every tracked repository: commits
/// and lines changed over time, who commits, where the changes land, and
/// when. The numbers are the ones <c>fleetmate repos stats</c> prints.
/// </summary>
public sealed class RepoInsightsView : UserControl
{
    private RepoWorkspaceModel? _model;
    private readonly RadioButton _scopeRepository, _scopeAll;
    private readonly ComboBox _period, _granularity;
    private readonly ProgressRing _ring;
    private readonly TextBlock _caption;
    private readonly StackPanel _content = new() { Margin = new Thickness(0, 0, 12, 16) };
    private string? _requestedKey;
    private int _refresh;

    /// <summary>Opens a file from the most-changed list in the Files panel.</summary>
    public Action<string>? OpenFile { get; set; }

    public RepoInsightsView()
    {
        _scopeRepository = ScopeButton("This Repository", InsightsScope.Repository);
        _scopeAll = ScopeButton("All Tracked", InsightsScope.All);
        var scope = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Margin = new Thickness(0, 0, 10, 0) };
        scope.SetResourceReference(Border.BackgroundProperty, "SubtleFillBrush");
        scope.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _scopeRepository, _scopeAll } };
        scope.ToolTip = "Statistics for the selected repository, or for every tracked repository together";

        _period = new ComboBox { Margin = new Thickness(0, 0, 8, 0), MinWidth = 110, ToolTip = "Period" };
        foreach (var p in Enum.GetValues<InsightsPeriod>()) _period.Items.Add(new ComboBoxItem { Content = Title(p), Tag = p });
        _granularity = new ComboBox { Margin = new Thickness(0, 0, 8, 0), MinWidth = 110, ToolTip = "Bars per day, week or month" };
        foreach (var g in Enum.GetValues<InsightsGranularity>()) _granularity.Items.Add(new ComboBoxItem { Content = Title(g), Tag = g });
        _period.SelectionChanged += (_, _) => { if (_model != null && _period.SelectedItem is ComboBoxItem { Tag: InsightsPeriod p }) { _model.Insights.Period = p; Load(); } };
        _granularity.SelectionChanged += (_, _) => { if (_model != null && _granularity.SelectedItem is ComboBoxItem { Tag: InsightsGranularity g }) { _model.Insights.Granularity = g; Load(); } };

        _ring = new ProgressRing { IsActive = true, Width = 16, Height = 16, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 8, 0) };
        var refresh = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(6, 4, 6, 4), ToolTip = "Read the history again" };
        System.Windows.Automation.AutomationProperties.SetName(refresh, "Refresh Insights");
        refresh.Click += (_, _) => { _refresh++; Load(); };
        _caption = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 8, 0) };
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");

        var controls = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(refresh, Dock.Right);
        controls.Children.Add(refresh);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Children = { scope, _period, _granularity, _ring } };
        DockPanel.SetDock(left, Dock.Left);
        controls.Children.Add(left);
        controls.Children.Add(_caption);

        var root = new DockPanel();
        DockPanel.SetDock(controls, Dock.Top);
        root.Children.Add(controls);
        root.Children.Add(new ScrollViewer
        {
            Content = _content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        Content = root;
        IsVisibleChanged += (_, _) => { if (IsVisible) Load(); };
    }

    private RadioButton ScopeButton(string text, InsightsScope scope)
    {
        var button = new RadioButton { Content = text, GroupName = "InsightsScope" + GetHashCode(), Tag = scope };
        button.SetResourceReference(StyleProperty, "NavigationTabStyle");
        button.Checked += (_, _) =>
        {
            if (_model == null) return;
            _model.Insights.Scope = scope;
            Load();
        };
        return button;
    }

    public void Attach(RepoWorkspaceModel model)
    {
        _model = model;
        var insights = model.Insights;
        (insights.Scope == InsightsScope.All ? _scopeAll : _scopeRepository).IsChecked = true;
        _period.SelectedItem = _period.Items.Cast<ComboBoxItem>().First(i => (InsightsPeriod)i.Tag == insights.Period);
        _granularity.SelectedItem = _granularity.Items.Cast<ComboBoxItem>().First(i => (InsightsGranularity)i.Tag == insights.Granularity);
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RepoWorkspaceModel.SelectedId) or nameof(RepoWorkspaceModel.Tracked) && IsVisible) Load();
        };
    }

    /// <summary>Reads the statistics for the current scope, period and selection, unless they are already shown.</summary>
    private async void Load()
    {
        if (_model == null || !IsVisible) return;
        var insights = _model.Insights;
        var key = insights.Key(_model.Selected, _model.Tracked) + "#" + _refresh;
        if (key == _requestedKey) return;
        _requestedKey = key;
        UpdateCaption();
        _ring.Visibility = Visibility.Visible;
        if (insights.ShownKey == null) Placeholder("Reading history…");
        await insights.LoadAsync(_model.Manager, _model.Selected, _model.Tracked);
        if (key != _requestedKey) return;
        _ring.Visibility = Visibility.Collapsed;
        Render();
    }

    private void UpdateCaption()
    {
        if (_model == null) return;
        var since = SinceArgument(_model.Insights.Period);
        var cli = _model.Insights.Scope == InsightsScope.All
            ? $"fleetmate repos stats --since {since}"
            : $"fleetmate repos stats {_model.Selected?.Key.DisplayName ?? "<repo>"} --since {since}";
        _caption.Text = "Merge commits excluded · " + cli;
        _caption.ToolTip = cli;
    }

    private void Placeholder(string text)
    {
        _content.Children.Clear();
        _content.Children.Add(new TextBlock { Text = text, Opacity = 0.7, Margin = new Thickness(4, 20, 4, 4), HorizontalAlignment = HorizontalAlignment.Center });
    }

    /// <summary>Builds the cards for what the model holds now.</summary>
    internal void Render()
    {
        if (_model == null) return;
        var insights = _model.Insights;
        _content.Children.Clear();
        if (insights.Error is { } error)
            _content.Children.Add(new TextBlock { Text = "⚠ " + error, Foreground = RepoBrushes.Warning, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        if (insights.Scope == InsightsScope.Repository)
        {
            if (insights.Report is { } report) RenderReport(report);
            else if (insights.Error == null) Placeholder("Select a repository with commits.");
        }
        else if (insights.Summary is { } summary)
        {
            RenderSummary(summary);
        }
    }

    // ── One repository ──────────────────────────────────────────────────

    private void RenderReport(RepoStatsReport report)
    {
        var bucket = TimelineChart.Bucket(report.Bucket);
        _content.Children.Add(Tiles(report.Total));
        var commits = new TimelineChart();
        commits.ShowCommits(report.Timeline, report.Bucket);
        _content.Children.Add(Card($"Commits per {bucket}", commits));
        var churn = new TimelineChart();
        churn.ShowChurn(report.Timeline, report.Bucket);
        _content.Children.Add(Card($"Lines changed per {bucket}", new StackPanel { Children = { churn, Legend(("Added", RepoBrushes.Added), ("Removed", RepoBrushes.Removed)) } }));
        _content.Children.Add(TwoUp(
            Card("Authors", Ranked(report.Contributors.Take(10).Select(c => (c.Name, c.Commits, $"+{c.Added:N0} −{c.Removed:N0}")), "commits")),
            Card("Top-level folders", Ranked(report.Areas.Take(10).Select(a => (a.Path, a.Commits, $"+{a.Added:N0} −{a.Removed:N0}")), "commits"))));
        _content.Children.Add(Card("Most-changed files", FileList(report.Files)));
        var heat = new ActivityHeatmap();
        heat.Show(report.Activity);
        _content.Children.Add(Card("When commits happen", heat));
    }

    // ── All repositories ────────────────────────────────────────────────

    private void RenderSummary(RepoStatsSummary summary)
    {
        var bucket = TimelineChart.Bucket(summary.Bucket);
        _content.Children.Add(Tiles(summary.Combined.Total));

        // The busiest repositories by name, in a fixed palette; the rest as Other.
        var active = summary.Rows.Where(r => r.Commits > 0).ToList();
        var named = active.Take(RepoBrushes.Series.Length).ToList();
        var series = named.Select((r, i) => (r.DisplayName, RepoBrushes.Series[i], r.Timeline)).ToList();
        if (active.Count > named.Count)
        {
            var other = new int[summary.BucketStarts.Count];
            foreach (var row in active.Skip(named.Count))
                for (var i = 0; i < other.Length && i < row.Timeline.Count; i++) other[i] += row.Timeline[i];
            series.Add(("Other", RepoBrushes.Other, other));
        }
        var stacked = new TimelineChart();
        stacked.ShowStacked(summary.BucketStarts, series.Select(s => (s.Item1, s.Item2, (IReadOnlyList<int>)s.Item3)).ToList(), summary.Bucket);
        _content.Children.Add(Card($"Commits per {bucket} by repository",
            new StackPanel { Children = { stacked, Legend(series.Select(s => (s.Item1, s.Item2)).ToArray()) } }));

        var churn = new TimelineChart();
        churn.ShowChurn(summary.Combined.Timeline, summary.Bucket);
        _content.Children.Add(Card($"Lines changed per {bucket}", new StackPanel { Children = { churn, Legend(("Added", RepoBrushes.Added), ("Removed", RepoBrushes.Removed)) } }));
        _content.Children.Add(Card("Repositories", SummaryTable(summary.Rows)));
        _content.Children.Add(TwoUp(
            Card("Authors", Ranked(summary.Combined.Contributors.Take(10).Select(c => (c.Name, c.Commits, $"+{c.Added:N0} −{c.Removed:N0}")), "commits")),
            Card("Behind upstream", Ranked(summary.Rows.Where(r => r.Behind > 0).OrderByDescending(r => r.Behind).Take(10)
                .Select(r => (r.DisplayName, r.Behind, r.Branch ?? "")), "commits behind"))));
        var heat = new ActivityHeatmap();
        heat.Show(summary.Combined.Activity);
        _content.Children.Add(Card("When commits happen", heat));
    }

    private UIElement SummaryTable(IReadOnlyList<RepoStatsSummaryRow> rows)
    {
        var grid = new Grid();
        var headers = new[] { "Repository", "Commits", "Added", "Removed", "Ahead", "Behind", "Open changes", "Last commit" };
        for (var c = 0; c < headers.Length; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 0 ? new GridLength(2, GridUnitType.Star) : c == 7 ? new GridLength(1.3, GridUnitType.Star) : GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < headers.Length; c++)
            Cell(grid, 0, c, new TextBlock { Text = headers[c], FontSize = 11, FontWeight = FontWeights.SemiBold, Opacity = 0.7, TextAlignment = c is > 0 and < 7 ? TextAlignment.Right : TextAlignment.Left });
        var r = 1;
        foreach (var row in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var link = new Hyperlink(new Run(row.DisplayName)) { ToolTip = row.Error ?? $"Show {row.DisplayName}'s insights" };
            var id = row.Id;
            link.Click += (_, _) =>
            {
                if (_model == null) return;
                _model.SelectedId = id;
                _scopeRepository.IsChecked = true;
            };
            Cell(grid, r, 0, new TextBlock(link) { TextTrimming = TextTrimming.CharacterEllipsis });
            var values = new[] { row.Commits.ToString("N0"), $"+{row.Added:N0}", $"−{row.Removed:N0}", row.Ahead.ToString("N0"), row.Behind.ToString("N0"), row.OpenChanges.ToString("N0") };
            for (var c = 0; c < values.Length; c++)
                Cell(grid, r, c + 1, new TextBlock { Text = values[c], TextAlignment = TextAlignment.Right });
            Cell(grid, r, 7, row.Error is { } error && row.Commits == 0
                ? new TextBlock { Text = "⚠ " + error, Foreground = RepoBrushes.Warning, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = error }
                : new TextBlock { Text = RepoShell.Relative(row.LastCommit), Opacity = 0.7 });
            r++;
        }
        return grid;
    }

    private static void Cell(Grid grid, int row, int column, FrameworkElement element)
    {
        element.Margin = new Thickness(column == 0 ? 0 : 14, 3, 0, 3);
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    // ── Pieces ──────────────────────────────────────────────────────────

    private static Border Card(string title, UIElement content)
    {
        var heading = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
        var border = new Border { Child = new StackPanel { Children = { heading, content } } };
        border.SetResourceReference(StyleProperty, "CardStyle");
        return border;
    }

    private static UIElement TwoUp(UIElement left, UIElement right)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private static UIElement Tiles(RepoStatsReport.Totals totals)
    {
        var grid = new UniformGrid { Rows = 1, Margin = new Thickness(0, 0, 0, 10) };
        void Tile(string title, string value)
        {
            var label = new TextBlock { Text = title, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            label.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
            var figure = new TextBlock { Text = value, FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = value };
            var border = new Border { Child = new StackPanel { Children = { label, figure } }, Margin = new Thickness(0, 0, 8, 0) };
            border.SetResourceReference(StyleProperty, "CardStyle");
            System.Windows.Automation.AutomationProperties.SetName(border, $"{title}: {value}");
            grid.Children.Add(border);
        }
        Tile("Commits", totals.Commits.ToString("N0"));
        Tile("Authors", totals.Authors.ToString("N0"));
        Tile("Lines added", "+" + totals.Added.ToString("N0"));
        Tile("Lines removed", "−" + totals.Removed.ToString("N0"));
        Tile("Files touched", totals.FilesTouched.ToString("N0"));
        Tile("Last commit", RepoShell.Relative(totals.LastCommit));
        return grid;
    }

    /// <summary>Label, bar, count and a detail per row. Only the bar carries colour.</summary>
    private static UIElement Ranked(IEnumerable<(string Label, int Value, string Detail)> items, string unit)
    {
        var rows = items.ToList();
        if (rows.Count == 0) return new TextBlock { Text = "Nothing in this period", Opacity = 0.7 };
        var peak = Math.Max(1, rows.Max(r => r.Value));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 200 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < rows.Count; i++)
        {
            var (label, value, detail) = rows[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = label, Margin = new Thickness(0, 2, 8, 2) };
            var bar = new Border
            {
                Background = RepoBrushes.Accent,
                CornerRadius = new CornerRadius(3),
                Height = 12,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var track = new Grid { Margin = new Thickness(0, 2, 8, 2) };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.02, value / (double)peak), GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 1 - value / (double)peak), GridUnitType.Star) });
            bar.HorizontalAlignment = HorizontalAlignment.Stretch;
            track.Children.Add(bar);
            var count = new TextBlock { Text = value.ToString("N0"), FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 2, 8, 2) };
            var extra = new TextBlock { Text = detail, FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 2, 0, 2), VerticalAlignment = VerticalAlignment.Center };
            foreach (var (element, column) in new (FrameworkElement, int)[] { (name, 0), (track, 1), (count, 2), (extra, 3) })
            {
                Grid.SetRow(element, i);
                Grid.SetColumn(element, column);
                grid.Children.Add(element);
            }
            System.Windows.Automation.AutomationProperties.SetName(track, $"{label}: {value} {unit}, {detail}");
        }
        return grid;
    }

    private UIElement FileList(IReadOnlyList<RepoStatsReport.PathActivity> files)
    {
        if (files.Count == 0) return new TextBlock { Text = "Nothing in this period", Opacity = 0.7 };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var c = 0; c < 3; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var headers = new[] { "File", "Commits", "Added", "Removed" };
        for (var c = 0; c < headers.Length; c++)
            Cell(grid, 0, c, new TextBlock { Text = headers[c], FontSize = 11, FontWeight = FontWeights.SemiBold, Opacity = 0.7, TextAlignment = c > 0 ? TextAlignment.Right : TextAlignment.Left });
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var link = new Hyperlink(new Run(file.Path)) { ToolTip = $"Open {file.Path} in Files" };
            link.Click += (_, _) => OpenFile?.Invoke(file.Path);
            var text = new TextBlock(link) { TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = new FontFamily("Consolas") };
            var menu = new ContextMenu();
            var open = new MenuItem { Header = "Open in Files" };
            open.Click += (_, _) => OpenFile?.Invoke(file.Path);
            var copy = new MenuItem { Header = "Copy Path" };
            copy.Click += (_, _) => RepoShell.Copy(file.Path);
            menu.Items.Add(open);
            menu.Items.Add(copy);
            text.ContextMenu = menu;
            Cell(grid, i + 1, 0, text);
            Cell(grid, i + 1, 1, new TextBlock { Text = file.Commits.ToString("N0"), TextAlignment = TextAlignment.Right });
            Cell(grid, i + 1, 2, new TextBlock { Text = $"+{file.Added:N0}", TextAlignment = TextAlignment.Right });
            Cell(grid, i + 1, 3, new TextBlock { Text = $"−{file.Removed:N0}", TextAlignment = TextAlignment.Right });
        }
        return grid;
    }

    private static UIElement Legend(params (string Name, Brush Brush)[] items)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (name, brush) in items)
        {
            panel.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = brush, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(new TextBlock { Text = name, FontSize = 11, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center });
        }
        return panel;
    }
}
