using FleetMate.Core.Shared;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FleetMate.Core.Models.Tickets;
using SkiaSharp;

namespace FleetMate.GUI.Views.Shared.Widgets;

/// <summary>
/// The Tickets widgets: six figures and five breakdowns, each a filter that
/// combines with the rest. Every breakdown counts the list's tickets with
/// every filter applied except its own category's, so its other values stay
/// on screen and can be added to the selection; clicking a value toggles it.
/// Values outside an active selection fade. Built by the Tickets page, which
/// owns the filters, and shared by FleetMate and TicketsMate.
/// </summary>
public static class TicketWidgets
{
    /// <param name="tickets">The list's tickets with every filter applied except the given category's (null: every filter).</param>
    /// <param name="filters">The list's own filters, which every widget reads and changes.</param>
    /// <param name="loading">Whether tickets are still loading.</param>
    public static List<UIElement> Build(Func<TicketFilterCategory?, List<TdxTicket>> tickets, TicketFilters filters, bool loading)
    {
        var shown = tickets(null);
        var stats = new TicketStats(shown);
        var waiting = loading && shown.Count == 0;

        var cards = new List<UIElement> { KpiGrid(Tiles(tickets, filters, stats), waiting) };

        var byStatus = new TicketStats(tickets(TicketFilterCategory.Status)).ByStatus;
        // The legend is a filter too: a row toggles its status and fades with the selection, as a wedge does.
        cards.Add(Stretch(WidgetCards.Card("By Status", byStatus.Count == 0
            ? Empty(loading)
            : WidgetCards.Donut(
                byStatus.Select(c => new ChartSlice(c.Label, c.Value)).ToList(),
                v => filters.Toggle(TicketFilterCategory.Status, v),
                byStatus.Select(c => Fade(ToSk(StatusColor(c.Label)), Faded(filters, TicketFilterCategory.Status, c.Label))).ToList(),
                size: 110, adaptive: true), loading: loading)));

        cards.Add(BarCard("By Priority", new TicketStats(tickets(TicketFilterCategory.Priority)).ByPriority,
            TicketFilterCategory.Priority, filters, loading, PriorityColor));
        cards.Add(BarCard("By Age", new TicketStats(tickets(TicketFilterCategory.Age)).ByAge,
            TicketFilterCategory.Age, filters, loading, AgeColor));
        cards.Add(BarCard("By Responsible", new TicketStats(tickets(TicketFilterCategory.Responsible)).ByResponsible,
            TicketFilterCategory.Responsible, filters, loading, v => ToneColor(TicketTones.Responsible(v))));
        cards.Add(BarCard("By Group", new TicketStats(tickets(TicketFilterCategory.Group)).ByGroup,
            TicketFilterCategory.Group, filters, loading, v => ToneColor(TicketTones.Group(v))));

        return cards;
    }

    /// <summary>One figure, which doubles as a filter: it toggles the selection it counts and lights up while that selection is on.</summary>
    internal sealed record Tile(string Title, int Value, string Glyph, Color Color, bool IsOn, string Help, Action Action);

    /// <summary>Statuses that count as open; see <see cref="TicketWidgetRules.OpenStatuses"/>.</summary>
    internal static HashSet<string> OpenStatuses(IEnumerable<TdxTicket> ticketsIgnoringStatus) =>
        TicketWidgetRules.OpenStatuses(ticketsIgnoringStatus);

    private static List<Tile> Tiles(Func<TicketFilterCategory?, List<TdxTicket>> tickets, TicketFilters filters, TicketStats stats) =>
        TicketWidgetRules.Tiles(tickets, filters)
            .Select(t => new Tile(t.Title, t.Value, t.Glyph, ToneColor(t.Tone), t.IsOn, t.Help, t.Action))
            .ToList();

    /// <summary>
    /// Six small figures in one chart-width card, so the five charts share
    /// its row: three columns of two where there is room, two compact columns
    /// of three where there is not (<see cref="KpiTilePanel"/>). Each is a
    /// button.
    /// </summary>
    internal static Border KpiGrid(IReadOnlyList<Tile> tiles, bool waiting, int units = 1, int maxColumns = 3)
    {
        var grid = new KpiTilePanel { MaxColumns = maxColumns };
        foreach (var t in tiles) grid.Children.Add(TileView(t, waiting));

        var card = new Border
        {
            Style = (Style)Application.Current.FindResource("CardStyle"),
            MaxHeight = WidgetCards.CardMaxHeight,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = grid,
        };
        WidgetFlowPanel.SetUnits(card, units);
        return card;
    }

    /// <summary>The figure and its icon on top, the label under them, the full text in the tooltip.</summary>
    private static UIElement TileView(Tile t, bool waiting)
    {
        var accent = Application.Current.TryFindResource("SystemAccentColor") is Color a ? a : Color.FromRgb(0, 120, 212);
        var medium = (Brush)Application.Current.FindResource("SystemControlForegroundBaseMediumBrush");

        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(new TextBlock
        {
            Text = t.Glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13,
            Foreground = new SolidColorBrush(t.Color), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
        });
        top.Children.Add(waiting
            ? WidgetCards.SkeletonBlock(28, 16, new Thickness(0, 1, 0, 1))
            : new TextBlock { Text = t.Value.ToString("N0"), FontSize = 16, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });

        var tile = new Border
        {
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    top,
                    new TextBlock { Text = t.Title, FontSize = WidgetCards.CaptionSize, Foreground = medium, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            },
            Padding = new Thickness(7, 4, 7, 4),
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(t.IsOn ? Color.FromArgb(36, accent.R, accent.G, accent.B) : Color.FromArgb(16, 128, 128, 128)),
            BorderBrush = new SolidColorBrush(t.IsOn ? accent : Color.FromArgb(40, 128, 128, 128)),
            BorderThickness = new Thickness(t.IsOn ? 1.5 : 1),
            Cursor = Cursors.Hand,
            // The label may be trimmed in a compact tile, so the tooltip carries it whole.
            ToolTip = $"{t.Title}: {t.Value:N0}. {t.Help}",
            Focusable = true,
            FocusVisualStyle = (Style?)Application.Current.TryFindResource(SystemParameters.FocusVisualStyleKey),
        };
        AutomationPropertiesName(tile, $"{t.Title}: {t.Value}. {t.Help}");
        if (t.IsOn) System.Windows.Automation.AutomationProperties.SetItemStatus(tile, "Selected");
        tile.MouseLeftButtonUp += (_, _) => t.Action();
        tile.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            e.Handled = true;
            t.Action();
        };
        return tile;
    }

    private static void AutomationPropertiesName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    private static UIElement BarCard(string title, IReadOnlyList<TicketStats.Count> counts, TicketFilterCategory category,
        TicketFilters filters, bool loading, Func<string, Color> colorOf) =>
        Stretch(WidgetCards.Card(title, counts.Count == 0
            ? Empty(loading)
            : WidgetCards.HorizontalBars(
                counts.Select(c => new ChartSlice(c.Label, c.Value)).ToList(),
                colorOf,
                v => filters.Toggle(category, v),
                v => Faded(filters, category, v),
                narrow: true), loading: loading));

    /// <summary>Cards in a row share its height (see <see cref="WidgetFlowPanel.EqualHeights"/>).</summary>
    private static Border Stretch(Border card)
    {
        card.VerticalAlignment = VerticalAlignment.Stretch;
        return card;
    }

    private static UIElement Empty(bool loading) => loading ? WidgetCards.Skeleton() : WidgetCards.Empty("No tickets match");

    /// <summary>Values outside an active selection fade; see <see cref="TicketWidgetRules.Faded"/>.</summary>
    internal static bool Faded(TicketFilters filters, TicketFilterCategory category, string value) =>
        TicketWidgetRules.Faded(filters, category, value);

    // Colours, matching the macOS app. No red: High is orange.

    private static readonly Color Blue = Color.FromRgb(33, 150, 243);
    private static readonly Color Green = Color.FromRgb(76, 175, 80);
    private static readonly Color Orange = Color.FromRgb(255, 152, 0);
    private static readonly Color Yellow = Color.FromRgb(242, 183, 0);
    private static readonly Color Purple = Color.FromRgb(156, 39, 176);
    private static readonly Color Teal = Color.FromRgb(0, 150, 136);
    private static readonly Color Indigo = Color.FromRgb(63, 81, 181);
    private static readonly Color Gray = Color.FromRgb(158, 158, 158);
    private static readonly Color Secondary = Color.FromRgb(128, 128, 128);

    internal static Color StatusColor(string name) => ToneColor(TicketTones.Status(name));

    internal static Color PriorityColor(string name) => ToneColor(TicketTones.Priority(name));

    internal static Color AgeColor(string bucket) => ToneColor(TicketTones.Age(bucket));

    internal static Color ToneColor(TicketTone tone) => tone switch
    {
        TicketTone.Blue => Blue,
        TicketTone.Green => Green,
        TicketTone.Orange => Orange,
        TicketTone.Yellow => Yellow,
        TicketTone.Purple => Purple,
        TicketTone.Teal => Teal,
        TicketTone.Indigo => Indigo,
        TicketTone.Gray => Gray,
        _ => Secondary,
    };

    private static SKColor ToSk(Color c) => new(c.R, c.G, c.B, c.A);

    private static SKColor Fade(SKColor c, bool faded) => faded ? c.WithAlpha(77) : c;
}
