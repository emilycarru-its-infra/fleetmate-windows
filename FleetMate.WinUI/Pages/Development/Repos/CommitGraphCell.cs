using FleetMate.Core.Services.Repos;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace FleetMate.WinUI.Pages.Development.Repos;

/// <summary>
/// One row of the History lane graph: the lines passing through, the lines
/// into and out of this commit's dot, and the dot. Geometry comes from
/// <see cref="CommitGraphBuilder"/>; lanes keep a colour for life. Drawn with
/// shapes on a canvas, redrawn when the row's height or data changes.
/// </summary>
public sealed partial class CommitGraphCell : Canvas
{
    public const double LaneWidth = 12;

    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(GraphRow), typeof(CommitGraphCell), new PropertyMetadata(null, (d, _) => ((CommitGraphCell)d).Redraw()));

    public static readonly DependencyProperty LaneCountProperty = DependencyProperty.Register(
        nameof(LaneCount), typeof(int), typeof(CommitGraphCell), new PropertyMetadata(1, (d, _) => ((CommitGraphCell)d).Redraw()));

    public CommitGraphCell()
    {
        IsHitTestVisible = false;
        Width = LaneWidth + 4;
        SizeChanged += (_, e) => { if (e.PreviousSize.Height != e.NewSize.Height) Redraw(); };
        ActualThemeChanged += (_, _) => Redraw();
    }

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

    private void Redraw()
    {
        Width = Math.Min(Math.Max(1, LaneCount), 12) * LaneWidth + 4;
        Children.Clear();
        if (Row is not { } row || ActualHeight <= 0) return;
        var height = ActualHeight;
        var middle = height / 2;
        static double X(int column) => column * LaneWidth + LaneWidth / 2 + 2;

        foreach (var segment in row.Segments)
        {
            var from = new Point(X(segment.FromColumn), segment.UpperHalf ? 0 : middle);
            var to = new Point(X(segment.ToColumn), segment.UpperHalf ? middle : height);
            var figure = new PathFigure { StartPoint = from, IsClosed = false, IsFilled = false };
            // A curve between lanes reads as a branch or merge, not a crossing.
            if (segment.FromColumn == segment.ToColumn) figure.Segments.Add(new LineSegment { Point = to });
            else figure.Segments.Add(new BezierSegment
            {
                Point1 = new Point(from.X, (from.Y + to.Y) / 2),
                Point2 = new Point(to.X, (from.Y + to.Y) / 2),
                Point3 = to,
            });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, Stroke = RepoBrushes.Lane(segment.ColorIndex), StrokeThickness = 1.6 });
        }
        var dot = new Ellipse
        {
            Width = 7.2,
            Height = 7.2,
            Fill = RepoBrushes.Lane(row.DotColorIndex),
            Stroke = RepoBrushes.Text,
            StrokeThickness = 0.8,
        };
        SetLeft(dot, X(row.DotColumn) - 3.6);
        SetTop(dot, middle - 3.6);
        Children.Add(dot);
    }
}
