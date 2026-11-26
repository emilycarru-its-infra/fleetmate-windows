using FleetMate.Core.Development;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace FleetMate.WinUI.Pages.Development.Graphs;

/// <summary>
/// The two drawn charts: a donut of Path arcs with a legend that carries every
/// count, and columns per day or week, stacked when there is more than one
/// series. Both are native shapes, themed through <see cref="GraphTheme"/>.
/// </summary>
internal static class GraphCharts
{
    private const double DonutSize = 116;
    private const double CompactDonutSize = 88;
    private const double DonutThickness = 16;

    /// <summary>Below this width the legend moves under a smaller donut.</summary>
    private const double DonutSideBySide = 300;

    // MARK: - Donut

    /// <summary>
    /// A donut with the total in its hole and a legend beside it (under it in a
    /// narrow card). The legend rows are the keyboard stops; a wedge's click
    /// opens the same thing as its row.
    /// </summary>
    public static FrameworkElement Donut(GraphCard card, Action<GraphFilter> open)
    {
        var bars = card.Bars;
        var total = bars.Sum(b => Math.Max(0, b.Value));
        var canvas = new Canvas { Width = DonutSize, Height = DonutSize };
        var hole = new TextBlock
        {
            Text = total.ToString("N0"),
            Style = GraphTheme.Style("SubtitleTextBlockStyle"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var ring = new Grid { Children = { canvas, hole }, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAccessibilityView(ring, AccessibilityView.Raw);

        void Draw(double size)
        {
            canvas.Children.Clear();
            canvas.Width = canvas.Height = size;
            var radius = (size - DonutThickness) / 2;
            var center = new Point(size / 2, size / 2);
            var shown = bars.Select((b, i) => (Bar: b, Index: i)).Where(x => x.Bar.Value > 0).ToList();
            var gap = shown.Count > 1 ? 2.0 / radius : 0;
            var start = -Math.PI / 2;
            foreach (var (bar, index) in shown)
            {
                var brush = GraphTheme.Bar(bar.Tone, index);
                var sweep = 2 * Math.PI * bar.Value / Math.Max(1, total);
                Shape wedge;
                if (shown.Count == 1)
                {
                    wedge = new Ellipse { Width = size - DonutThickness, Height = size - DonutThickness, Stroke = brush, StrokeThickness = DonutThickness };
                    Canvas.SetLeft(wedge, DonutThickness / 2);
                    Canvas.SetTop(wedge, DonutThickness / 2);
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
                    wedge = new Microsoft.UI.Xaml.Shapes.Path
                    {
                        Data = new PathGeometry { Figures = { figure } },
                        Stroke = brush,
                        StrokeThickness = DonutThickness,
                    };
                }
                ToolTipService.SetToolTip(wedge, bar.Description);
                if (bar.Open is { } target) wedge.Tapped += (_, _) => open(target);
                canvas.Children.Add(wedge);
                start += sweep;
            }
        }

        var legend = GraphCardView.ChartGroup(new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center }, card.Title);
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var row = new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions = { new() { Width = GridLength.Auto }, new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } },
            };
            var swatch = new Ellipse { Width = 8, Height = 8, Fill = GraphTheme.Bar(bar.Tone, i), VerticalAlignment = VerticalAlignment.Center };
            var label = GraphTheme.Caption(bar.Label, secondary: false, wrap: true);
            var count = GraphTheme.Caption(bar.Value.ToString("N0"));
            Grid.SetColumn(label, 1);
            Grid.SetColumn(count, 2);
            row.Children.Add(swatch);
            row.Children.Add(label);
            row.Children.Add(count);
            legend.Children.Add(GraphCardView.Item(row, bar.Description, $"{GraphCardView.Slug(card.Title)}{i}",
                bar.Open is { } target ? () => open(target) : null));
        }

        var layout = new Grid { ColumnSpacing = 16, RowSpacing = 8 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(ring);
        layout.Children.Add(legend);

        var compact = (bool?)null;
        void Arrange(double width)
        {
            var narrow = width > 0 && width < DonutSideBySide;
            if (compact == narrow) return;
            compact = narrow;
            Draw(narrow ? CompactDonutSize : DonutSize);
            Grid.SetColumn(legend, narrow ? 0 : 1);
            Grid.SetRow(legend, narrow ? 1 : 0);
            Grid.SetColumnSpan(legend, narrow ? 2 : 1);
            ring.HorizontalAlignment = narrow ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            Grid.SetColumnSpan(ring, narrow ? 2 : 1);
        }
        Arrange(DonutSideBySide);
        layout.SizeChanged += (_, e) => Arrange(e.NewSize.Width);
        return layout;
    }

    private static Point PointAt(Point center, double radius, double angle) =>
        new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));

    // MARK: - Columns

    /// <summary>
    /// One column per day or week, stacked by series, with the busiest
    /// period's count and zero on the left and dates under it. Each column is a
    /// keyboard stop naming its period and values.
    /// </summary>
    public static FrameworkElement Columns(GraphCard card)
    {
        var count = card.Columns.Count;
        var totals = Enumerable.Range(0, count).Select(i => card.Series.Sum(s => Math.Max(0, s.Values[i]))).ToList();
        var max = Math.Max(1, totals.DefaultIfEmpty(0).Max());

        var plot = GraphCardView.ChartGroup(new Grid { ColumnSpacing = count > 10 ? 4 : 8, MinHeight = 110 }, card.Title);
        for (var i = 0; i < count; i++)
        {
            plot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var stack = new Grid();
            stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(max - totals[i], GridUnitType.Star) });
            // Bottom-up: the first series sits on the baseline.
            for (var s = card.Series.Count - 1; s >= 0; s--)
            {
                var value = Math.Max(0, card.Series[s].Values[i]);
                stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(value, GridUnitType.Star) });
                if (value == 0) continue;
                var segment = new Border
                {
                    Background = GraphTheme.Bar(card.Series[s].Tone, s),
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(0, 1, 0, 0),
                };
                Grid.SetRow(segment, stack.RowDefinitions.Count - 1);
                stack.Children.Add(segment);
            }
            var column = card.Columns[i];
            var item = GraphCardView.Item(stack, column.Description, $"{GraphCardView.Slug(card.Title)}{i}", null);
            item.Padding = new Thickness(0);
            item.MinWidth = 0;
            Grid.SetColumn(item, i);
            plot.Children.Add(item);
        }

        var baseline = new Rectangle { Height = 1, Fill = GraphTheme.Track, VerticalAlignment = VerticalAlignment.Bottom };
        var scale = new Grid
        {
            RowDefinitions = { new() { Height = GridLength.Auto }, new() { Height = new GridLength(1, GridUnitType.Star) }, new() { Height = GridLength.Auto } },
        };
        var top = GraphTheme.Caption(max.ToString("N0"));
        var zero = GraphTheme.Caption("0");
        Grid.SetRow(zero, 2);
        top.HorizontalAlignment = zero.HorizontalAlignment = HorizontalAlignment.Right;
        scale.Children.Add(top);
        scale.Children.Add(zero);
        AutomationProperties.SetAccessibilityView(scale, AccessibilityView.Raw);

        var axis = DateAxis(card, plot);

        var chart = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new() { Width = GridLength.Auto }, new() { Width = new GridLength(1, GridUnitType.Star) } },
            RowDefinitions = { new() { Height = new GridLength(1, GridUnitType.Star) }, new() { Height = GridLength.Auto } },
        };
        Grid.SetColumn(plot, 1);
        Grid.SetColumn(baseline, 1);
        Grid.SetColumn(axis, 1);
        Grid.SetRow(axis, 1);
        chart.Children.Add(scale);
        chart.Children.Add(plot);
        chart.Children.Add(baseline);
        chart.Children.Add(axis);

        if (!card.Legend) return chart;

        var legend = new GraphWrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (series, s) in card.Series.Select((x, s) => (x, s)))
        {
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            entry.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = GraphTheme.Bar(series.Tone, s), VerticalAlignment = VerticalAlignment.Center });
            entry.Children.Add(GraphTheme.Caption(series.Name, secondary: false, wrap: true));
            legend.Children.Add(entry);
        }
        Grid.SetRow(legend, 1);
        return new Grid
        {
            RowDefinitions = { new() { Height = new GridLength(1, GridUnitType.Star) }, new() { Height = GridLength.Auto } },
            Children = { chart, legend },
        };
    }

    /// <summary>
    /// Date labels under the columns, as many as fit without touching: every
    /// column when there is room, otherwise every second or third, always the
    /// newest. A canvas, so a label wider than its column is never cut.
    /// </summary>
    private static FrameworkElement DateAxis(GraphCard card, Grid plot)
    {
        var canvas = new Canvas { Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetAccessibilityView(canvas, AccessibilityView.Raw);
        var labels = card.Columns.Select(c => GraphTheme.Caption(c.Axis)).ToList();
        foreach (var label in labels)
        {
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            canvas.Children.Add(label);
        }
        canvas.Height = labels.Count == 0 ? 0 : labels.Max(l => l.DesiredSize.Height);

        void Place(double width)
        {
            var n = labels.Count;
            if (n == 0 || width <= 0) return;
            var slot = width / n;
            var widest = labels.Max(l => l.DesiredSize.Width) + 8;
            var step = Math.Max(1, (int)Math.Ceiling(widest / slot));
            for (var i = 0; i < n; i++)
            {
                var label = labels[i];
                var shown = (n - 1 - i) % step == 0;
                label.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
                var x = slot * (i + 0.5) - label.DesiredSize.Width / 2;
                Canvas.SetLeft(label, Math.Clamp(x, 0, Math.Max(0, width - label.DesiredSize.Width)));
            }
        }
        plot.SizeChanged += (_, e) => Place(e.NewSize.Width);
        return canvas;
    }
}
