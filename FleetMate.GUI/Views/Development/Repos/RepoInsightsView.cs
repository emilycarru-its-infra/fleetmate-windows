using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using FleetMate.Core.Services.Repos;
using FleetMate.GUI.Views.Shared.Widgets;
using ModernWpf.Controls;
using static FleetMate.GUI.Views.Development.Repos.RepoInsightsModel;

namespace FleetMate.GUI.Views.Development.Repos;

/// <summary>
/// Charts of a repository's history, or of every tracked repository: commits
/// and lines changed over time, who commits, where the changes land, and
/// when. The numbers are the ones <c>fleetmate repos stats</c> prints. It is
/// the Repos sub-tab's Graphs strip: the cards flow across the full width in
/// rows of equal height, as the other sub-tabs' widgets do.
/// </summary>
public sealed class RepoInsightsView : UserControl
{
    private RepoWorkspaceModel? _model;
    private readonly RadioButton _scopeRepository, _scopeAll;
    private readonly ComboBox _period, _granularity;
    private readonly ProgressRing _ring;
    private readonly TextBlock _caption;
    private readonly WidgetFlowPanel _content = new() { EqualHeights = true };

    /// <summary>A card's units when it takes a whole row of the strip.</summary>
    private const int FullRow = 99;
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
        root.Children.Add(_content);
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
        _caption.Text = "origin/main · Merge commits excluded · " + cli + " --ref " + HistoryReference;
        _caption.ToolTip = cli + " --ref " + HistoryReference;
    }

    private void Placeholder(string text)
    {
        _content.Children.Clear();
        _content.Children.Add(Whole(new TextBlock { Text = text, Opacity = 0.7, Margin = new Thickness(4, 20, 4, 20), HorizontalAlignment = HorizontalAlignment.Center }));
    }

    /// <summary>Builds the cards for what the model holds now.</summary>
    internal void Render()
    {
        if (_model == null) return;
        var insights = _model.Insights;
        _content.Children.Clear();
        if (insights.Error is { } error)
            _content.Children.Add(Whole(new TextBlock { Text = "⚠ " + error, Foreground = RepoBrushes.Warning, TextWrapping = TextWrapping.Wrap }));
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
        _content.Children.Add(Card($"Commits per {bucket}", commits, 2));
        _content.Children.Add(ChurnCard(report.Timeline, report.Bucket));
        _content.Children.Add(Card("Authors", Ranked(report.Contributors.Take(10).Select(c => (c.Name, c.Commits, $"+{c.Added:N0} −{c.Removed:N0}")), "commits")));
        _content.Children.Add(Card("Top-level folders", Ranked(report.Areas.Take(10).Select(a => (a.Path, a.Commits, $"+{a.Added:N0} −{a.Removed:N0}")), "commits")));
        _content.Children.Add(Card("Most-changed files", FileList(report.Files), 2));
        var heat = new ActivityHeatmap();
        heat.Show(report.Activity);
        _content.Children.Add(Card("When commits happen", heat, 2));
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
            new StackPanel { Children = { stacked, Legend(series.Select(s => (s.Item1, s.Item2)).ToArray()) } }, 2));

        _content.Children.Add(ChurnCard(summary.Combined.Timeline, summary.Bucket));
        _content.Children.Add(Card("Authors", Ranked(summary.Combined.Contributors.Take(10).Select(c => (c.Name, c.Commits, $"+{c.Added:N0} −{c.Removed:N0}")), "commits")));
        _content.Children.Add(Card("Behind upstream", Ranked(summary.Rows.Where(r => r.Behind > 0).OrderByDescending(r => r.Behind).Take(10)
            .Select(r => (r.DisplayName, r.Behind, r.Branch ?? "")), "commits behind")));
        _content.Children.Add(Card("Repositories", SummaryTable(summary.Rows), 2));
        var heat = new ActivityHeatmap();
        heat.Show(summary.Combined.Activity);
        _content.Children.Add(Card("When commits happen", heat, 2));
    }

    private UIElement ChurnCard(IReadOnlyList<RepoStatsReport.TimelinePoint> points, RepoStatsBucket bucket)
    {
        var choice = _model!.Insights.ChurnScale;
        var effective = RepoChurnScaling.Resolve(choice, points);
        var automatic = RepoChurnScaling.Resolve(RepoChurnScale.Automatic, points);
        var picker = new ComboBox { MinWidth = 170, Margin = new Thickness(6, 0, 12, 0) };
        System.Windows.Automation.AutomationProperties.SetName(picker, "Lines changed scale");
        foreach (var value in Enum.GetValues<RepoChurnScale>())
            picker.Items.Add(new ComboBoxItem { Content = value == RepoChurnScale.Automatic ? $"Automatic ({automatic})" : value.ToString(), Tag = value });
        picker.SelectedItem = picker.Items.Cast<ComboBoxItem>().First(i => (RepoChurnScale)i.Tag == choice);
        picker.SelectionChanged += (_, _) => {
            if (_model == null || picker.SelectedItem is not ComboBoxItem { Tag: RepoChurnScale value }) return;
            _model.Insights.ChurnScale = value;
            Render();
        };
        var caption = new TextBlock { Text = effective == RepoChurnScale.Compressed ? "Compressed scale · raw values on hover" : "Linear scale", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        var controls = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        controls.Children.Add(new TextBlock { Text = "Scale", VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(picker);
        controls.Children.Add(caption);
        var churn = new TimelineChart();
        churn.ShowChurn(points, bucket, effective);
        return Card($"Lines changed per {TimelineChart.Bucket(bucket)}", new StackPanel { Children = { controls, churn, Legend(("Added", RepoBrushes.Added), ("Removed", RepoBrushes.Removed)) } }, 2);
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

    /// <summary>An element that takes a whole row of the strip.</summary>
    private static UIElement Whole(UIElement element)
    {
        WidgetFlowPanel.SetUnits(element, FullRow);
        return element;
    }

    /// <summary>A titled card spanning <paramref name="units"/> of the strip's width.</summary>
    private static Border Card(string title, UIElement content, int units = 1)
    {
        var heading = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(content, 1);
        body.Children.Add(heading);
        body.Children.Add(content);
        var border = new Border { Child = body, VerticalAlignment = VerticalAlignment.Stretch };
        border.SetResourceReference(StyleProperty, "CardStyle");
        WidgetFlowPanel.SetUnits(border, units);
        return border;
    }

    private static UIElement Tiles(RepoStatsReport.Totals totals)
    {
        var grid = new UniformGrid { Rows = 1, Margin = new Thickness(-4, 0, -4, 0) };
        WidgetFlowPanel.SetUnits(grid, FullRow);
        void Tile(string title, string value)
        {
            var label = new TextBlock { Text = title, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            label.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
            var figure = new TextBlock { Text = value, FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = value };
            var border = new Border { Child = new StackPanel { Children = { label, figure } }, Margin = new Thickness(4, 0, 4, 0) };
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
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 24 });
            var name = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = label, Margin = new Thickness(0, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
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
            var count = new TextBlock { Text = value.ToString("N0"), FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
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
