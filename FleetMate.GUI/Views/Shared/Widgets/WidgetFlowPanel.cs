using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Shared;

namespace FleetMate.GUI.Views.Shared.Widgets;

/// <summary>
/// A responsive grid for a tab's cards, matching the macOS app. The width is
/// cut into units of at least <see cref="MinUnitWidth"/>; a card takes its
/// span of them (<see cref="UnitsProperty"/>). Cards keep their order and are
/// split into the fewest rows that hold them, then balanced so every row
/// carries about the same number of units (<see cref="WidgetRowPacking"/>) —
/// never a full row followed by one stretched leftover. A card spanning
/// <see cref="FullRow"/> takes a row of its own. Every row spans the full
/// width, and every card in a row is given the row's tallest height, so a
/// row reads as one level band; cards must stretch vertically to use it.
/// With <see cref="SingleRow"/> every card shares one row at any width.
/// </summary>
public sealed class WidgetFlowPanel : Panel
{
    public const double MinUnitWidth = 240;
    public const double Gap = 12;

    /// <summary>A card's units when it takes a whole row.</summary>
    public const int FullRow = 1000;

    private bool _equalHeights = true;
    private bool _singleRow;

    /// <summary>Keep every card on one row, narrowing them rather than wrapping.</summary>
    public bool SingleRow
    {
        get => _singleRow;
        set { _singleRow = value; InvalidateMeasure(); }
    }

    /// <summary>Offer every card its row's tallest height. On by default.</summary>
    public bool EqualHeights
    {
        get => _equalHeights;
        set { _equalHeights = value; InvalidateMeasure(); }
    }

    public static readonly DependencyProperty UnitsProperty = DependencyProperty.RegisterAttached(
        "Units", typeof(int), typeof(WidgetFlowPanel),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static int GetUnits(UIElement element) => (int)element.GetValue(UnitsProperty);
    public static void SetUnits(UIElement element, int value) => element.SetValue(UnitsProperty, value);

    internal static int UnitsPerRow(double width) => Math.Max(1, (int)Math.Floor((width + Gap) / (MinUnitWidth + Gap)));

    /// <summary>
    /// Rows of child indices, balanced (see <see cref="WidgetRowPacking"/>).
    /// With <paramref name="singleRow"/> every card is on the one row.
    /// </summary>
    internal static List<List<int>> Rows(IReadOnlyList<int> units, double width, bool singleRow = false)
    {
        if (singleRow)
            return units.Count == 0 ? new List<List<int>>() : new List<List<int>> { Enumerable.Range(0, units.Count).ToList() };
        return WidgetRowPacking.Rows(units, UnitsPerRow(width), FullRow);
    }

    private List<(int Index, Rect Slot)> Arrange(double width)
    {
        var children = InternalChildren.Cast<UIElement>().ToList();
        var visible = children.Select((c, i) => (c, i)).Where(x => x.c.Visibility != Visibility.Collapsed).ToList();
        var rows = Rows(visible.Select(x => GetUnits(x.c)).ToList(), width, SingleRow);

        var slots = new List<(int, Rect)>();
        var y = 0.0;

        foreach (var row in rows)
        {
            var perRow = SingleRow ? int.MaxValue : UnitsPerRow(width);
            int Span(UIElement c) => Math.Min(Math.Max(1, GetUnits(c)), perRow);
            var rowUnits = row.Sum(r => Span(visible[r].c));
            var unitWidth = (width - Gap * (row.Count - 1)) / rowUnits;
            var x = 0.0;
            var rowHeight = 0.0;
            var rowStart = slots.Count;

            foreach (var r in row)
            {
                var (child, index) = visible[r];
                var w = unitWidth * Span(child);
                child.Measure(new Size(w, double.PositiveInfinity));
                var h = child.DesiredSize.Height;
                slots.Add((index, new Rect(x, y, w, h)));
                rowHeight = Math.Max(rowHeight, h);
                x += w + Gap;
            }

            if (EqualHeights)
            {
                for (var k = rowStart; k < slots.Count; k++)
                {
                    var (i, slot) = slots[k];
                    slots[k] = (i, new Rect(slot.X, slot.Y, slot.Width, rowHeight));
                }
            }

            y += rowHeight + Gap;
        }

        return slots;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? MinUnitWidth * 3 : availableSize.Width;
        var slots = Arrange(width);
        var height = slots.Count == 0 ? 0 : slots.Max(s => s.Slot.Bottom);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (index, slot) in Arrange(finalSize.Width))
            InternalChildren[index].Arrange(slot);
        return finalSize;
    }
}
