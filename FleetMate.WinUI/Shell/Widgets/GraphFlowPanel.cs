using FleetMate.Core.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace FleetMate.WinUI.Shell.Widgets;

/// <summary>
/// The strip's responsive grid, as FleetMate.GUI's WidgetFlowPanel and the
/// macOS app lay it out: the width is cut into units of at least
/// <see cref="WidgetRowPacking.MinUnitWidth"/>, a card takes its span of them
/// (<see cref="UnitsProperty"/>), and cards keep their order in the fewest,
/// most even rows (<see cref="WidgetRowPacking"/>). Every card in a row gets
/// the row's tallest height, so a row reads as one level band.
/// </summary>
internal sealed partial class GraphFlowPanel : Panel
{
    public static readonly DependencyProperty UnitsProperty = DependencyProperty.RegisterAttached(
        "Units", typeof(int), typeof(GraphFlowPanel), new PropertyMetadata(1));

    public static int GetUnits(UIElement element) => (int)element.GetValue(UnitsProperty);
    public static void SetUnits(UIElement element, int value) => element.SetValue(UnitsProperty, value);

    /// <summary>The bottom of each row of cards, from the last measure; a host snaps its height cap to these.</summary>
    public IReadOnlyList<double> RowBottoms { get; private set; } = Array.Empty<double>();

    /// <summary>Raised after a measure changes <see cref="RowBottoms"/>.</summary>
    public event Action<GraphFlowPanel>? RowsMeasured;

    private List<(int Index, Rect Slot)> Layout(double width, bool measure)
    {
        var bottoms = new List<double>();
        var visible = Children.Select((c, i) => (Child: c, Index: i)).Where(x => x.Child.Visibility == Visibility.Visible).ToList();
        var perRow = WidgetRowPacking.UnitsPerRow(width);
        var rows = WidgetRowPacking.Rows(visible.Select(x => GetUnits(x.Child)).ToList(), perRow, WidgetRowPacking.FullRow);
        var gap = WidgetRowPacking.Gap;
        var slots = new List<(int, Rect)>();
        var y = 0.0;

        foreach (var row in rows)
        {
            int Span(UIElement c) => Math.Min(Math.Max(1, GetUnits(c)), perRow);
            var unitWidth = (width - gap * (row.Count - 1)) / row.Sum(r => Span(visible[r].Child));
            var x = 0.0;
            var rowHeight = 0.0;
            var start = slots.Count;
            foreach (var r in row)
            {
                var (child, index) = visible[r];
                var w = Math.Max(0, unitWidth * Span(child));
                if (measure) child.Measure(new Size(w, double.PositiveInfinity));
                slots.Add((index, new Rect(x, y, w, child.DesiredSize.Height)));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                x += w + gap;
            }
            for (var k = start; k < slots.Count; k++)
            {
                var (i, slot) = slots[k];
                slots[k] = (i, new Rect(slot.X, slot.Y, slot.Width, rowHeight));
            }
            bottoms.Add(y + rowHeight);
            y += rowHeight + gap;
        }
        if (measure && !bottoms.SequenceEqual(RowBottoms))
        {
            RowBottoms = bottoms;
            RowsMeasured?.Invoke(this);
        }
        return slots;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? WidgetRowPacking.MinUnitWidth * 3 : availableSize.Width;
        var slots = Layout(width, measure: true);
        return new Size(width, slots.Count == 0 ? 0 : slots.Max(s => s.Slot.Bottom));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (index, slot) in Layout(finalSize.Width, measure: false)) Children[index].Arrange(slot);
        return finalSize;
    }
}

/// <summary>Left-to-right items that wrap whole onto the next line: a chart's legend.</summary>
internal sealed partial class GraphWrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 12;
    public double VerticalSpacing { get; set; } = 4;

    private IEnumerable<(UIElement Child, Point At)> Place(double width)
    {
        double x = 0, y = 0, line = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > width)
            {
                x = 0;
                y += line + VerticalSpacing;
                line = 0;
            }
            yield return (child, new Point(x, y));
            x += size.Width + HorizontalSpacing;
            line = Math.Max(line, size.Height);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        var width = double.IsInfinity(availableSize.Width) ? Children.Sum(c => c.DesiredSize.Width + HorizontalSpacing) : availableSize.Width;
        var bottom = Place(width).Select(p => p.At.Y + p.Child.DesiredSize.Height).DefaultIfEmpty(0).Max();
        return new Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width, bottom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (child, at) in Place(finalSize.Width))
            child.Arrange(new Rect(at, new Size(Math.Min(child.DesiredSize.Width, finalSize.Width), child.DesiredSize.Height)));
        return finalSize;
    }
}
