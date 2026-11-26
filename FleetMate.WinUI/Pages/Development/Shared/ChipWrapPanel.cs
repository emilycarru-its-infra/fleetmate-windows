using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace FleetMate.WinUI.Pages.Development.Shared;

/// <summary>
/// Filter chips that wrap onto another line rather than clip when the list is
/// narrow. A child whose Tag is "break" starts a group with extra space before it.
/// </summary>
public sealed partial class ChipWrapPanel : Panel
{
    public double Spacing { get; set; } = 4;
    public double GroupSpacing { get; set; } = 12;
    public double LineSpacing { get; set; } = 4;

    protected override Size MeasureOverride(Size availableSize) => Layout(availableSize.Width, arrange: false);

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Layout(double width, bool arrange)
    {
        double x = 0, y = 0, lineHeight = 0, widest = 0;
        var first = true;
        foreach (var child in Children)
        {
            if (!arrange) child.Measure(new Size(width, double.PositiveInfinity));
            if (child.Visibility == Visibility.Collapsed) continue;
            var size = child.DesiredSize;
            var gap = first ? 0 : (child is FrameworkElement { Tag: "break" } ? GroupSpacing : Spacing);
            if (!first && x + gap + size.Width > width)
            {
                x = 0;
                y += lineHeight + LineSpacing;
                lineHeight = 0;
                gap = 0;
            }
            x += gap;
            if (arrange) child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width;
            widest = Math.Max(widest, x);
            lineHeight = Math.Max(lineHeight, size.Height);
            first = false;
        }
        return new Size(double.IsInfinity(width) ? widest : Math.Min(widest, width), y + lineHeight);
    }
}
