using FleetMate.Core.Development;
using FleetMate.Core.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FleetMate.WinUI.Shell.Widgets;

/// <summary>
/// The treemap and the small figures above a bar chart, for the cards that
/// need them (Devices' Platform Distribution and Errors by Category). The
/// tiles are laid out by FleetMate.Core's <see cref="TreemapLayout"/>, as
/// FleetMate.GUI and the Mac lay them out; the legend under them is the
/// keyboard path, one stop per slice, and a tile opens the same thing as its row.
/// </summary>
internal static class GraphTreemap
{
    private const double Height = 120;
    private const double Gap = 2;

    public static FrameworkElement Build(GraphCard card, Action<GraphFilter> open)
    {
        var bars = card.Bars;
        var total = bars.Sum(b => Math.Max(b.Value, 0));
        var slices = bars.Select(b => new ChartSlice(b.Label, b.Value)).ToList();
        var canvas = new Canvas { Height = Height };
        AutomationProperties.SetAccessibilityView(canvas, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

        void Layout(double width)
        {
            canvas.Children.Clear();
            canvas.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, Height) };
            if (width <= 0) return;
            foreach (var tile in TreemapLayout.Tiles(slices, 0, 0, width, Height))
            {
                var bar = bars[tile.Index];
                var block = new Border
                {
                    Width = Math.Max(0, tile.Width - Gap),
                    Height = Math.Max(0, tile.Height - Gap),
                    CornerRadius = new CornerRadius(4),
                    Background = GraphTheme.Bar(bar.Tone, tile.Index),
                };
                ToolTipService.SetToolTip(block, $"{bar.Label}\n{bar.Value:N0}  ·  {TreemapLayout.SharePercent(bar.Value, total)}%");
                if (tile.Width > 48 && tile.Height > 30)
                {
                    block.Child = new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Children =
                        {
                            OnTile(bar.Label, bold: true),
                            OnTile(bar.Value.ToString("N0"), bold: false),
                        },
                    };
                }
                if (bar.Open is { } target) block.Tapped += (_, _) => open(target);
                Canvas.SetLeft(block, tile.X);
                Canvas.SetTop(block, tile.Y);
                canvas.Children.Add(block);
            }
        }
        canvas.SizeChanged += (_, e) => Layout(e.NewSize.Width);

        // The legend: whole entries wrap; each is the slice's keyboard stop and button.
        var legend = GraphCardView.ChartGroup(new GraphWrapPanel { HorizontalSpacing = 4, VerticalSpacing = 0 }, card.Title);
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var entry = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    new Microsoft.UI.Xaml.Shapes.Ellipse
                    {
                        Width = 8, Height = 8, Fill = GraphTheme.Bar(bar.Tone, i), VerticalAlignment = VerticalAlignment.Center,
                    },
                    GraphTheme.Caption(bar.Label),
                    new TextBlock
                    {
                        Text = bar.Value.ToString("N0"),
                        Style = GraphTheme.Style("CaptionTextBlockStyle"),
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = GraphTheme.Primary,
                    },
                },
            };
            legend.Children.Add(GraphCardView.Item(entry, bar.Description, $"{GraphCardView.Slug(card.Title)}{i}",
                bar.Open is { } target ? () => open(target) : null));
        }

        return new StackPanel { Spacing = 4, Children = { canvas, legend } };
    }

    /// <summary>White on the tile's colour, which every series brush is dark enough to carry.</summary>
    private static TextBlock OnTile(string text, bool bold) => new()
    {
        Text = text,
        Style = GraphTheme.Style("CaptionTextBlockStyle"),
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
        HorizontalAlignment = HorizontalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxLines = 1,
    };

    /// <summary>A card's figures as small coloured numbers over their labels, above its chart.</summary>
    public static FrameworkElement WithStats(GraphCard card, FrameworkElement chart)
    {
        var stats = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(8, 0, 0, 6) };
        foreach (var kpi in card.Kpis)
        {
            var stat = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = kpi.Value,
                        Style = GraphTheme.Style("BodyStrongTextBlockStyle"),
                        Foreground = GraphTheme.Tone(kpi.Tone),
                    },
                    GraphTheme.Caption(kpi.Title),
                },
            };
            AutomationProperties.SetName(stat, kpi.Description);
            stats.Children.Add(stat);
        }
        return new StackPanel { Children = { stats, chart } };
    }
}
