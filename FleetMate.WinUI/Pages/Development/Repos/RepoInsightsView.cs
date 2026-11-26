using FleetMate.Core.Services.Repos;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using FleetMate.Core.Shared;
using FleetMate.WinUI.Shell.Widgets;
using static FleetMate.Core.Services.Repos.RepoInsightsModel;

namespace FleetMate.WinUI.Pages.Development.Repos;

/// <summary>
/// Charts of a repository's history, or of every tracked repository: commits
/// and lines changed over time, who commits, where the changes land, and
/// when. The numbers are the ones <c>fleetmate repos stats</c> prints, read
/// from <c>origin/main</c>. It is the Repos sub-tab's Graphs strip: the cards
/// flow across the full width in rows of equal height.
/// </summary>
public sealed partial class RepoInsightsView : UserControl
{
    private readonly RepoWorkspaceModel _model;
    private readonly SelectorBar _scope;
    private readonly SelectorBarItem _scopeRepository, _scopeAll;
    private readonly ComboBox _period, _granularity;
    private readonly ProgressRing _ring;
    private readonly TextBlock _caption;
    private readonly GraphFlowPanel _content = new();
    private string? _requestedKey;
    private int _refresh;
    private bool _syncing;

    /// <summary>Opens a file from the most-changed list in the Files panel.</summary>
    public Action<string>? OpenFile { get; set; }

    /// <summary>Raised after the cards were rebuilt, so the host can resize the strip.</summary>
    public event EventHandler? Rendered;

    public RepoInsightsView(RepoWorkspaceModel model)
    {
        _model = model;
        _scopeRepository = new SelectorBarItem { Text = "This Repository", Tag = InsightsScope.Repository };
        _scopeAll = new SelectorBarItem { Text = "All Tracked", Tag = InsightsScope.All };
        AutomationProperties.SetAutomationId(_scopeRepository, "ReposInsightsScopeRepository");
        AutomationProperties.SetAutomationId(_scopeAll, "ReposInsightsScopeAll");
        _scope = new SelectorBar { Items = { _scopeRepository, _scopeAll } };
        AutomationProperties.SetName(_scope, "Insights scope");
        ToolTipService.SetToolTip(_scope, "Statistics for the selected repository, or for every tracked repository together");
        _scope.SelectionChanged += (_, _) =>
        {
            if (_syncing || _scope.SelectedItem?.Tag is not InsightsScope scope) return;
            _model.Insights.Scope = scope;
            Load();
        };

        _period = new ComboBox { MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_period, "Period");
        AutomationProperties.SetAutomationId(_period, "ReposInsightsPeriod");
        ToolTipService.SetToolTip(_period, "Period");
        foreach (var p in Enum.GetValues<InsightsPeriod>()) _period.Items.Add(new ComboBoxItem { Content = Title(p), Tag = p });
        _granularity = new ComboBox { MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_granularity, "Bars per day, week or month");
        AutomationProperties.SetAutomationId(_granularity, "ReposInsightsGranularity");
        ToolTipService.SetToolTip(_granularity, "Bars per day, week or month");
        foreach (var g in Enum.GetValues<InsightsGranularity>()) _granularity.Items.Add(new ComboBoxItem { Content = Title(g), Tag = g });
        _period.SelectionChanged += (_, _) =>
        {
            if (_syncing || _period.SelectedItem is not ComboBoxItem { Tag: InsightsPeriod p }) return;
            _model.Insights.Period = p;
            Load();
        };
        _granularity.SelectionChanged += (_, _) =>
        {
            if (_syncing || _granularity.SelectedItem is not ComboBoxItem { Tag: InsightsGranularity g }) return;
            _model.Insights.Granularity = g;
            Load();
        };

        _ring = new ProgressRing { IsActive = false, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_ring, "Reading history");
        var refresh = new Button { Content = new FontIcon { Glyph = "\uE72C", FontSize = 14 }, Padding = new Thickness(8, 6, 8, 6), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(refresh, "Refresh Insights");
        AutomationProperties.SetAutomationId(refresh, "ReposInsightsRefresh");
        ToolTipService.SetToolTip(refresh, "Read the history again");
        refresh.Click += (_, _) => { _refresh++; Load(); };
        _caption = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = RepoBrushes.Secondary,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            IsTextSelectionEnabled = true,
        };

        var controls = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        FrameworkElement[] parts = { _scope, _period, _granularity, _ring, _caption, refresh };
        for (var i = 0; i < parts.Length; i++)
        {
            Grid.SetColumn(parts[i], i);
            controls.Children.Add(parts[i]);
        }

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_content, 1);
        root.Children.Add(controls);
        root.Children.Add(_content);
        Content = root;
        AutomationProperties.SetName(this, "Repository Insights");

        SyncControls();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RepoWorkspaceModel.SelectedId) or nameof(RepoWorkspaceModel.Tracked) && IsLoaded) Load();
        };
        Loaded += (_, _) => Load();
    }

    private void SyncControls()
    {
        var insights = _model.Insights;
        _syncing = true;
        _scope.SelectedItem = insights.Scope == InsightsScope.All ? _scopeAll : _scopeRepository;
        _period.SelectedItem = _period.Items.Cast<ComboBoxItem>().First(i => (InsightsPeriod)i.Tag == insights.Period);
        _granularity.SelectedItem = _granularity.Items.Cast<ComboBoxItem>().First(i => (InsightsGranularity)i.Tag == insights.Granularity);
        _syncing = false;
    }

    /// <summary>Reads the statistics for the current scope, period and selection, unless they are already shown.</summary>
    public async void Load()
    {
        if (!IsLoaded) return;
        var insights = _model.Insights;
        var key = insights.Key(_model.Selected, _model.Tracked) + "#" + _refresh;
        if (key == _requestedKey) return;
        _requestedKey = key;
        _caption.Text = RepoWorkspaceText.InsightsCaption(_model);
        ToolTipService.SetToolTip(_caption, RepoWorkspaceText.InsightsCommand(_model));
        _ring.IsActive = true;
        if (insights.ShownKey == null) Placeholder("Reading history…");
        await insights.LoadAsync(_model.Manager, _model.Selected, _model.Tracked);
        if (key != _requestedKey) return;
        _ring.IsActive = false;
        Render();
    }

    private void Placeholder(string text)
    {
        _content.Children.Clear();
        _content.Children.Add(Whole(new TextBlock
        {
            Text = text,
            Foreground = RepoBrushes.Secondary,
            Margin = new Thickness(4, 20, 4, 20),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        }));
    }

    /// <summary>Builds the cards for what the model holds now.</summary>
    private void Render()
    {
        var insights = _model.Insights;
        _content.Children.Clear();
        if (insights.Error is { } error)
            _content.Children.Add(Whole(new InfoBar
            {
                Title = "Insights could not read the history",
                Message = error,
                Severity = InfoBarSeverity.Warning,
                IsOpen = true,
                IsClosable = false,
            }));
        if (insights.Scope == InsightsScope.Repository)
        {
            if (insights.Report is { } report) RenderReport(report);
            else if (insights.Error == null) Placeholder("Select a repository with commits.");
        }
        else if (insights.Summary is { } summary)
        {
            RenderSummary(summary);
        }
        Rendered?.Invoke(this, EventArgs.Empty);
    }

    // MARK: - One repository

    private void RenderReport(RepoStatsReport report)
    {
        var bucket = TimelineChart.Bucket(report.Bucket);
        _content.Children.Add(Tiles(report.Total));
        var commits = new TimelineChart();
        commits.ShowCommits(report.Timeline, report.Bucket);
        _content.Children.Add(Card($"Commits per {bucket}", commits, 2));
        _content.Children.Add(ChurnCard(report.Timeline, report.Bucket));
        _content.Children.Add(Card("Authors", Ranked(report.Contributors.Take(8).Select(c => (c.Name, c.Commits, $"+{c.Added:N0} −{c.Removed:N0}")), "commits")));
        _content.Children.Add(Card("Top-level folders", Ranked(report.Areas.Take(8).Select(a => (a.Path, a.Commits, $"+{a.Added:N0} −{a.Removed:N0}")), "commits")));
        _content.Children.Add(Card("Most-changed files", FileList(report.Files.Take(8).ToList()), 2));
        var heat = new ActivityHeatmap();
        heat.Show(report.Activity);
        _content.Children.Add(Card("When commits happen", heat, 2));
    }

    // MARK: - All repositories

    private void RenderSummary(RepoStatsSummary summary)
    {
        var bucket = TimelineChart.Bucket(summary.Bucket);
        _content.Children.Add(Tiles(summary.Combined.Total));

        // The busiest repositories by name, in a fixed palette; the rest as Other.
        var active = summary.Rows.Where(r => r.Commits > 0).ToList();
        var named = active.Take(RepoBrushes.SeriesCount).ToList();
        var series = named.Select((r, i) => (Name: r.DisplayName, Slot: i, Values: (IReadOnlyList<int>)r.Timeline)).ToList();
        if (active.Count > named.Count)
        {
            var other = new int[summary.BucketStarts.Count];
            foreach (var row in active.Skip(named.Count))
                for (var i = 0; i < other.Length && i < row.Timeline.Count; i++) other[i] += row.Timeline[i];
            series.Add(("Other", TimelineChart.OtherSlot, other));
        }
        var stacked = new TimelineChart();
        stacked.ShowStacked(summary.BucketStarts, series, summary.Bucket);
        _content.Children.Add(Card($"Commits per {bucket} by repository",
            Docked(stacked, Legend(series.Select(s => (s.Name, TimelineChart.SeriesBrush(s.Slot))).ToArray())), 2));

        _content.Children.Add(ChurnCard(summary.Combined.Timeline, summary.Bucket));
        _content.Children.Add(Card("Authors", Ranked(summary.Combined.Contributors.Take(8).Select(c => (c.Name, c.Commits, $"+{c.Added:N0} −{c.Removed:N0}")), "commits")));
        _content.Children.Add(Card("Behind upstream", Ranked(summary.Rows.Where(r => r.Behind > 0).OrderByDescending(r => r.Behind).Take(8)
            .Select(r => (r.DisplayName, r.Behind, r.Branch ?? "")), "commits behind")));
        _content.Children.Add(Card("Repositories", SummaryTable(summary.Rows), 2));
        var heat = new ActivityHeatmap();
        heat.Show(summary.Combined.Activity);
        _content.Children.Add(Card("When commits happen", heat, 2));
    }

    private UIElement ChurnCard(IReadOnlyList<RepoStatsReport.TimelinePoint> points, RepoStatsBucket bucket)
    {
        var choice = _model.Insights.ChurnScale;
        var effective = RepoChurnScaling.Resolve(choice, points);
        var automatic = RepoChurnScaling.Resolve(RepoChurnScale.Automatic, points);
        var picker = new ComboBox { MinWidth = 170 };
        AutomationProperties.SetName(picker, "Lines changed scale");
        AutomationProperties.SetAutomationId(picker, "ReposInsightsChurnScale");
        foreach (var value in Enum.GetValues<RepoChurnScale>())
            picker.Items.Add(new ComboBoxItem { Content = value == RepoChurnScale.Automatic ? $"Automatic ({automatic})" : value.ToString(), Tag = value });
        picker.SelectedItem = picker.Items.Cast<ComboBoxItem>().First(i => (RepoChurnScale)i.Tag == choice);
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is not ComboBoxItem { Tag: RepoChurnScale value } || value == _model.Insights.ChurnScale) return;
            _model.Insights.ChurnScale = value;
            Render();
        };
        var caption = new TextBlock
        {
            Text = effective == RepoChurnScale.Compressed ? "Compressed scale · raw values on hover" : "Linear scale",
            Foreground = RepoBrushes.Secondary,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        var controls = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 6) };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock { Text = "Scale", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(picker, 1);
        Grid.SetColumn(caption, 2);
        controls.Children.Add(label);
        controls.Children.Add(picker);
        controls.Children.Add(caption);
        var churn = new TimelineChart();
        churn.ShowChurn(points, bucket, effective);
        return Card($"Lines changed per {TimelineChart.Bucket(bucket)}",
            Docked(churn, Legend(("Added", RepoBrushes.Added), ("Removed", RepoBrushes.Removed)), controls), 2);
    }

    private UIElement SummaryTable(IReadOnlyList<RepoStatsSummaryRow> rows)
    {
        var grid = new Grid { ColumnSpacing = 14, RowSpacing = 2 };
        var headers = new[] { "Repository", "Commits", "Added", "Removed", "Ahead", "Behind", "Open changes", "Last commit" };
        for (var c = 0; c < headers.Length; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = c == 0 ? new GridLength(2, GridUnitType.Star) : c == 7 ? new GridLength(1.3, GridUnitType.Star) : GridLength.Auto,
            });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < headers.Length; c++)
            Cell(grid, 0, c, new TextBlock
            {
                Text = headers[c],
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                FontWeight = FontWeights.SemiBold,
                Foreground = RepoBrushes.Secondary,
                TextAlignment = c is > 0 and < 7 ? TextAlignment.Right : TextAlignment.Left,
                HorizontalAlignment = c is > 0 and < 7 ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            });
        var r = 1;
        foreach (var row in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var link = new HyperlinkButton
            {
                Content = new TextBlock { Text = row.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis },
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            ToolTipService.SetToolTip(link, row.Error ?? $"Show {row.DisplayName}'s insights");
            AutomationProperties.SetName(link, $"Show {row.DisplayName}'s insights");
            var id = row.Id;
            link.Click += (_, _) =>
            {
                _model.Insights.Scope = InsightsScope.Repository;
                _model.SelectedId = id;
                SyncControls();
                Load();
            };
            Cell(grid, r, 0, link);
            var values = new[] { row.Commits.ToString("N0"), $"+{row.Added:N0}", $"−{row.Removed:N0}", row.Ahead.ToString("N0"), row.Behind.ToString("N0"), row.OpenChanges.ToString("N0") };
            for (var c = 0; c < values.Length; c++)
                Cell(grid, r, c + 1, new TextBlock { Text = values[c], HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
            FrameworkElement last = row.Error is { } error && row.Commits == 0
                ? new TextBlock { Text = "⚠ " + error, Foreground = RepoBrushes.Warning, TextTrimming = TextTrimming.CharacterEllipsis }
                : new TextBlock { Text = RepoShell.Relative(row.LastCommit), Foreground = RepoBrushes.Secondary };
            if (row.Error is { } tip) ToolTipService.SetToolTip(last, tip);
            last.VerticalAlignment = VerticalAlignment.Center;
            Cell(grid, r, 7, last);
            r++;
        }
        return new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    private static void Cell(Grid grid, int row, int column, FrameworkElement element)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    // MARK: - Pieces

    /// <summary>An element that takes a whole row of the strip.</summary>
    private static UIElement Whole(FrameworkElement element)
    {
        GraphFlowPanel.SetUnits(element, WidgetRowPacking.FullRow);
        return element;
    }

    /// <summary>A chart that takes whatever height the card has, with an optional header above and a legend below.</summary>
    private static Grid Docked(UIElement chart, UIElement legend, UIElement? header = null)
    {
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (header != null) panel.Children.Add(header);
        Grid.SetRow((FrameworkElement)chart, 1);
        Grid.SetRow((FrameworkElement)legend, 2);
        panel.Children.Add(chart);
        panel.Children.Add(legend);
        return panel;
    }

    /// <summary>A titled card spanning <paramref name="units"/> of the strip's width.</summary>
    private static Border Card(string title, UIElement content, int units = 1)
    {
        var heading = new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 0, 0, 10) };
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow((FrameworkElement)content, 1);
        body.Children.Add(heading);
        body.Children.Add(content);
        var border = new Border { Child = body, VerticalAlignment = VerticalAlignment.Stretch, Style = (Style)Application.Current.Resources["CardStyle"] };
        AutomationProperties.SetName(border, title);
        GraphFlowPanel.SetUnits(border, units);
        return border;
    }

    /// <summary>The period's totals as a three-by-two grid of figures in one card two units wide.</summary>
    private static UIElement Tiles(RepoStatsReport.Totals totals)
    {
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        for (var i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 2; i++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var index = 0;
        void Tile(string title, string value)
        {
            var label = new TextBlock { Text = title, Foreground = RepoBrushes.Secondary, TextTrimming = TextTrimming.CharacterEllipsis,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
            var figure = new TextBlock { Text = value, FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTipService.SetToolTip(figure, value);
            var tile = new Border
            {
                Child = new StackPanel { Children = { label, figure }, VerticalAlignment = VerticalAlignment.Center },
                Padding = new Thickness(12, 8, 12, 8),
                Background = GraphTheme.Resource("LayerFillColorDefaultBrush"),
                BorderBrush = GraphTheme.Resource("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
            };
            AutomationProperties.SetName(tile, $"{title}: {value}");
            Grid.SetColumn(tile, index % 3);
            Grid.SetRow(tile, index / 3);
            grid.Children.Add(tile);
            index++;
        }
        Tile("Commits", totals.Commits.ToString("N0"));
        Tile("Authors", totals.Authors.ToString("N0"));
        Tile("Files touched", totals.FilesTouched.ToString("N0"));
        Tile("Lines added", "+" + totals.Added.ToString("N0"));
        Tile("Lines removed", "−" + totals.Removed.ToString("N0"));
        Tile("Last commit", RepoShell.Relative(totals.LastCommit));
        return Card("Totals", grid, 2);
    }

    /// <summary>
    /// Label, bar, count and a detail per row. Only the bar carries colour. The
    /// rows share the card's height, so a short list fills its card rather than
    /// leaving an empty lower half.
    /// </summary>
    private static UIElement Ranked(IEnumerable<(string Label, int Value, string Detail)> items, string unit)
    {
        var rows = items.ToList();
        if (rows.Count == 0) return new TextBlock { Text = "Nothing in this period", Foreground = RepoBrushes.Secondary };
        var peak = Math.Max(1, rows.Max(r => r.Value));
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = 180 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 24 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < rows.Count; i++)
        {
            var (label, value, detail) = rows[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 26 });
            var name = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(name, label);
            var share = value / (double)peak;
            var track = new Grid { VerticalAlignment = VerticalAlignment.Center, Height = 12 };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.02, share), GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 1 - share), GridUnitType.Star) });
            track.Children.Add(new Border { Background = RepoBrushes.Accent, CornerRadius = new CornerRadius(3) });
            AutomationProperties.SetName(track, $"{label}: {value} {unit}, {detail}");
            var count = new TextBlock { Text = value.ToString("N0"), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var extra = new TextBlock { Text = detail, Foreground = RepoBrushes.Secondary, VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
            foreach (var (element, column) in new (FrameworkElement, int)[] { (name, 0), (track, 1), (count, 2), (extra, 3) })
            {
                Grid.SetRow(element, i);
                Grid.SetColumn(element, column);
                grid.Children.Add(element);
            }
        }
        return grid;
    }

    private UIElement FileList(IReadOnlyList<RepoStatsReport.PathActivity> files)
    {
        if (files.Count == 0) return new TextBlock { Text = "Nothing in this period", Foreground = RepoBrushes.Secondary };
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var c = 0; c < 3; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var headers = new[] { "File", "Commits", "Added", "Removed" };
        for (var c = 0; c < headers.Length; c++)
            Cell(grid, 0, c, new TextBlock
            {
                Text = headers[c],
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                FontWeight = FontWeights.SemiBold,
                Foreground = RepoBrushes.Secondary,
                HorizontalAlignment = c > 0 ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            });
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 26 });
            var link = new HyperlinkButton
            {
                Content = new TextBlock { Text = file.Path, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 },
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(link, $"Open {file.Path} in Files");
            AutomationProperties.SetName(link, $"Open {file.Path} in Files");
            var path = file.Path;
            link.Click += (_, _) => OpenFile?.Invoke(path);
            var menu = new MenuFlyout();
            menu.Items.Add(RowMenus.Item("Open in Files", () => OpenFile?.Invoke(path), glyph: "\uE70F"));
            menu.Items.Add(RowMenus.Item("Copy Path", () => DevelopmentUi.Copy(path), glyph: "\uE8C8"));
            link.ContextFlyout = menu;
            Cell(grid, i + 1, 0, link);
            Cell(grid, i + 1, 1, Number(file.Commits.ToString("N0")));
            Cell(grid, i + 1, 2, Number($"+{file.Added:N0}"));
            Cell(grid, i + 1, 3, Number($"−{file.Removed:N0}"));
        }
        return grid;
    }

    private static TextBlock Number(string text) => new() { Text = text, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };

    private static UIElement Legend(params (string Name, Brush Brush)[] items)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (name, brush) in items)
        {
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            entry.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = brush, VerticalAlignment = VerticalAlignment.Center });
            entry.Children.Add(new TextBlock { Text = name, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(entry);
        }
        return new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
        };
    }
}
