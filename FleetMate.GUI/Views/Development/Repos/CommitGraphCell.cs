using System.Windows;
using System.Windows.Media;
using FleetMate.Core.Services.Repos;

namespace FleetMate.GUI.Views.Development.Repos;

/// <summary>
/// One row of the History lane graph: the lines passing through, the lines
/// into and out of this commit's dot, and the dot. Geometry comes from
/// <see cref="CommitGraphBuilder"/>; lanes keep a colour for life.
/// </summary>
public sealed class CommitGraphCell : FrameworkElement
{
    public const double LaneWidth = 12;

    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(GraphRow), typeof(CommitGraphCell),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LaneCountProperty = DependencyProperty.Register(
        nameof(LaneCount), typeof(int), typeof(CommitGraphCell),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public GraphRow? Row
    {
        get => (GraphRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public int LaneCount
    {
        get => (int)GetValue(LaneCountProperty);
        set => SetValue(LaneCountProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Min(Math.Max(1, LaneCount), 12) * LaneWidth + 4, 0);

    protected override void OnRender(DrawingContext dc)
    {
        if (Row is not { } row) return;
        var height = ActualHeight;
        var middle = height / 2;
        double X(int column) => column * LaneWidth + LaneWidth / 2 + 2;

        foreach (var segment in row.Segments)
        {
            var pen = new Pen(RepoBrushes.Lane(segment.ColorIndex), 1.6);
            var from = new Point(X(segment.FromColumn), segment.UpperHalf ? 0 : middle);
            var to = new Point(X(segment.ToColumn), segment.UpperHalf ? middle : height);
            if (segment.FromColumn == segment.ToColumn)
            {
                dc.DrawLine(pen, from, to);
                continue;
            }
            // A curve between lanes reads as a branch or merge, not a crossing.
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(from, false, false);
                context.BezierTo(new Point(from.X, (from.Y + to.Y) / 2), new Point(to.X, (from.Y + to.Y) / 2), to, true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }
        dc.DrawEllipse(RepoBrushes.Lane(row.DotColorIndex), new Pen(RepoBrushes.Text, 0.8), new Point(X(row.DotColumn), middle), 3.6, 3.6);
    }
}
