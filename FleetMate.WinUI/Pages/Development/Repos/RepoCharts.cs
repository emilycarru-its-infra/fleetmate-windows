using System.Globalization;
using FleetMate.Core.Services.Repos;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using FleetMate.WinUI.Shell.Widgets;

namespace FleetMate.WinUI.Pages.Development.Repos;

/// <summary>
/// The shared drawing for the Insights charts: a canvas redrawn from its data
/// when its size or theme changes, axis text in the caption size, and a hover
/// box that names what is under the pointer.
/// </summary>
public abstract partial class InsightsChart : Canvas
{
    protected const double ChartText = 12;
    private readonly Border _hover;
    private readonly TextBlock _hoverText;

    protected InsightsChart()
    {
        MinHeight = 130;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _hoverText = new TextBlock { FontSize = ChartText, Foreground = RepoBrushes.Text };
        _hover = new Border
        {
            Child = _hoverText,
            Padding = new Thickness(6, 2, 6, 3),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
        PointerMoved += (_, e) => Hover(e.GetCurrentPoint(this).Position);
        PointerExited += (_, _) => Hover(null);
    }

    protected double W => ActualWidth;
    protected double H => ActualHeight;

    protected void Redraw()
    {
        Children.Clear();
        if (W <= 0 || H <= 0) return;
        Draw();
        _hover.Background = GraphTheme.Resource("SolidBackgroundFillColorTertiaryBrush");
        _hover.BorderBrush = GraphTheme.Resource("CardStrokeColorDefaultBrush");
        _hoverText.Foreground = RepoBrushes.Text;
        _hover.Visibility = Visibility.Collapsed;
        Children.Add(_hover);
    }

    protected abstract void Draw();

    /// <summary>What to say for the point under the pointer, and where to put the box; null hides it.</summary>
    protected abstract (string Text, double X, double Y)? HoverAt(Windows.Foundation.Point point);

    private void Hover(Windows.Foundation.Point? point)
    {
        var at = point is { } p ? HoverAt(p) : null;
        OnHoverChanged(point);
        if (at is not { } hover)
        {
            _hover.Visibility = Visibility.Collapsed;
            return;
        }
        _hoverText.Text = hover.Text;
        _hover.Visibility = Visibility.Visible;
        _hover.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        SetLeft(_hover, Math.Max(0, Math.Min(hover.X - _hover.DesiredSize.Width / 2, W - _hover.DesiredSize.Width)));
        SetTop(_hover, Math.Max(0, hover.Y));
    }

    protected virtual void OnHoverChanged(Windows.Foundation.Point? point) { }

    protected TextBlock Label(string text, double x, double y, Brush? brush = null)
    {
        var block = new TextBlock { Text = text, FontSize = ChartText, Foreground = brush ?? RepoBrushes.Secondary };
        SetLeft(block, x);
        SetTop(block, y);
        Children.Add(block);
        return block;
    }

    protected static double TextWidth(TextBlock block)
    {
        block.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return block.DesiredSize.Width;
    }

    protected Rectangle Bar(double x, double y, double width, double height, Brush fill, double radius = 0)
    {
        var bar = new Rectangle { Width = Math.Max(0, width), Height = Math.Max(0, height), Fill = fill, RadiusX = radius, RadiusY = radius };
        SetLeft(bar, x);
        SetTop(bar, y);
        Children.Add(bar);
        return bar;
    }

    protected void Rule(double x1, double y1, double x2, double y2, double opacity = 1, double thickness = 0.6) =>
        Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = RepoBrushes.Secondary, StrokeThickness = thickness, Opacity = opacity });
}

/// <summary>
/// A bar chart over a statistics timeline: commits per bucket, lines added
/// above the axis and removed below it, or commits stacked by repository.
/// Hovering a bar shows its bucket and values above the plot.
/// </summary>
public sealed partial class TimelineChart : InsightsChart
{
    public enum ChartMode { Commits, Churn, Stacked }

    private ChartMode _mode;
    private IReadOnlyList<DateTimeOffset> _starts = Array.Empty<DateTimeOffset>();
    private IReadOnlyList<(string Name, int Slot, IReadOnlyList<int> Values)> _series = Array.Empty<(string, int, IReadOnlyList<int>)>();
    private IReadOnlyList<int> _added = Array.Empty<int>();
    private IReadOnlyList<int> _removed = Array.Empty<int>();
    private RepoStatsBucket _bucket;
    private RepoChurnScale _churnScale;
    private readonly List<List<Rectangle>> _bars = new();
    private int _hovered = -1;

    /// <summary>The slot of the "Other" series: drawn in the tertiary text colour, not a series colour.</summary>
    public const int OtherSlot = -1;

    private const double Top = 22, Bottom = 20, Left = 52;

    public void ShowCommits(IReadOnlyList<RepoStatsReport.TimelinePoint> points, RepoStatsBucket bucket)
    {
        _mode = ChartMode.Commits;
        _bucket = bucket;
        _starts = points.Select(p => p.Start).ToList();
        _series = new[] { ("Commits", int.MinValue, (IReadOnlyList<int>)points.Select(p => p.Commits).ToList()) };
        AutomationProperties.SetName(this, $"Commits per {Bucket(bucket)}: {points.Sum(p => p.Commits):N0} in {points.Count} {Bucket(bucket)}s");
        Redraw();
    }

    public void ShowChurn(IReadOnlyList<RepoStatsReport.TimelinePoint> points, RepoStatsBucket bucket, RepoChurnScale scale = RepoChurnScale.Automatic)
    {
        _mode = ChartMode.Churn;
        _churnScale = RepoChurnScaling.Resolve(scale, points);
        _bucket = bucket;
        _starts = points.Select(p => p.Start).ToList();
        _added = points.Select(p => p.Added).ToList();
        _removed = points.Select(p => p.Removed).ToList();
        AutomationProperties.SetName(this, $"Lines changed per {Bucket(bucket)}: {_added.Sum():N0} added, {_removed.Sum():N0} removed");
        Redraw();
    }

    /// <summary>Series by name and colour slot (<see cref="RepoBrushes.Series"/>, or <see cref="OtherSlot"/>).</summary>
    public void ShowStacked(IReadOnlyList<DateTimeOffset> starts, IReadOnlyList<(string Name, int Slot, IReadOnlyList<int> Values)> series, RepoStatsBucket bucket)
    {
        _mode = ChartMode.Stacked;
        _bucket = bucket;
        _starts = starts;
        _series = series;
        AutomationProperties.SetName(this, $"Commits per {Bucket(bucket)}, stacked by repository: " +
            string.Join(", ", series.Select(s => $"{s.Name} {s.Values.Sum():N0}")));
        Redraw();
    }

    public static string Bucket(RepoStatsBucket bucket) => bucket.ToString().ToLowerInvariant();

    public static string DateLabel(DateTimeOffset start, RepoStatsBucket bucket) => bucket switch
    {
        RepoStatsBucket.Day => start.ToString("ddd d MMM", CultureInfo.CurrentCulture),
        RepoStatsBucket.Week => "Week of " + start.ToString("d MMM yyyy", CultureInfo.CurrentCulture),
        _ => start.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
    };

    internal static string Compact(int value) =>
        value >= 1_000_000 ? $"{value / 1_000_000.0:0.#}M" : value >= 1_000 ? $"{value / 1_000.0:0.#}K" : value.ToString();

    public static Brush SeriesBrush(int slot) => slot switch
    {
        int.MinValue => RepoBrushes.Accent,
        OtherSlot => RepoBrushes.Other,
        _ => RepoBrushes.Series(slot),
    };

    private int Total(int i) => _series.Sum(s => i < s.Values.Count ? s.Values[i] : 0);

    protected override void Draw()
    {
        _bars.Clear();
        if (_starts.Count == 0) return;
        var plotWidth = Math.Max(1, W - Left);
        var plotHeight = Math.Max(1, H - Top - Bottom);
        var slot = plotWidth / _starts.Count;
        var barWidth = Math.Max(1, slot * 0.72);

        if (_mode == ChartMode.Churn)
        {
            var peak = Math.Max(1, Math.Max(_added.DefaultIfEmpty(0).Max(), _removed.DefaultIfEmpty(0).Max()));
            var zero = Top + plotHeight / 2;
            var transformedPeak = RepoChurnScaling.Transform(peak, _churnScale);
            for (var i = 0; i < _starts.Count; i++)
            {
                var x = Left + i * slot + (slot - barWidth) / 2;
                var up = plotHeight / 2 * RepoChurnScaling.Transform(_added[i], _churnScale) / transformedPeak;
                var down = plotHeight / 2 * RepoChurnScaling.Transform(_removed[i], _churnScale) / transformedPeak;
                var bars = new List<Rectangle>();
                if (up > 0) bars.Add(Bar(x, zero - up, barWidth, up, RepoBrushes.Added));
                if (down > 0) bars.Add(Bar(x, zero, barWidth, down, RepoBrushes.Removed));
                _bars.Add(bars);
            }
            Rule(Left, zero, W, zero);
            Label("0", 2, zero - 8);
            var previousOffset = 0.0;
            foreach (var value in RepoChurnScaling.Ticks(peak, _churnScale).Where(v => v > 0))
            {
                var offset = plotHeight / 2 * RepoChurnScaling.Transform(value, _churnScale) / transformedPeak;
                if (offset - previousOffset < 14) continue;
                previousOffset = offset;
                Label(Compact(value), 2, zero - offset - 8);
                Label("−" + Compact(value), 2, zero + offset - 8);
                Rule(Left, zero - offset, W, zero - offset, 0.25, 0.4);
                Rule(Left, zero + offset, W, zero + offset, 0.25, 0.4);
            }
        }
        else
        {
            var peak = Math.Max(1, Enumerable.Range(0, _starts.Count).Select(Total).DefaultIfEmpty(0).Max());
            var baseline = Top + plotHeight;
            for (var i = 0; i < _starts.Count; i++)
            {
                var x = Left + i * slot + (slot - barWidth) / 2;
                var y = baseline;
                var bars = new List<Rectangle>();
                foreach (var series in _series)
                {
                    var value = i < series.Values.Count ? series.Values[i] : 0;
                    if (value <= 0) continue;
                    var height = plotHeight * value / peak;
                    bars.Add(Bar(x, y - height, barWidth, height, SeriesBrush(series.Slot)));
                    y -= height;
                }
                _bars.Add(bars);
            }
            Rule(Left, baseline, W, baseline);
            Label(peak.ToString("N0"), 2, Top - 8);
            Label("0", 2, baseline - 10);
        }

        // Dates under the first, middle and last bars.
        foreach (var i in new[] { 0, _starts.Count / 2, _starts.Count - 1 }.Distinct())
        {
            var label = Label(DateLabel(_starts[i], _bucket).Replace("Week of ", ""), 0, H - Bottom + 2);
            SetLeft(label, Math.Max(Left, Math.Min(Left + i * slot, W - TextWidth(label))));
        }
    }

    protected override (string Text, double X, double Y)? HoverAt(Windows.Foundation.Point point)
    {
        if (_starts.Count == 0) return null;
        var slot = Math.Max(1, W - Left) / _starts.Count;
        var index = (int)Math.Floor((point.X - Left) / slot);
        if (index < 0 || index >= _starts.Count) return null;
        var lines = _mode switch
        {
            ChartMode.Churn => $"+{_added[index]:N0} added   −{_removed[index]:N0} removed",
            ChartMode.Stacked => $"{Total(index):N0} commits" + string.Concat(_series
                .Where(s => index < s.Values.Count && s.Values[index] > 0)
                .Select(s => $"   {s.Name} {s.Values[index]}")),
            _ => $"{Total(index):N0} commit{(Total(index) == 1 ? "" : "s")}",
        };
        return ($"{DateLabel(_starts[index], _bucket)}:  {lines}", Left + index * slot + slot / 2, 0);
    }

    protected override void OnHoverChanged(Windows.Foundation.Point? point)
    {
        var index = -1;
        if (point is { } p && _starts.Count > 0)
        {
            var slot = Math.Max(1, W - Left) / _starts.Count;
            index = (int)Math.Floor((p.X - Left) / slot);
            if (index < 0 || index >= _starts.Count) index = -1;
        }
        if (index == _hovered) return;
        _hovered = index;
        for (var i = 0; i < _bars.Count; i++)
            foreach (var bar in _bars[i]) bar.Opacity = index < 0 || index == i ? 1.0 : 0.45;
    }
}

/// <summary>Commits by weekday and hour: one hue, deeper for more. Hover shows the count.</summary>
public sealed partial class ActivityHeatmap : InsightsChart
{
    private IReadOnlyList<RepoStatsReport.ActivityCell> _cells = Array.Empty<RepoStatsReport.ActivityCell>();
    private const double Left = 42, Top = 4, Bottom = 20;

    public ActivityHeatmap() => AutomationProperties.SetName(this, "Commits by weekday and hour");

    public void Show(IReadOnlyList<RepoStatsReport.ActivityCell> cells)
    {
        _cells = RepoActivityGrid.Expand(cells);
        var busiest = _cells.OrderByDescending(c => c.Commits).FirstOrDefault();
        if (busiest is { Commits: > 0 })
            AutomationProperties.SetName(this, $"Commits by weekday and hour; busiest {Names[busiest.Weekday - 1]} {busiest.Hour:00}:00 with {busiest.Commits}");
        Redraw();
    }

    private static string[] Names => CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;

    private (double W, double H) Cell => ((W - Left) / 24, (H - Top - Bottom) / 7);

    protected override void Draw()
    {
        var (w, h) = Cell;
        if (w <= 0 || h <= 0) return;
        var peak = Math.Max(1, _cells.Select(c => c.Commits).DefaultIfEmpty(0).Max());
        var counts = _cells.ToDictionary(c => (c.Weekday, c.Hour), c => c.Commits);
        for (var weekday = 1; weekday <= 7; weekday++)
        {
            var y = Top + (weekday - 1) * h;
            Label(Names[weekday - 1], 2, y + h / 2 - 8);
            for (var hour = 0; hour < 24; hour++)
            {
                var count = counts.GetValueOrDefault((weekday, hour));
                var cell = Bar(Left + hour * w + 1, y + 1, Math.Max(1, w - 2), Math.Max(1, h - 2), RepoBrushes.Accent, 2);
                cell.Opacity = count == 0 ? 0.08 : 0.2 + 0.8 * count / peak;
            }
        }
        foreach (var hour in new[] { 0, 3, 6, 9, 12, 15, 18, 21 })
            Label($"{hour:00}:00", Left + hour * w, H - Bottom + 2);
    }

    protected override (string Text, double X, double Y)? HoverAt(Windows.Foundation.Point point)
    {
        var (w, h) = Cell;
        if (w <= 0 || h <= 0) return null;
        var hour = (int)Math.Floor((point.X - Left) / w);
        var row = (int)Math.Floor((point.Y - Top) / h);
        if (hour is < 0 or >= 24 || row is < 0 or >= 7) return null;
        var count = _cells.FirstOrDefault(c => c.Weekday == row + 1 && c.Hour == hour)?.Commits ?? 0;
        return ($"{Names[row]} {hour:00}:00  {count} commit{(count == 1 ? "" : "s")}", Left + hour * w + w / 2, Math.Max(0, Top + row * h - 24));
    }
}
