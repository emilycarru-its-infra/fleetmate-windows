using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using SkiaSharp;

namespace FleetMate.GUI.Views.Shared.Widgets;

/// <summary>
/// One KPI tile: a big number with a label. <see cref="Loading"/> shows a
/// skeleton in place of the number; <see cref="Open"/>, when set, makes the
/// tile a button.
/// </summary>
public sealed record KpiTile(string Title, string Value, string Glyph, string ColorHex, bool Loading = false, Action? Open = null);

/// <summary>One bar or slice; <see cref="Label"/> is also the filter value a click applies.</summary>
public sealed record ChartSlice(string Label, int Value);

/// <summary>One row in a list card.</summary>
public sealed record ListRow(string Title, string Detail, Action? Open);

/// <summary>The named chart colours, matching the macOS app's. No red.</summary>
public static class WidgetPalette
{
    public static readonly Color Blue = Color.FromRgb(33, 150, 243);
    public static readonly Color Green = Color.FromRgb(76, 175, 80);
    public static readonly Color Orange = Color.FromRgb(255, 152, 0);
    public static readonly Color Purple = Color.FromRgb(156, 39, 176);
    public static readonly Color Teal = Color.FromRgb(0, 150, 136);
    public static readonly Color Brown = Color.FromRgb(121, 85, 72);
    public static readonly Color Indigo = Color.FromRgb(63, 81, 181);
    public static readonly Color Pink = Color.FromRgb(236, 64, 122);
    public static readonly Color Gray = Color.FromRgb(158, 158, 158);

    public static SKColor Sk(Color c) => new(c.R, c.G, c.B, c.A);

    public static string Hex(Color c) => $"#FF{c.R:X2}{c.G:X2}{c.B:X2}";
}

/// <summary>
/// Builders for the cards a Widgets section holds, matching the macOS app's
/// components. Everything is themed for light and dark: LiveCharts paints its
/// own text and knows nothing about the WPF theme.
/// </summary>
public static class WidgetCards
{
    public const double CardMaxHeight = 240;
    private const double DonutSize = 130;
    private const double TreemapHeight = 120;

    /// <summary>The colours a chart falls back to when its caller gives none.</summary>
    private static readonly Color[] Palette =
    {
        WidgetPalette.Blue, WidgetPalette.Green, WidgetPalette.Orange, WidgetPalette.Purple,
        WidgetPalette.Teal, WidgetPalette.Brown, WidgetPalette.Indigo, WidgetPalette.Gray,
    };

    /// <summary>Hovered marks draw at this opacity, as on the Mac.</summary>
    private const double HoverOpacity = 0.72;

    private static Brush Medium => (Brush)Application.Current.FindResource("SystemControlForegroundBaseMediumBrush");

    private static Brush Subtle => new SolidColorBrush(Color.FromArgb(24, 128, 128, 128));

    /// <summary>
    /// A titled card. Clicking the title runs <paramref name="onTitle"/> when
    /// given; <paramref name="loading"/> puts a spinner beside the title.
    /// </summary>
    public static Border Card(string title, UIElement content, int units = 1, Action? onTitle = null, bool loading = false)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold });
        if (onTitle != null)
        {
            header.Children.Add(new TextBlock
            {
                Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10,
                Foreground = Medium, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0),
            });
            header.Cursor = Cursors.Hand;
            header.Background = Brushes.Transparent;
            header.MouseLeftButtonUp += (_, _) => onTitle();
        }
        if (loading)
        {
            header.Children.Add(new ModernWpf.Controls.ProgressRing
            {
                IsActive = true, Width = 12, Height = 12, Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
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

    /// <summary>
    /// A chart card's body: the chart when there is data, a skeleton while it
    /// loads, and <paramref name="emptyMessage"/> otherwise.
    /// </summary>
    public static UIElement ChartOr(bool hasData, bool loading, string emptyMessage, Func<UIElement> chart) =>
        hasData ? chart() : loading ? Skeleton() : Empty(emptyMessage);

    /// <summary>KPI tiles stacked in one cell, each its own tile; a tile with an action is a button.</summary>
    public static StackPanel KpiStack(IEnumerable<KpiTile> tiles)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        foreach (var tile in tiles)
        {
            var color = (Color)ColorConverter.ConvertFromString(tile.ColorHex);
            var row = new DockPanel();
            var glyph = new TextBlock
            {
                Text = tile.Glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 20,
                Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center,
                Width = 28, Margin = new Thickness(0, 0, 10, 0),
            };
            DockPanel.SetDock(glyph, Dock.Left);
            row.Children.Add(glyph);

            UIElement value = tile.Loading
                ? SkeletonBlock(60, 22, new Thickness(0, 2, 0, 3))
                : new TextBlock { Text = tile.Value, FontSize = 22, FontWeight = FontWeights.Bold };
            row.Children.Add(new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children = { value, new TextBlock { Text = tile.Title, FontSize = 11, Foreground = Medium } },
            });

            var card = new Border
            {
                Style = (Style)Application.Current.FindResource("CardStyle"),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = row,
            };
            System.Windows.Automation.AutomationProperties.SetName(card, $"{tile.Title}: {tile.Value}");
            if (tile.Open is { } open)
            {
                card.Cursor = Cursors.Hand;
                card.MouseEnter += (_, _) => card.Opacity = 0.85;
                card.MouseLeave += (_, _) => card.Opacity = 1;
                card.MouseLeftButtonUp += (_, _) => open();
            }
            stack.Children.Add(card);
        }

        if (stack.Children.Count > 0 && stack.Children[^1] is Border last) last.Margin = new Thickness(0);
        WidgetFlowPanel.SetUnits(stack, 1);
        return stack;
    }

    // MARK: - Donut

    /// <summary>
    /// A donut on the left and a legend carrying every count on the right.
    /// A wedge or a legend row passes its label to <paramref name="onClick"/>.
    /// Colours carry their own alpha, so a faded slice fades in the legend too.
    /// </summary>
    public static UIElement Donut(IReadOnlyList<ChartSlice> slices, Action<string>? onClick = null,
        IReadOnlyList<SKColor>? colors = null, double size = DonutSize)
    {
        var diameter = Math.Min(size, 150);
        var total = slices.Sum(s => s.Value);
        var fills = slices.Select((_, i) => colors?[i % colors.Count] ?? WidgetPalette.Sk(Palette[i % Palette.Length])).ToList();

        var series = slices.Select((s, i) =>
        {
            // Only wedges wide enough to hold a number get one; every count is in the legend.
            var label = WedgeLabel(s.Value, total);
            return (ISeries)new PieSeries<int>
            {
                Values = new[] { s.Value },
                Name = s.Label,
                Fill = new SolidColorPaint(fills[i]),
                InnerRadius = diameter / 2 * 0.55,
                HoverPushout = 0,
                DataLabelsPaint = new SolidColorPaint(SKColors.White),
                DataLabelsSize = 10,
                DataLabelsPosition = PolarLabelsPosition.Middle,
                DataLabelsFormatter = _ => label,
            };
        }).ToArray();

        var chart = new PieChart
        {
            Width = diameter,
            Height = diameter,
            LegendPosition = LegendPosition.Hidden,
            TooltipPosition = TooltipPosition.Hidden,
            Series = series,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Wire(chart, series, slices, onClick);

        var grid = new Grid { MinHeight = diameter };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 110 });
        var legend = Legend(slices, fills, onClick);
        legend.Margin = new Thickness(14, 0, 0, 0);
        legend.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(legend, 1);
        grid.Children.Add(chart);
        grid.Children.Add(legend);
        return grid;
    }

    /// <summary>The count drawn on a wedge: only one at least 12% of the whole gets one.</summary>
    internal static string WedgeLabel(int value, int total) =>
        total > 0 && (double)value / total >= 0.12 ? value.ToString("N0") : "";

    /// <summary>Swatch, label and count per slice. Labels wrap rather than truncate; each row is a button.</summary>
    private static StackPanel Legend(IReadOnlyList<ChartSlice> slices, IReadOnlyList<SKColor> fills, Action<string>? onClick)
    {
        var stack = new StackPanel();
        for (var i = 0; i < slices.Count; i++)
        {
            var slice = slices[i];
            var c = fills[i];
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5), Background = Brushes.Transparent };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var swatch = new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8, Margin = new Thickness(0, 4, 6, 0), VerticalAlignment = VerticalAlignment.Top,
                Fill = new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue)),
            };
            var label = new TextBlock
            {
                Text = DisplayLabel(slice), FontSize = 11, TextWrapping = TextWrapping.Wrap,
                MaxHeight = 30, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var count = new TextBlock
            {
                Text = slice.Value.ToString("N0"), FontSize = 11, FontWeight = FontWeights.Medium,
                Foreground = Medium, Margin = new Thickness(6, 0, 0, 0),
            };
            Grid.SetColumn(label, 1);
            Grid.SetColumn(count, 2);
            row.Children.Add(swatch);
            row.Children.Add(label);
            row.Children.Add(count);

            if (onClick != null)
            {
                row.Cursor = Cursors.Hand;
                row.MouseEnter += (_, _) => row.Background = Subtle;
                row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
                row.MouseLeftButtonUp += (_, _) => onClick(slice.Label);
            }
            stack.Children.Add(row);
        }
        return stack;
    }

    /// <summary>
    /// Some slice labels already end in "(count)"; the legend shows the count
    /// in its own column, so it drops the duplicate.
    /// </summary>
    internal static string DisplayLabel(ChartSlice slice)
    {
        var suffix = $" ({slice.Value})";
        return slice.Label.EndsWith(suffix, StringComparison.Ordinal) ? slice.Label[..^suffix.Length] : slice.Label;
    }

    // MARK: - Horizontal bars

    /// <summary>Colours by position in <paramref name="slices"/>, cycling <paramref name="palette"/>.</summary>
    public static Func<string, Color> ByIndex(IReadOnlyList<ChartSlice> slices, IReadOnlyList<Color> palette)
    {
        var map = new Dictionary<string, Color>();
        for (var i = 0; i < slices.Count; i++) map.TryAdd(slices[i].Label, palette[i % palette.Count]);
        return label => map.TryGetValue(label, out var c) ? c : WidgetPalette.Gray;
    }

    /// <summary>
    /// Label | bar | count rows, so long names stay readable. A row highlights
    /// on hover and a click passes its label. <paramref name="faded"/> values
    /// draw at reduced opacity.
    /// </summary>
    public static UIElement HorizontalBars(IReadOnlyList<ChartSlice> slices, Func<string, Color> colorOf,
        Action<string>? onClick = null, Func<string, bool>? faded = null)
    {
        var max = Math.Max(1, slices.Count == 0 ? 1 : slices.Max(s => s.Value));
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MaxWidth = 160 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 40 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        for (var i = 0; i < slices.Count; i++)
        {
            var slice = slices[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var opacity = faded?.Invoke(slice.Label) == true ? 0.3 : 1.0;

            var label = new TextBlock
            {
                Text = slice.Label, FontSize = 11, Foreground = Medium, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 8, 3), Opacity = opacity,
            };
            var value = Math.Max(slice.Value, 0);
            var track = new Grid { Margin = new Thickness(0, 3, 8, 3), Opacity = opacity };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(value, GridUnitType.Star), MinWidth = value > 0 ? 3 : 0 });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(max - value, GridUnitType.Star) });
            var bar = new Border { Height = 14, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(colorOf(slice.Label)) };
            track.Children.Add(bar);
            var count = new TextBlock
            {
                Text = slice.Value.ToString("N0"), FontSize = 11, FontWeight = FontWeights.Medium, Foreground = Medium,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 3, 0, 3), Opacity = opacity,
            };

            Grid.SetRow(label, i); Grid.SetColumn(label, 0);
            Grid.SetRow(track, i); Grid.SetColumn(track, 1);
            Grid.SetRow(count, i); Grid.SetColumn(count, 2);
            grid.Children.Add(label);
            grid.Children.Add(track);
            grid.Children.Add(count);

            // One hit target across the whole row, so a short bar is as easy to
            // hover and click as a long one.
            var hit = new Border { Background = Brushes.Transparent, ToolTip = slice.Label };
            hit.MouseEnter += (_, _) => bar.Opacity = HoverOpacity;
            hit.MouseLeave += (_, _) => bar.Opacity = 1;
            if (onClick != null)
            {
                hit.Cursor = Cursors.Hand;
                hit.MouseLeftButtonUp += (_, _) => onClick(slice.Label);
            }
            Grid.SetRow(hit, i);
            Grid.SetColumnSpan(hit, 3);
            grid.Children.Add(hit);
        }

        return grid;
    }

    private static void Wire(PieChart chart, ISeries[] series, IReadOnlyList<ChartSlice> slices, Action<string>? onClick)
    {
        if (onClick == null) return;
        var labels = series.Select((s, i) => (s, slices[i].Label)).ToDictionary(x => x.s, x => x.Label);
        chart.Cursor = Cursors.Hand;
        chart.DataPointerDown += (_, points) =>
        {
            var point = points.FirstOrDefault();
            if (point?.Context.Series is ISeries s && labels.TryGetValue(s, out var label)) onClick(label);
        };
    }

    // MARK: - Treemap

    /// <summary>
    /// A squarified treemap: tiles sized by share and kept close to square. A
    /// tile or a legend entry passes its label to <paramref name="onClick"/>;
    /// hovering a tile shows its count and share.
    /// </summary>
    public static UIElement Treemap(IReadOnlyList<ChartSlice> slices, Action<string>? onClick = null,
        IReadOnlyList<Color>? colors = null, double height = TreemapHeight)
    {
        Color ColorAt(int i) => colors?[i % colors.Count] ?? Palette[i % Palette.Length];
        var total = slices.Sum(s => Math.Max(s.Value, 0));
        var canvas = new Canvas { Height = height, ClipToBounds = true };

        void Layout()
        {
            canvas.Children.Clear();
            var w = canvas.ActualWidth;
            if (w <= 0) return;
            foreach (var (slice, rect, index) in TreemapRects(slices, new Rect(0, 0, w, height)))
            {
                var tile = new Border
                {
                    Width = Math.Max(0, rect.Width - 2),
                    Height = Math.Max(0, rect.Height - 2),
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(ColorAt(index)),
                    ToolTip = TreemapTooltip(slice, total),
                };
                if (rect.Width > 48 && rect.Height > 30)
                {
                    tile.Child = new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = slice.Label, Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.Bold,
                                TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center,
                            },
                            new TextBlock
                            {
                                Text = slice.Value.ToString("N0"), Foreground = Brushes.White, FontSize = 11,
                                HorizontalAlignment = HorizontalAlignment.Center,
                            },
                        },
                    };
                }
                tile.MouseEnter += (_, _) => tile.Opacity = HoverOpacity;
                tile.MouseLeave += (_, _) => tile.Opacity = 1;
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

        // Legend: whole entries flow onto the next line; an entry never breaks inside itself.
        var legend = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        for (var i = 0; i < slices.Count; i++)
        {
            var slice = slices[i];
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 2), Background = Brushes.Transparent };
            entry.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 7, Height = 7, Fill = new SolidColorBrush(ColorAt(i)),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
            });
            entry.Children.Add(new TextBlock { Text = slice.Label, FontSize = 11, Foreground = Medium, Margin = new Thickness(0, 0, 4, 0) });
            entry.Children.Add(new TextBlock { Text = slice.Value.ToString("N0"), FontSize = 11, FontWeight = FontWeights.Medium });
            if (onClick != null)
            {
                entry.Cursor = Cursors.Hand;
                entry.MouseLeftButtonUp += (_, _) => onClick(slice.Label);
            }
            legend.Children.Add(entry);
        }

        return new StackPanel { Children = { canvas, legend } };
    }

    private static ToolTip TreemapTooltip(ChartSlice slice, int total) => new()
    {
        Content = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = slice.Label, FontWeight = FontWeights.Bold },
                new TextBlock { Text = $"{slice.Value:N0}  ·  {SharePercent(slice.Value, total)}%" },
            },
        },
    };

    /// <summary>A slice's whole-number share of the total, rounded down as the Mac does.</summary>
    internal static int SharePercent(int value, int total) => total > 0 ? (int)((double)value / total * 100) : 0;

    /// <summary>
    /// Squarified treemap rectangles (Bruls, Huizing and van Wijk), as the
    /// macOS app lays them out: slices fill strips along the shorter side, and
    /// a strip takes another slice only while that keeps its worst aspect
    /// ratio from getting worse. Slices go in largest first; empty ones are
    /// skipped. Index is the slice's position in <paramref name="slices"/>.
    /// </summary>
    internal static List<(ChartSlice Slice, Rect Rect, int Index)> TreemapRects(IReadOnlyList<ChartSlice> slices, Rect bounds)
    {
        var ordered = slices.Select((s, i) => (Slice: s, Index: i))
            .Where(x => x.Slice.Value > 0)
            .OrderByDescending(x => x.Slice.Value)
            .ToList();
        var result = new List<(ChartSlice, Rect, int)>();
        var total = (double)ordered.Sum(x => x.Slice.Value);
        if (ordered.Count == 0 || total <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;

        var areas = ordered.Select(x => x.Slice.Value / total * bounds.Width * bounds.Height).ToList();
        var remaining = bounds;
        var i = 0;
        while (i < ordered.Count)
        {
            var wide = remaining.Width >= remaining.Height;
            var side = wide ? remaining.Height : remaining.Width;
            var row = new List<int>();
            var rowArea = 0.0;
            var bestWorst = double.PositiveInfinity;

            for (var j = i; j < ordered.Count; j++)
            {
                var candidate = rowArea + areas[j];
                var strip = candidate / side;
                var worst = row.Append(j).Max(k =>
                {
                    var length = areas[k] / strip;
                    return Math.Max(length / strip, strip / length);
                });
                if (row.Count == 0 || worst <= bestWorst)
                {
                    row.Add(j);
                    rowArea = candidate;
                    bestWorst = worst;
                }
                else
                {
                    break;
                }
            }

            var thickness = rowArea / side;
            var offset = 0.0;
            foreach (var k in row)
            {
                var length = areas[k] / thickness;
                var rect = wide
                    ? new Rect(remaining.X, remaining.Y + offset, thickness, length)
                    : new Rect(remaining.X + offset, remaining.Y, length, thickness);
                result.Add((ordered[k].Slice, rect, ordered[k].Index));
                offset += length;
            }

            remaining = wide
                ? new Rect(remaining.X + thickness, remaining.Y, Math.Max(0, remaining.Width - thickness), remaining.Height)
                : new Rect(remaining.X, remaining.Y + thickness, remaining.Width, Math.Max(0, remaining.Height - thickness));
            i += row.Count;
        }

        return result;
    }

    // MARK: - Loading and empty states

    /// <summary>A chart-shaped placeholder: a title bar over six bars, pulsing while data loads.</summary>
    public static UIElement Skeleton()
    {
        var bars = new Grid { Height = 100, Margin = new Thickness(0, 12, 0, 0) };
        var heights = new[] { 60, 90, 45, 80, 55, 70 };
        for (var i = 0; i < heights.Length; i++)
        {
            bars.ColumnDefinitions.Add(new ColumnDefinition());
            var bar = SkeletonBlock(double.NaN, heights[i], new Thickness(i == 0 ? 0 : 4, 0, i == heights.Length - 1 ? 0 : 4, 0));
            bar.VerticalAlignment = VerticalAlignment.Bottom;
            bar.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(bar, i);
            bars.Children.Add(bar);
        }

        var panel = new StackPanel { Children = { SkeletonBlock(140, 16, new Thickness(0)), bars } };
        panel.HorizontalAlignment = HorizontalAlignment.Stretch;
        ((FrameworkElement)panel.Children[0]).HorizontalAlignment = HorizontalAlignment.Left;
        Pulse(panel);
        return panel;
    }

    internal static Border SkeletonBlock(double width, double height, Thickness margin) => new()
    {
        Width = width,
        Height = height,
        Margin = margin,
        CornerRadius = new CornerRadius(4),
        Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private static void Pulse(UIElement element) =>
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.45, new Duration(TimeSpan.FromMilliseconds(900)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        });

    /// <summary>A card's message when it has nothing to chart.</summary>
    public static UIElement Empty(string message) => new TextBlock
    {
        Text = message, FontSize = 12, Foreground = Medium,
        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 20),
    };

    // MARK: - Lists

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
