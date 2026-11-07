using System.ComponentModel;
using FleetMate.Core.Models.Tickets;
using FleetMate.WinUI.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace FleetMate.WinUI.Pages;

public sealed partial class TicketsPage : Page, INotifyPropertyChanged
{
    private static readonly string[] BoardColumns = ["New", "In Process", "Awaiting Input", "On Hold"];

    private readonly List<TicketRow> _all = SampleTickets.Tickets.Select(t => new TicketRow(t)).ToList();
    private List<FeedItem> _feed = [];
    private string _query = string.Empty;
    private TicketRow? _selected;

    public TicketsPage()
    {
        InitializeComponent();
        MainWindow.SearchChanged += (_, q) => { _query = q; Refresh(); };
        MainWindow.ViewModeChanged += (_, _) => ApplyViewMode();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The ticket in the detail pane.</summary>
    public TicketRow? Selected
    {
        get => _selected;
        private set
        {
            if (ReferenceEquals(_selected, value) || value is null) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            _feed = SampleTickets.FeedFor(value.Ticket).Select(e => new FeedItem(e)).ToList();
            TaskRepeater.ItemsSource = SampleTickets.TasksFor(value.Ticket);
            TasksExpander.Header = $"Tasks  ·  {SampleTickets.TasksFor(value.Ticket).Count(t => t.Done)} of 3 done";
            ApplyFeedFilter();
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_selected is null) Refresh();
        ApplyViewMode();
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) Refresh();
    }

    private IEnumerable<TicketRow> Visible()
    {
        var activeOnly = StateBox.SelectedIndex != 1;
        return _all.Where(r =>
            (!activeOnly || !TicketStats.IsClosed(r.Ticket))
            && QueueBox.SelectedIndex switch
            {
                1 => r.Responsible == "Jordan Lee",
                2 => r.IsUnassigned,
                _ => true,
            }
            && r.Matches(_query));
    }

    private void Refresh()
    {
        var rows = Visible()
            .OrderByDescending(r => r.Ticket.IsSlaViolated)
            .ThenBy(r => r.Ticket.DaysOld)
            .ToList();

        TicketList.ItemsSource = rows;
        if (rows.Count > 0)
        {
            TicketList.SelectedItem = rows.Contains(_selected!) ? _selected : rows[0];
        }

        var stats = new TicketStats(rows.Select(r => r.Ticket));
        ShowingText.Text = $"{stats.Total} shown · sample data";
        ShowingValue.Text = stats.Total.ToString();
        OpenValue.Text = stats.Open.ToString();
        OnHoldValue.Text = stats.OnHold.ToString();
        UnassignedValue.Text = stats.UnassignedCount.ToString();
        SlaValue.Text = stats.SlaViolated.ToString();
        AgingValue.Text = stats.Aging.ToString();

        DrawDonut(stats.ByStatus, stats.Total);
        FillBars(PriorityBars, stats.ByPriority);
        FillBars(AgeBars, stats.ByAge);
        FillBars(ResponsibleBars, stats.ByResponsible);
        FillBars(GroupBars, stats.ByGroup);
        FillBoard(rows);
    }

    private void TicketList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Selected = TicketList.SelectedItem as TicketRow;

    private void FeedFilter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        ApplyFeedFilter();

    private void ApplyFeedFilter()
    {
        var tag = FeedFilter.SelectedItem?.Tag as string ?? "All";
        FeedRepeater.ItemsSource = tag == "All"
            ? _feed
            : _feed.Where(f => f.Kind.ToString() == tag).ToList();
    }

    private void ApplyViewMode()
    {
        var board = MainWindow.ViewMode == "board";
        ListCard.Visibility = board ? Visibility.Collapsed : Visibility.Visible;
        BoardGrid.Visibility = board ? Visibility.Visible : Visibility.Collapsed;
    }

    // Donut: one arc per status, a 2px surface gap between segments, the total in the hole.
    private void DrawDonut(IReadOnlyList<TicketStats.Count> counts, int total)
    {
        DonutCanvas.Children.Clear();
        StatusLegend.Children.Clear();
        DonutTotal.Text = total.ToString();
        if (total == 0) return;

        const double size = 104, thickness = 14;
        var radius = (size - thickness) / 2;
        var center = new Point(size / 2, size / 2);
        var gap = counts.Count > 1 ? 2.0 / radius : 0;
        var start = -Math.PI / 2;

        foreach (var c in counts)
        {
            var brush = TicketRow.SeriesBrush(TicketRow.StatusSlot(c.Label));
            var sweep = 2 * Math.PI * c.Value / total;
            if (counts.Count == 1)
            {
                var ring = new Ellipse { Width = size - thickness, Height = size - thickness, Stroke = brush, StrokeThickness = thickness };
                Canvas.SetLeft(ring, thickness / 2);
                Canvas.SetTop(ring, thickness / 2);
                DonutCanvas.Children.Add(ring);
            }
            else
            {
                var a0 = start + gap / 2;
                var a1 = start + sweep - gap / 2;
                var figure = new PathFigure { StartPoint = PointAt(center, radius, a0), IsClosed = false };
                figure.Segments.Add(new ArcSegment
                {
                    Point = PointAt(center, radius, a1),
                    Size = new Size(radius, radius),
                    IsLargeArc = a1 - a0 > Math.PI,
                    SweepDirection = SweepDirection.Clockwise,
                });
                var geometry = new PathGeometry();
                geometry.Figures.Add(figure);
                var path = new Path { Data = geometry, Stroke = brush, StrokeThickness = thickness };
                ToolTipService.SetToolTip(path, $"{c.Label}: {c.Value}");
                DonutCanvas.Children.Add(path);
            }
            start += sweep;

            StatusLegend.Children.Add(LegendRow(brush, c.Label, c.Value));
        }
        AutomationProperties.SetName(DonutCanvas,
            "By status: " + string.Join(", ", counts.Select(c => $"{c.Label} {c.Value}")));
    }

    private static Point PointAt(Point center, double radius, double angle) =>
        new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));

    private static Grid LegendRow(Brush swatch, string label, int value)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = swatch, VerticalAlignment = VerticalAlignment.Center });
        var name = Caption(label, "TextFillColorPrimaryBrush");
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        var count = Caption(value.ToString(), "TextFillColorSecondaryBrush");
        Grid.SetColumn(count, 2);
        row.Children.Add(count);
        return row;
    }

    // Horizontal bars: one hue, label left, value right, a faint track behind each bar.
    private static void FillBars(StackPanel host, IReadOnlyList<TicketStats.Count> counts)
    {
        host.Children.Clear();
        if (counts.Count == 0)
        {
            host.Children.Add(Caption("Nothing to show", "TextFillColorTertiaryBrush"));
            return;
        }

        var max = counts.Max(c => c.Value);
        foreach (var c in counts)
        {
            var row = new Grid { ColumnSpacing = 8, Height = 16 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });

            var label = Caption(c.Label, c.Label == TicketStats.Unassigned ? "TextFillColorTertiaryBrush" : "TextFillColorPrimaryBrush");
            row.Children.Add(label);

            var track = new Grid
            {
                Height = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Background = (Brush)Application.Current.Resources["BarTrackBrush"],
                CornerRadius = new CornerRadius(4),
            };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(c.Value, GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(max - c.Value, GridUnitType.Star) });
            track.Children.Add(new Border { Background = TicketRow.SeriesBrush(1), CornerRadius = new CornerRadius(4) });
            Grid.SetColumn(track, 1);
            ToolTipService.SetToolTip(track, $"{c.Label}: {c.Value}");
            row.Children.Add(track);

            var value = Caption(c.Value.ToString(), "TextFillColorSecondaryBrush");
            value.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(value, 2);
            row.Children.Add(value);

            AutomationProperties.SetName(row, $"{c.Label}: {c.Value}");
            host.Children.Add(row);
        }
    }

    private static TextBlock Caption(string text, string brushKey) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources[brushKey],
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxLines = 1,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // Board: one column per active status, each its own vertical list. Columns share the width.
    private void FillBoard(List<TicketRow> rows)
    {
        BoardGrid.Children.Clear();
        BoardGrid.ColumnDefinitions.Clear();
        for (var i = 0; i < BoardColumns.Length; i++)
        {
            var status = BoardColumns[i];
            var items = rows.Where(r => r.Status == status).ToList();
            BoardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var column = new Grid { RowSpacing = 8 };
            column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(4, 0, 0, 0) };
            header.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = TicketRow.SeriesBrush(TicketRow.StatusSlot(status)), VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new TextBlock { Text = status, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            header.Children.Add(Caption(items.Count.ToString(), "TextFillColorSecondaryBrush"));
            column.Children.Add(header);

            var list = new ListView
            {
                ItemsSource = items,
                ItemTemplate = (DataTemplate)Resources["BoardCardTemplate"],
                SelectionMode = ListViewSelectionMode.None,
                IsItemClickEnabled = true,
            };
            ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Disabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            list.ItemClick += (_, e) => Selected = e.ClickedItem as TicketRow;
            AutomationProperties.SetName(list, status);
            Grid.SetRow(list, 1);
            column.Children.Add(list);

            Grid.SetColumn(column, i);
            BoardGrid.Children.Add(column);
        }
    }
}
