using FleetMate.Core.Development;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.ViewManagement;

namespace FleetMate.WinUI.Pages.Development.Graphs;

/// <summary>
/// Draws one <see cref="GraphCard"/> from FleetMate.Core: KPI tiles, a donut,
/// horizontal bars, columns per day or week, a list, or its loading, empty and
/// all-clear states. Every bar, slice, column and row is a keyboard stop with
/// an accessible name; one that opens something is a button. Tab enters a
/// chart once and the arrow keys move inside it.
/// </summary>
internal static class GraphCardView
{
    public static FrameworkElement Build(GraphCard card, Action<GraphFilter> open)
    {
        if (card.Kind == GraphCardKind.Kpis) return Kpis(card, open);

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        var title = new TextBlock
        {
            Text = card.Title,
            Style = GraphTheme.Style("BodyStrongTextBlockStyle"),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None,
        };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level3);
        header.Children.Add(title);
        if (card.Loading)
        {
            header.Children.Add(new ProgressRing
            {
                IsActive = true, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center,
                IsTabStop = false,
            });
        }

        FrameworkElement body = card.Body switch
        {
            GraphCardBody.Loading => Skeleton(),
            GraphCardBody.Empty => Empty(card.Message),
            GraphCardBody.AllClear => AllClear(card.Message, card.Detail),
            _ => Chart(card, open),
        };
        Grid.SetRow(body, 1);

        var grid = new Grid { RowDefinitions = { new() { Height = GridLength.Auto }, new() { Height = new GridLength(1, GridUnitType.Star) } } };
        grid.Children.Add(header);
        grid.Children.Add(body);

        var border = new Border
        {
            Style = GraphTheme.Style("CardStyle"),
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = grid,
        };
        AutomationProperties.SetName(border, card.Description);
        AutomationProperties.SetAutomationId(border, $"DevelopmentGraph{Slug(card.Title)}");
        GraphFlowPanel.SetUnits(border, card.Units);
        return border;
    }

    private static FrameworkElement Chart(GraphCard card, Action<GraphFilter> open) => card.Kind switch
    {
        GraphCardKind.Donut => GraphCharts.Donut(card, open),
        GraphCardKind.Columns => GraphCharts.Columns(card),
        GraphCardKind.List => List(card, open),
        _ => Bars(card, open),
    };

    // MARK: - KPI tiles

    /// <summary>Tiles stacked in one cell, sharing its height, so the column ends level with the cards beside it.</summary>
    private static FrameworkElement Kpis(GraphCard card, Action<GraphFilter> open)
    {
        var grid = new Grid { RowSpacing = 8, VerticalAlignment = VerticalAlignment.Stretch };
        for (var i = 0; i < card.Kpis.Count; i++)
        {
            var kpi = card.Kpis[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var tile = KpiTile(kpi, open);
            Grid.SetRow(tile, i);
            grid.Children.Add(tile);
        }
        GraphFlowPanel.SetUnits(grid, 1);
        return grid;
    }

    private static FrameworkElement KpiTile(GraphKpi kpi, Action<GraphFilter> open)
    {
        var accent = new Border { Width = 4, CornerRadius = new CornerRadius(2), Background = GraphTheme.Tone(kpi.Tone), Margin = new Thickness(0, 2, 12, 2) };
        FrameworkElement value = kpi.Loading
            ? Block(56, 24, new Thickness(0, 2, 0, 4))
            : new TextBlock { Text = kpi.Value, Style = GraphTheme.Style("SubtitleTextBlockStyle"), Foreground = GraphTheme.Primary };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { value, GraphTheme.Caption(kpi.Title, wrap: true) } };
        Grid.SetColumn(text, 1);
        var row = new Grid
        {
            ColumnDefinitions = { new() { Width = GridLength.Auto }, new() { Width = new GridLength(1, GridUnitType.Star) } },
            Children = { accent, text },
        };
        if (kpi.Loading) Pulse(value);

        var id = $"DevelopmentGraphKpi{Slug(kpi.Title)}";
        if (kpi.Open is { } target && !kpi.Loading)
        {
            var button = new Button
            {
                Content = row,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(12, 10, 12, 10),
                Background = DevelopmentUi.Theme("CardBackgroundFillColorDefaultBrush"),
                BorderBrush = DevelopmentUi.Theme("CardStrokeColorDefaultBrush"),
                CornerRadius = (CornerRadius)Application.Current.Resources["OverlayCornerRadius"],
            };
            button.Click += (_, _) => open(target);
            ToolTipService.SetToolTip(button, $"{kpi.Description}. Show them");
            AutomationProperties.SetName(button, kpi.Description);
            AutomationProperties.SetAutomationId(button, id);
            return button;
        }

        var tile = new Border
        {
            Style = GraphTheme.Style("CardStyle"),
            Padding = new Thickness(12, 10, 12, 10),
            Child = row,
        };
        AutomationProperties.SetName(tile, kpi.Loading ? $"{kpi.Title}: loading" : kpi.Description);
        AutomationProperties.SetAutomationId(tile, id);
        return tile;
    }

    // MARK: - Bars

    /// <summary>Label | bar | count rows, so long names stay readable; the whole row is the target.</summary>
    private static FrameworkElement Bars(GraphCard card, Action<GraphFilter> open)
    {
        var max = Math.Max(1, card.Bars.Count == 0 ? 1 : card.Bars.Max(b => b.Value));
        var stack = ChartGroup(new StackPanel { Spacing = 2 }, card.Title);
        for (var i = 0; i < card.Bars.Count; i++)
        {
            var bar = card.Bars[i];
            var value = Math.Max(bar.Value, 0);
            var row = new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions =
                {
                    new() { Width = new GridLength(2, GridUnitType.Star), MaxWidth = 180 },
                    new() { Width = new GridLength(3, GridUnitType.Star), MinWidth = 32 },
                    new() { Width = GridLength.Auto },
                },
            };
            var label = GraphTheme.Caption(bar.Label, secondary: false, wrap: true);
            var track = new Grid
            {
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = GraphTheme.Track,
                VerticalAlignment = VerticalAlignment.Center,
                ColumnDefinitions =
                {
                    new() { Width = new GridLength(value, GridUnitType.Star), MinWidth = value > 0 ? 4 : 0 },
                    new() { Width = new GridLength(max - value, GridUnitType.Star) },
                },
                Children = { new Border { CornerRadius = new CornerRadius(4), Background = GraphTheme.Bar(bar.Tone, i) } },
            };
            var count = GraphTheme.Caption(bar.Value.ToString("N0"));
            count.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(track, 1);
            Grid.SetColumn(count, 2);
            row.Children.Add(label);
            row.Children.Add(track);
            row.Children.Add(count);

            stack.Children.Add(Item(row, bar.Description, $"{Slug(card.Title)}{i}",
                bar.Open is { } target ? () => open(target) : null));
        }
        return stack;
    }

    // MARK: - List

    private static FrameworkElement List(GraphCard card, Action<GraphFilter> open)
    {
        var stack = ChartGroup(new StackPanel { Spacing = 2 }, card.Title);
        foreach (var (row, i) in card.Rows.Take(card.VisibleRows).Select((r, i) => (r, i)))
        {
            var content = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = row.Title, Style = GraphTheme.Style("BodyTextBlockStyle"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None,
                    },
                    GraphTheme.Caption(row.Detail, wrap: true),
                },
            };
            stack.Children.Add(Item(content, $"{row.Title}, {row.Detail}", $"{Slug(card.Title)}{i}",
                row.Open is { } target ? () => open(target) : null));
        }
        return stack;
    }

    // MARK: - States

    private static FrameworkElement Empty(string message)
    {
        var text = GraphTheme.Caption(message, wrap: true);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.TextAlignment = TextAlignment.Center;
        text.Margin = new Thickness(0, 20, 0, 20);
        return text;
    }

    private static FrameworkElement AllClear(string title, string detail)
    {
        var message = new TextBlock
        {
            Text = title, Style = GraphTheme.Style("BodyStrongTextBlockStyle"),
            HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        };
        var second = GraphTheme.Caption(detail, wrap: true);
        second.TextAlignment = TextAlignment.Center;
        second.HorizontalAlignment = HorizontalAlignment.Center;
        return new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 8),
            Children =
            {
                new FontIcon
                {
                    Glyph = "", FontSize = 20, Foreground = GraphTheme.Success,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4),
                },
                message,
                second,
            },
        };
    }

    /// <summary>A chart-shaped placeholder: a title bar over six columns, pulsing while data loads.</summary>
    private static FrameworkElement Skeleton()
    {
        var bars = new Grid { Height = 96, ColumnSpacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        int[] heights = { 60, 90, 45, 80, 55, 70 };
        for (var i = 0; i < heights.Length; i++)
        {
            bars.ColumnDefinitions.Add(new ColumnDefinition());
            var bar = Block(double.NaN, heights[i], new Thickness(0));
            bar.VerticalAlignment = VerticalAlignment.Bottom;
            bar.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(bar, i);
            bars.Children.Add(bar);
        }
        var panel = new StackPanel { Children = { Block(140, 14, new Thickness(0)), bars } };
        AutomationProperties.SetName(panel, "Loading");
        Pulse(panel);
        return panel;
    }

    private static Border Block(double width, double height, Thickness margin) => new()
    {
        Width = width,
        Height = height,
        Margin = margin,
        CornerRadius = new CornerRadius(4),
        Background = GraphTheme.Placeholder,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    /// <summary>A slow fade in and out; still when Windows has animations turned off.</summary>
    private static void Pulse(UIElement element)
    {
        if (!new UISettings().AnimationsEnabled) return;
        var fade = new DoubleAnimation
        {
            From = 1, To = 0.45, Duration = TimeSpan.FromMilliseconds(900),
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));
        var story = new Storyboard { Children = { fade } };
        if (element is FrameworkElement fe)
        {
            fe.Loaded += (_, _) => story.Begin();
            fe.Unloaded += (_, _) => story.Stop();
        }
        else story.Begin();
    }

    // MARK: - Pieces

    /// <summary>
    /// One bar, slice, column or row: a button when it opens something,
    /// otherwise a focusable stop that still reads its name and shows its tooltip.
    /// </summary>
    public static Control Item(UIElement content, string name, string id, Action? invoke)
    {
        Control item;
        if (invoke != null)
        {
            var button = new Button
            {
                Content = content,
                Style = GraphTheme.Style("SubtleButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(8, 4, 8, 4),
            };
            button.Click += (_, _) => invoke();
            item = button;
        }
        else
        {
            item = new GraphStaticItem
            {
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(8, 4, 8, 4),
            };
        }
        AutomationProperties.SetName(item, name);
        AutomationProperties.SetAutomationId(item, $"DevelopmentGraph{id}");
        ToolTipService.SetToolTip(item, name);
        return item;
    }

    /// <summary>Tab enters the chart once; the arrow keys move between its items.</summary>
    public static T ChartGroup<T>(T panel, string title) where T : Panel
    {
        panel.TabFocusNavigation = KeyboardNavigationMode.Once;
        panel.XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
        AutomationProperties.SetName(panel, title);
        return panel;
    }

    public static string Slug(string title) => new(title.Where(char.IsLetterOrDigit).ToArray());
}

/// <summary>
/// A figure that opens nothing but is still a keyboard stop, with the system
/// focus rectangle, so it can be reached and read. UI Automation sees it as a
/// list item carrying its accessible name.
/// </summary>
internal sealed partial class GraphStaticItem : ContentControl
{
    public GraphStaticItem()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        Background = DevelopmentUi.Theme("SubtleFillColorTransparentBrush");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GraphStaticItemPeer(this);

    private sealed partial class GraphStaticItemPeer(GraphStaticItem owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
        protected override string GetClassNameCore() => nameof(GraphStaticItem);
        protected override bool IsKeyboardFocusableCore() => true;
    }
}
