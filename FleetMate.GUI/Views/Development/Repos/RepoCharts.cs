using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FleetMate.Core.Services.Repos;

namespace FleetMate.GUI.Views.Development.Repos;

/// <summary>
/// A bar chart over a statistics timeline: commits per bucket, lines added
/// above the axis and removed below it, or commits stacked by repository.
/// Hovering a bar shows its bucket and values above the plot.
/// </summary>
public sealed class TimelineChart : FrameworkElement
{
    public enum ChartMode { Commits, Churn, Stacked }

    private ChartMode _mode;
    private IReadOnlyList<DateTimeOffset> _starts = Array.Empty<DateTimeOffset>();
    private IReadOnlyList<(string Name, Brush Brush, IReadOnlyList<int> Values)> _series = Array.Empty<(string, Brush, IReadOnlyList<int>)>();
    private IReadOnlyList<int> _added = Array.Empty<int>();
    private IReadOnlyList<int> _removed = Array.Empty<int>();
    private RepoStatsBucket _bucket;
    private int _hovered = -1;

    private const double Top = 22, Bottom = 18, Left = 34;

    public TimelineChart()
    {
        Height = 200;
        MouseMove += (_, e) => Hover(e.GetPosition(this));
        MouseLeave += (_, _) => { _hovered = -1; InvalidateVisual(); };
        Focusable = false;
    }

    public void ShowCommits(IReadOnlyList<RepoStatsReport.TimelinePoint> points, RepoStatsBucket bucket)
    {
        _mode = ChartMode.Commits;
        _bucket = bucket;
        _starts = points.Select(p => p.Start).ToList();
        _series = new[] { ("Commits", RepoBrushes.Accent, (IReadOnlyList<int>)points.Select(p => p.Commits).ToList()) };
        System.Windows.Automation.AutomationProperties.SetName(this, $"Commits per {Bucket(bucket)}");
        InvalidateVisual();
    }

    public void ShowChurn(IReadOnlyList<RepoStatsReport.TimelinePoint> points, RepoStatsBucket bucket)
    {
        _mode = ChartMode.Churn;
        _bucket = bucket;
        _starts = points.Select(p => p.Start).ToList();
        _added = points.Select(p => p.Added).ToList();
        _removed = points.Select(p => p.Removed).ToList();
        System.Windows.Automation.AutomationProperties.SetName(this, $"Lines added and removed per {Bucket(bucket)}");
        InvalidateVisual();
    }

    public void ShowStacked(IReadOnlyList<DateTimeOffset> starts, IReadOnlyList<(string Name, Brush Brush, IReadOnlyList<int> Values)> series, RepoStatsBucket bucket)
    {
        _mode = ChartMode.Stacked;
        _bucket = bucket;
        _starts = starts;
        _series = series;
        System.Windows.Automation.AutomationProperties.SetName(this, $"Commits per {Bucket(bucket)}, stacked by repository");
        InvalidateVisual();
    }

    public static string Bucket(RepoStatsBucket bucket) => bucket.ToString().ToLowerInvariant();

    public static string Label(DateTimeOffset start, RepoStatsBucket bucket) => bucket switch
    {
        RepoStatsBucket.Day => start.ToString("ddd d MMM", CultureInfo.CurrentCulture),
        RepoStatsBucket.Week => "Week of " + start.ToString("d MMM yyyy", CultureInfo.CurrentCulture),
        _ => start.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
    };

    private int Total(int i) => _series.Sum(s => i < s.Values.Count ? s.Values[i] : 0);

    private void Hover(Point point)
    {
        if (_starts.Count == 0) return;
        var width = Math.Max(1, ActualWidth - Left);
        var index = (int)Math.Floor((point.X - Left) / (width / _starts.Count));
        index = index >= 0 && index < _starts.Count ? index : -1;
        if (index == _hovered) return;
        _hovered = index;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_starts.Count == 0) return;
        var plotWidth = Math.Max(1, ActualWidth - Left);
        var plotHeight = Math.Max(1, ActualHeight - Top - Bottom);
        var slot = plotWidth / _starts.Count;
        var barWidth = Math.Max(1, slot * 0.72);
        var axisPen = new Pen(RepoBrushes.Secondary, 0.6);

        if (_mode == ChartMode.Churn)
        {
            var peak = Math.Max(1, Math.Max(_added.DefaultIfEmpty(0).Max(), _removed.DefaultIfEmpty(0).Max()));
            var zero = Top + plotHeight / 2;
            for (var i = 0; i < _starts.Count; i++)
            {
                var x = Left + i * slot + (slot - barWidth) / 2;
                var fade = _hovered < 0 || _hovered == i ? 1.0 : 0.45;
                var up = plotHeight / 2 * _added[i] / peak;
                var down = plotHeight / 2 * _removed[i] / peak;
                dc.PushOpacity(fade);
                if (up > 0) dc.DrawRectangle(RepoBrushes.Added, null, new Rect(x, zero - up, barWidth, up));
                if (down > 0) dc.DrawRectangle(RepoBrushes.Removed, null, new Rect(x, zero, barWidth, down));
                dc.Pop();
            }
            dc.DrawLine(axisPen, new Point(Left, zero), new Point(ActualWidth, zero));
            Text(dc, Compact(peak), new Point(2, Top - 6), RepoBrushes.Secondary, 10);
            Text(dc, Compact(peak), new Point(2, Top + plotHeight - 8), RepoBrushes.Secondary, 10);
        }
        else
        {
            var peak = Math.Max(1, Enumerable.Range(0, _starts.Count).Select(Total).DefaultIfEmpty(0).Max());
            var baseline = Top + plotHeight;
            for (var i = 0; i < _starts.Count; i++)
            {
                var x = Left + i * slot + (slot - barWidth) / 2;
                var y = baseline;
                dc.PushOpacity(_hovered < 0 || _hovered == i ? 1.0 : 0.45);
                foreach (var series in _series)
                {
                    var value = i < series.Values.Count ? series.Values[i] : 0;
                    if (value <= 0) continue;
                    var height = plotHeight * value / peak;
                    dc.DrawRectangle(series.Brush, null, new Rect(x, y - height, barWidth, height));
                    y -= height;
                }
                dc.Pop();
            }
            dc.DrawLine(axisPen, new Point(Left, baseline), new Point(ActualWidth, baseline));
            Text(dc, peak.ToString("N0"), new Point(2, Top - 6), RepoBrushes.Secondary, 10);
            Text(dc, "0", new Point(2, baseline - 8), RepoBrushes.Secondary, 10);
        }

        // Dates under the first, middle and last bars.
        foreach (var i in new[] { 0, _starts.Count / 2, _starts.Count - 1 }.Distinct())
        {
            var label = Label(_starts[i], _bucket).Replace("Week of ", "");
            var x = Left + i * slot;
            var formatted = Formatted(label, RepoBrushes.Secondary, 10);
            x = Math.Min(Math.Max(Left, x), ActualWidth - formatted.Width);
            dc.DrawText(formatted, new Point(x, ActualHeight - Bottom + 3));
        }

        if (_hovered >= 0)
        {
            var lines = _mode switch
            {
                ChartMode.Churn => $"+{_added[_hovered]:N0} added   −{_removed[_hovered]:N0} removed",
                ChartMode.Stacked => $"{Total(_hovered):N0} commits" + string.Concat(_series
                    .Where(s => _hovered < s.Values.Count && s.Values[_hovered] > 0)
                    .Select(s => $"   {s.Name} {s.Values[_hovered]}")),
                _ => $"{Total(_hovered):N0} commit{(Total(_hovered) == 1 ? "" : "s")}",
            };
            var formatted = Formatted($"{Label(_starts[_hovered], _bucket)}:  {lines}", RepoBrushes.Text, 11);
            var x = Math.Min(Math.Max(Left, Left + _hovered * slot - formatted.Width / 2), Math.Max(Left, ActualWidth - formatted.Width - 8));
            var box = new Rect(x - 4, 0, formatted.Width + 8, formatted.Height + 4);
            dc.DrawRoundedRectangle(RepoBrushes.Subtle, new Pen(RepoBrushes.Secondary, 0.5), box, 4, 4);
            dc.DrawText(formatted, new Point(x, 2));
        }
    }

    internal static string Compact(int value) =>
        value >= 1_000_000 ? $"{value / 1_000_000.0:0.#}M" : value >= 1_000 ? $"{value / 1_000.0:0.#}K" : value.ToString();

    private FormattedText Formatted(string text, Brush brush, double size) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void Text(DrawingContext dc, string text, Point at, Brush brush, double size) =>
        dc.DrawText(Formatted(text, brush, size), at);
}

/// <summary>Commits by weekday and hour: one hue, darker for more. Hover shows the count.</summary>
public sealed class ActivityHeatmap : FrameworkElement
{
    private IReadOnlyList<RepoStatsReport.ActivityCell> _cells = Array.Empty<RepoStatsReport.ActivityCell>();
    private (int Weekday, int Hour)? _hovered;
    private const double Left = 40, Top = 4, Bottom = 18;

    public ActivityHeatmap()
    {
        Height = 190;
        System.Windows.Automation.AutomationProperties.SetName(this, "Commits by weekday and hour");
        MouseMove += (_, e) => Hover(e.GetPosition(this));
        MouseLeave += (_, _) => { _hovered = null; InvalidateVisual(); };
    }

    public void Show(IReadOnlyList<RepoStatsReport.ActivityCell> cells)
    {
        _cells = cells;
        InvalidateVisual();
    }

    private (double W, double H) Cell => ((ActualWidth - Left) / 24, (ActualHeight - Top - Bottom) / 7);

    private void Hover(Point p)
    {
        var (w, h) = Cell;
        var hour = (int)Math.Floor((p.X - Left) / w);
        var row = (int)Math.Floor((p.Y - Top) / h);
        (int, int)? next = hour is >= 0 and < 24 && row is >= 0 and < 7 ? (row + 1, hour) : null;
        if (next == _hovered) return;
        _hovered = next;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var (w, h) = Cell;
        if (w <= 0 || h <= 0) return;
        var peak = Math.Max(1, _cells.Select(c => c.Commits).DefaultIfEmpty(0).Max());
        var counts = _cells.ToDictionary(c => (c.Weekday, c.Hour), c => c.Commits);
        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var weekday = 1; weekday <= 7; weekday++)
        {
            var y = Top + (weekday - 1) * h;
            dc.DrawText(new FormattedText(names[weekday - 1], CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10, RepoBrushes.Secondary, dpi), new Point(2, y + h / 2 - 7));
            for (var hour = 0; hour < 24; hour++)
            {
                var count = counts.GetValueOrDefault((weekday, hour));
                var rect = new Rect(Left + hour * w + 1, y + 1, Math.Max(1, w - 2), Math.Max(1, h - 2));
                dc.PushOpacity(count == 0 ? 0.08 : 0.2 + 0.8 * count / peak);
                dc.DrawRoundedRectangle(RepoBrushes.Accent, null, rect, 2, 2);
                dc.Pop();
            }
        }
        foreach (var hour in new[] { 0, 3, 6, 9, 12, 15, 18, 21 })
            dc.DrawText(new FormattedText($"{hour:00}:00", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10, RepoBrushes.Secondary, dpi), new Point(Left + hour * w, ActualHeight - Bottom + 3));

        if (_hovered is { } cell)
        {
            var count = counts.GetValueOrDefault(cell);
            var text = new FormattedText($"{names[cell.Weekday - 1]} {cell.Hour:00}:00  {count} commit{(count == 1 ? "" : "s")}",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, RepoBrushes.Text, dpi);
            var x = Math.Min(Left + cell.Hour * w, ActualWidth - text.Width - 8);
            var y = Math.Max(0, Top + (cell.Weekday - 1) * h - text.Height - 4);
            dc.DrawRoundedRectangle(RepoBrushes.Subtle, new Pen(RepoBrushes.Secondary, 0.5), new Rect(x - 4, y, text.Width + 8, text.Height + 4), 4, 4);
            dc.DrawText(text, new Point(x, y + 2));
        }
    }
}
