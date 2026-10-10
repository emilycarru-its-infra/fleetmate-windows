using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using SkiaSharp;

namespace FleetMate.GUI.Views.Shared.Widgets;

/// <summary>One KPI tile: a big number with a label, stacked with others in one card.</summary>
public sealed record KpiTile(string Title, string Value, string Glyph, string ColorHex);

/// <summary>One bar or slice; <see cref="Label"/> is also the filter value a click applies.</summary>
public sealed record ChartSlice(string Label, int Value);

/// <summary>One row in a list card.</summary>
public sealed record ListRow(string Title, string Detail, Action? Open);

/// <summary>
/// Builders for the cards a Widgets section holds. Everything is themed for
/// light and dark: LiveCharts paints its own text and knows nothing about
/// the WPF theme, so every legend and axis gets a paint that matches it.
/// </summary>
public static class WidgetCards
{
    public const double CardMaxHeight = 240;
    private const double ChartHeight = 170;

    private static readonly SKColor[] Palette =
    {
        new(33, 150, 243), new(76, 175, 80), new(255, 152, 0), new(156, 39, 176),
        new(0, 150, 136), new(121, 85, 72), new(63, 81, 181), new(158, 158, 158),
    };

    private static bool IsDark =>
        ModernWpf.ThemeManager.Current.ActualApplicationTheme == ModernWpf.ApplicationTheme.Dark;

    private static SolidColorPaint TextPaint => new(IsDark ? new SKColor(255, 255, 255, 222) : new SKColor(0, 0, 0, 200));

    private static Axis ValueAxis() => new()
    {
        LabelsPaint = TextPaint,
        TextSize = 10,
        SeparatorsPaint = new SolidColorPaint(IsDark ? new SKColor(255, 255, 255, 30) : new SKColor(0, 0, 0, 25)),
    };

    private static Brush Medium => (Brush)Application.Current.FindResource("SystemControlForegroundBaseMediumBrush");

    /// <summary>A titled card. Clicking the title runs <paramref name="onTitle"/> when given.</summary>
    public static Border Card(string title, UIElement content, int units = 1, Action? onTitle = null)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold });
        if (onTitle != null)
        {
            header.Children.Add(new TextBlock
            {
                Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10,
                Foreground = Medium, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0),
            });
            header.Cursor = Cursors.Hand;
            header.Background = Brushes.Transparent;
            header.MouseLeftButtonUp += (_, _) => onTitle();
        }

        var card = new Border
        {
            Style = (Style)Application.Current.FindResource("CardStyle"),
            MaxHeight = CardMaxHeight,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new DockPanel { Children = { header, content } },
        };
        DockPanel.SetDock(header, Dock.Top);
        WidgetFlowPanel.SetUnits(card, units);
        return card;
    }

    /// <summary>KPI tiles stacked in one cell.</summary>
    public static Border KpiStack(IEnumerable<KpiTile> tiles, Action? onClick = null)
    {
        var stack = new StackPanel();
        foreach (var tile in tiles)
        {
            var color = (Color)ColorConverter.ConvertFromString(tile.ColorHex);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            row.Children.Add(new TextBlock
            {
                Text = tile.Glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 20,
                Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            });
            row.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = tile.Value, FontSize = 22, FontWeight = FontWeights.Bold },
                    new TextBlock { Text = tile.Title, FontSize = 11, Foreground = Medium },
                },
            });
            stack.Children.Add(row);
        }

        var card = new Border
        {
            Style = (Style)Application.Current.FindResource("CardStyle"),
            MaxHeight = CardMaxHeight,
            VerticalAlignment = VerticalAlignment.Top,
            Child = stack,
        };
        if (onClick != null)
        {
            card.Cursor = Cursors.Hand;
            card.MouseLeftButtonUp += (_, _) => onClick();
        }
        WidgetFlowPanel.SetUnits(card, 1);
        return card;
    }

    /// <summary>Vertical bars, one colour per bar; a click passes the bar's label.</summary>
    public static UIElement Bars(IReadOnlyList<ChartSlice> slices, Action<string>? onClick = null, IReadOnlyList<SKColor>? colors = null)
    {
        var series = slices.Select((s, i) => (ISeries)new ColumnSeries<int>
        {
            Values = new[] { s.Value },
            Name = s.Label,
            Fill = new SolidColorPaint((colors ?? Palette)[i % (colors ?? Palette).Count]),
        }).ToArray();

        var chart = new CartesianChart
        {
            Height = ChartHeight,
            LegendPosition = LiveChartsCore.Measure.LegendPosition.Hidden,
            XAxes = new Axis[] { new() { Labels = slices.Select(s => s.Label).ToArray(), LabelsRotation = 15, TextSize = 10, LabelsPaint = TextPaint, SeparatorsPaint = null } },
            YAxes = new Axis[] { ValueAxis() },
            Series = series,
        };
        Wire(chart, series, slices, onClick);
        return chart;
    }

    /// <summary>A donut with a legend; a click passes the slice's label.</summary>
    public static UIElement Donut(IReadOnlyList<ChartSlice> slices, Action<string>? onClick = null, IReadOnlyList<SKColor>? colors = null)
    {
        var series = slices.Select((s, i) => (ISeries)new PieSeries<int>
        {
            Values = new[] { s.Value },
            Name = $"{s.Label} ({s.Value})",
            Fill = new SolidColorPaint((colors ?? Palette)[i % (colors ?? Palette).Count]),
            InnerRadius = 45,
        }).ToArray();

        var chart = new PieChart
        {
            Height = ChartHeight,
            LegendPosition = LiveChartsCore.Measure.LegendPosition.Right,
            LegendTextPaint = TextPaint,
            Series = series,
        };
        Wire(chart, series, slices, onClick);
        return chart;
    }

    /// <summary>
    /// Horizontal bars, one row per value with its label, bar and count, so
    /// long names (people, groups) stay readable. A click passes the label.
    /// <paramref name="faded"/> values draw at reduced opacity.
    /// </summary>
    public static UIElement HorizontalBars(IReadOnlyList<ChartSlice> slices, Func<string, Color> colorOf,
        Action<string>? onClick = null, Func<string, bool>? faded = null)
    {
        var max = Math.Max(1, slices.Count == 0 ? 1 : slices.Max(s => s.Value));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MaxWidth = 160 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        for (var i = 0; i < slices.Count; i++)
        {
            var slice = slices[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var opacity = faded?.Invoke(slice.Label) == true ? 0.3 : 1.0;

            var label = new TextBlock
            {
                Text = slice.Label, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 8, 3), ToolTip = slice.Label,
                Opacity = opacity,
            };
            var track = new Grid { Margin = new Thickness(0, 3, 8, 3), Opacity = opacity };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(slice.Value, 0), GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(max - Math.Max(slice.Value, 0), GridUnitType.Star) });
            var bar = new Border { Height = 10, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(colorOf(slice.Label)) };
            track.Children.Add(bar);
            var count = new TextBlock
            {
                Text = slice.Value.ToString(), FontSize = 11, Foreground = Medium,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 3), Opacity = opacity,
            };

            Grid.SetRow(label, i); Grid.SetColumn(label, 0);
            Grid.SetRow(track, i); Grid.SetColumn(track, 1);
            Grid.SetRow(count, i); Grid.SetColumn(count, 2);
            grid.Children.Add(label);
            grid.Children.Add(track);
            grid.Children.Add(count);

            if (onClick != null)
            {
                // One hit target across the whole row, so a short bar is as easy to click as a long one.
                var hit = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = $"{slice.Label}: {slice.Value}" };
                hit.MouseLeftButtonUp += (_, _) => onClick(slice.Label);
                Grid.SetRow(hit, i);
                Grid.SetColumnSpan(hit, 3);
                grid.Children.Add(hit);
            }
        }

        return grid;
    }

    private static void Wire(UIElement chart, ISeries[] series, IReadOnlyList<ChartSlice> slices, Action<string>? onClick)
    {
        if (onClick == null) return;
        var labels = series.Select((s, i) => (s, slices[i].Label)).ToDictionary(x => x.s, x => x.Label);

        void Pick(IEnumerable<ChartPoint> points)
        {
            var point = points.FirstOrDefault();
            if (point?.Context.Series is ISeries s && labels.TryGetValue(s, out var label)) onClick(label);
        }

        switch (chart)
        {
            case CartesianChart c: c.DataPointerDown += (_, points) => Pick(points); c.Cursor = Cursors.Hand; break;
            case PieChart p: p.DataPointerDown += (_, points) => Pick(points); p.Cursor = Cursors.Hand; break;
        }
    }

    /// <summary>
    /// A treemap: tiles sized by share, laid out slice-and-dice (alternating
    /// split direction). LiveCharts has no treemap; this is enough for a
    /// handful of platforms.
    /// </summary>
    public static UIElement Treemap(IReadOnlyList<ChartSlice> slices, Action<string>? onClick = null)
    {
        var canvas = new Canvas { Height = ChartHeight, ClipToBounds = true };

        void Layout()
        {
            canvas.Children.Clear();
            var w = canvas.ActualWidth;
            if (w <= 0) return;
            foreach (var (slice, rect, index) in TreemapRects(slices, new Rect(0, 0, w, ChartHeight)))
            {
                var c = Palette[index % Palette.Length];
                var tile = new Border
                {
                    Width = Math.Max(0, rect.Width - 2),
                    Height = Math.Max(0, rect.Height - 2),
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Color.FromArgb(220, c.Red, c.Green, c.Blue)),
                    ToolTip = $"{slice.Label}: {slice.Value}",
                    Child = new TextBlock
                    {
                        Text = $"{slice.Label}\n{slice.Value}",
                        Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(6, 4, 4, 4),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                };
                if (onClick != null)
                {
                    tile.Cursor = Cursors.Hand;
                    tile.MouseLeftButtonUp += (_, _) => onClick(slice.Label);
                }
                Canvas.SetLeft(tile, rect.X);
                Canvas.SetTop(tile, rect.Y);
                canvas.Children.Add(tile);
            }
        }

        canvas.SizeChanged += (_, _) => Layout();
        return canvas;
    }

    /// <summary>Slice-and-dice rectangles, largest first, alternating horizontal and vertical splits.</summary>
    internal static List<(ChartSlice Slice, Rect Rect, int Index)> TreemapRects(IReadOnlyList<ChartSlice> slices, Rect bounds)
    {
        var ordered = slices.Where(s => s.Value > 0).OrderByDescending(s => s.Value).ToList();
        var result = new List<(ChartSlice, Rect, int)>();
        var remaining = bounds;
        var total = (double)ordered.Sum(s => s.Value);

        for (var i = 0; i < ordered.Count; i++)
        {
            var slice = ordered[i];
            if (i == ordered.Count - 1 || total <= 0)
            {
                result.Add((slice, remaining, i));
                break;
            }

            var share = slice.Value / total;
            Rect rect;
            if (remaining.Width >= remaining.Height)
            {
                rect = new Rect(remaining.X, remaining.Y, remaining.Width * share, remaining.Height);
                remaining = new Rect(rect.Right, remaining.Y, remaining.Width - rect.Width, remaining.Height);
            }
            else
            {
                rect = new Rect(remaining.X, remaining.Y, remaining.Width, remaining.Height * share);
                remaining = new Rect(remaining.X, rect.Bottom, remaining.Width, remaining.Height - rect.Height);
            }

            result.Add((slice, rect, i));
            total -= slice.Value;
        }

        return result;
    }

    /// <summary>
    /// A list card body: the first <paramref name="visible"/> rows, then
    /// "Show all N" which opens every row in a window.
    /// </summary>
    public static UIElement List(string title, IReadOnlyList<ListRow> rows, int visible = 5)
    {
        var stack = new StackPanel();
        if (rows.Count == 0)
        {
            stack.Children.Add(new TextBlock { Text = "Nothing here.", FontSize = 11, Foreground = Medium });
            return stack;
        }

        foreach (var row in rows.Take(visible)) stack.Children.Add(Row(row));

        if (rows.Count > visible)
        {
            var more = new Button
            {
                Content = $"Show all {rows.Count}", FontSize = 11, Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            };
            more.Click += (_, _) => WidgetListWindow.Show(title, rows);
            stack.Children.Add(more);
        }

        return stack;
    }

    internal static FrameworkElement Row(ListRow row)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent };
        panel.Children.Add(new TextBlock { Text = row.Title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = row.Title });
        if (!string.IsNullOrEmpty(row.Detail))
            panel.Children.Add(new TextBlock { Text = row.Detail, FontSize = 10, Foreground = Medium, TextTrimming = TextTrimming.CharacterEllipsis });
        if (row.Open != null)
        {
            panel.Cursor = Cursors.Hand;
            panel.MouseLeftButtonUp += (_, _) => row.Open();
        }
        return panel;
    }

    public static TextBlock Caption(string text) =>
        new() { Text = text, FontSize = 10, Margin = new Thickness(0, 4, 0, 0), Foreground = Medium };
}

/// <summary>The full list behind a list card's "Show all N".</summary>
public sealed class WidgetListWindow : Window
{
    public static void Show(string title, IReadOnlyList<ListRow> rows)
    {
        var stack = new StackPanel { Margin = new Thickness(16) };
        foreach (var row in rows) stack.Children.Add(WidgetCards.Row(row));

        var window = new WidgetListWindow
        {
            Title = $"{title} ({rows.Count})",
            Width = 640,
            Height = 560,
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack },
        };
        window.SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        window.SetResourceReference(ForegroundProperty, "SystemControlForegroundBaseHighBrush");
        ModernWpf.Controls.Primitives.WindowHelper.SetUseModernWindowStyle(window, true);
        window.Show();
    }
}
