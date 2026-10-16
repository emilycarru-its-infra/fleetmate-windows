using System.Windows;
using System.Windows.Controls;

namespace FleetMate.GUI.Views.Shared.Widgets;

/// <summary>
/// A donut and its legend that fit whatever width their card has: side by
/// side while the legend still gets <see cref="MinLegendWidth"/> beside the
/// full-size donut, otherwise a <see cref="CompactDiameter"/> donut centred
/// over a full-width legend. The legend keeps every row in both layouts and
/// takes whatever height is left, scrolling if it needs more.
/// </summary>
internal sealed class AdaptiveDonutPanel : Panel
{
    public const double CompactDiameter = 90;
    public const double MinLegendWidth = 110;
    public const double Gap = 14;
    public const double StackGap = 6;

    private readonly UIElement _chart;
    private readonly UIElement _legend;
    private readonly double _diameter;
    private readonly Action<double> _resize;
    private bool? _stacked;

    /// <param name="resize">Sets the donut to a diameter; called only when the layout changes.</param>
    public AdaptiveDonutPanel(UIElement chart, UIElement legend, double diameter, Action<double> resize)
    {
        _chart = chart;
        _legend = legend;
        _diameter = diameter;
        _resize = resize;
        Children.Add(chart);
        Children.Add(legend);
    }

    /// <summary>Whether the last layout stacked the donut over the legend.</summary>
    public bool IsStacked => _stacked == true;

    internal static bool Stacks(double width, double diameter) =>
        !double.IsInfinity(width) && width < diameter + Gap + MinLegendWidth;

    private double Diameter => IsStacked ? Math.Min(CompactDiameter, _diameter) : _diameter;

    protected override Size MeasureOverride(Size availableSize)
    {
        var stacked = Stacks(availableSize.Width, _diameter);
        if (_stacked != stacked)
        {
            _stacked = stacked;
            _resize(Diameter);
        }

        var d = Diameter;
        _chart.Measure(new Size(d, d));
        if (stacked)
        {
            var legendHeight = Math.Max(0, availableSize.Height - d - StackGap);
            _legend.Measure(new Size(availableSize.Width, legendHeight));
            return new Size(availableSize.Width, d + StackGap + _legend.DesiredSize.Height);
        }

        var legendWidth = double.IsInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : Math.Max(0, availableSize.Width - d - Gap);
        _legend.Measure(new Size(legendWidth, availableSize.Height));
        var width = double.IsInfinity(availableSize.Width) ? d + Gap + _legend.DesiredSize.Width : availableSize.Width;
        return new Size(width, Math.Max(d, _legend.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var d = Diameter;
        if (IsStacked)
        {
            _chart.Arrange(new Rect(Math.Max(0, (finalSize.Width - d) / 2), 0, d, d));
            _legend.Arrange(new Rect(0, d + StackGap, finalSize.Width, Math.Max(0, finalSize.Height - d - StackGap)));
        }
        else
        {
            _chart.Arrange(new Rect(0, Math.Max(0, (finalSize.Height - d) / 2), d, d));
            _legend.Arrange(new Rect(d + Gap, 0, Math.Max(0, finalSize.Width - d - Gap), finalSize.Height));
        }
        return finalSize;
    }
}
