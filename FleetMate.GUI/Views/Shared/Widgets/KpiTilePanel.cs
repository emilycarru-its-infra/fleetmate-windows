using System.Windows;
using System.Windows.Controls;

namespace FleetMate.GUI.Views.Shared.Widgets;

/// <summary>
/// The Tickets figures inside one chart-width card: three columns where each
/// tile still gets <see cref="MinTileWidth"/>, two compact columns otherwise,
/// with as many rows as the tiles need. Cells share the width equally and,
/// when the card is stretched to its row's height, the height too.
/// </summary>
public sealed class KpiTilePanel : Panel
{
    /// <summary>The narrowest a tile in the three-column layout may be.</summary>
    public const double MinTileWidth = 76;

    internal static int ColumnsFor(double width) => width >= 3 * MinTileWidth ? 3 : 2;

    private int Columns(double width) => double.IsInfinity(width) ? 3 : ColumnsFor(width);

    private int RowCount(int columns) => (VisibleChildren().Count() + columns - 1) / columns;

    private IEnumerable<UIElement> VisibleChildren() =>
        InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed);

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Columns(availableSize.Width);
        var cellWidth = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width / columns;
        double tallest = 0, widest = 0;
        foreach (var child in VisibleChildren())
        {
            child.Measure(new Size(cellWidth, double.PositiveInfinity));
            tallest = Math.Max(tallest, child.DesiredSize.Height);
            widest = Math.Max(widest, child.DesiredSize.Width);
        }
        var width = double.IsInfinity(availableSize.Width) ? widest * columns : availableSize.Width;
        return new Size(width, tallest * RowCount(columns));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var rows = Math.Max(1, RowCount(columns));
        var cellWidth = finalSize.Width / columns;
        var cellHeight = finalSize.Height / rows;
        var i = 0;
        foreach (var child in VisibleChildren())
        {
            child.Arrange(new Rect(i % columns * cellWidth, i / columns * cellHeight, cellWidth, cellHeight));
            i++;
        }
        return finalSize;
    }
}
