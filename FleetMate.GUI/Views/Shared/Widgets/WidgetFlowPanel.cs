using System.Windows;
using System.Windows.Controls;

namespace FleetMate.GUI.Views.Shared.Widgets;

/// <summary>
/// Lays widget cards out in rows that fill the full width. A card spans one
/// or more units (<see cref="UnitsProperty"/>); a row takes as many units as
/// fit while each unit stays at least <see cref="MinUnitWidth"/> wide, and
/// wraps otherwise. Cards keep their natural height (capped by their own
/// MaxHeight) unless <see cref="EqualHeights"/> is set, which gives every
/// card in a row the row's tallest height so the row reads as one band.
/// With <see cref="SingleRow"/> every card shares one row at any width, each
/// unit taking an equal share, so nothing wraps beneath it.
/// </summary>
public sealed class WidgetFlowPanel : Panel
{
    public const double MinUnitWidth = 240;
    public const double Gap = 12;

    private bool _equalHeights;
    private bool _singleRow;

    /// <summary>Keep every card on one row, narrowing them rather than wrapping.</summary>
    public bool SingleRow
    {
        get => _singleRow;
        set { _singleRow = value; InvalidateMeasure(); }
    }

    /// <summary>Offer every card its row's tallest height. Cards must stretch vertically to use it.</summary>
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

    /// <summary>
    /// Rows of child indices. A unit is never narrower than <see cref="MinUnitWidth"/>;
    /// a card wider than the whole row still gets a row of its own. With
    /// <paramref name="singleRow"/> every card is on the one row.
    /// </summary>
    internal static List<List<int>> Rows(IReadOnlyList<int> units, double width, bool singleRow = false)
    {
        if (singleRow)
            return units.Count == 0 ? new List<List<int>>() : new List<List<int>> { Enumerable.Range(0, units.Count).ToList() };

        var perRow = Math.Max(1, (int)Math.Floor((width + Gap) / (MinUnitWidth + Gap)));
        var rows = new List<List<int>>();
        var current = new List<int>();
        var used = 0;

        for (var i = 0; i < units.Count; i++)
        {
            var u = Math.Max(1, units[i]);
            if (current.Count > 0 && used + u > perRow)
            {
                rows.Add(current);
                current = new List<int>();
                used = 0;
            }
            current.Add(i);
            used += u;
        }

        if (current.Count > 0) rows.Add(current);
        return rows;
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
            var rowUnits = row.Sum(r => Math.Max(1, GetUnits(visible[r].c)));
            var unitWidth = (width - Gap * (row.Count - 1)) / rowUnits;
            var x = 0.0;
            var rowHeight = 0.0;
            var rowStart = slots.Count;

            foreach (var r in row)
            {
                var (child, index) = visible[r];
                var w = unitWidth * Math.Max(1, GetUnits(child));
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
