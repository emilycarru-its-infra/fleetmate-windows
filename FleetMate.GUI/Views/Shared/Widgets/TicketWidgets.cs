using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        cards.Add(Stretch(WidgetCards.Card("By Status", byStatus.Count == 0
            ? Empty(loading)
            : WidgetCards.Donut(
                byStatus.Select(c => new ChartSlice(c.Label, c.Value)).ToList(),
                v => filters.Toggle(TicketFilterCategory.Status, v),
                byStatus.Select(c => Fade(ToSk(StatusColor(c.Label)), Faded(filters, TicketFilterCategory.Status, c.Label))).ToList()))));

        cards.Add(BarCard("By Priority", new TicketStats(tickets(TicketFilterCategory.Priority)).ByPriority,
            TicketFilterCategory.Priority, filters, loading, PriorityColor));
        cards.Add(BarCard("By Age", new TicketStats(tickets(TicketFilterCategory.Age)).ByAge,
            TicketFilterCategory.Age, filters, loading, AgeColor));
        cards.Add(BarCard("By Responsible", new TicketStats(tickets(TicketFilterCategory.Responsible)).ByResponsible,
            TicketFilterCategory.Responsible, filters, loading, v => v == TicketStats.Unassigned ? Gray : Teal));
        cards.Add(BarCard("By Group", new TicketStats(tickets(TicketFilterCategory.Group)).ByGroup,
            TicketFilterCategory.Group, filters, loading, v => v == TicketStats.Unassigned ? Gray : Indigo));

        return cards;
    }

    /// <summary>One figure, which doubles as a filter: it toggles the selection it counts and lights up while that selection is on.</summary>
    internal sealed record Tile(string Title, int Value, string Glyph, Color Color, bool IsOn, string Help, Action Action);

    /// <summary>
    /// Statuses that count as open: every status the list could show except
    /// On Hold. The Open figure selects exactly these.
    /// </summary>
    internal static HashSet<string> OpenStatuses(IEnumerable<TdxTicket> ticketsIgnoringStatus) =>
        new TicketStats(ticketsIgnoringStatus).ByStatus.Select(c => c.Label)
            .Where(l => l != TicketStats.OnHoldStatus).ToHashSet();

    private static List<Tile> Tiles(Func<TicketFilterCategory?, List<TdxTicket>> tickets, TicketFilters filters, TicketStats stats)
    {
        var openStatuses = OpenStatuses(tickets(TicketFilterCategory.Status));
        var openOn = openStatuses.Count > 0 && filters.Selected(TicketFilterCategory.Status).SetEquals(openStatuses);

        Tile Single(string title, int value, string glyph, Color color, string help, TicketFilterCategory category, string v) =>
            new(title, value, glyph, color, filters.IsSelected(category, v), help, () => filters.Toggle(category, v));

        return new List<Tile>
        {
            new("Showing", stats.Total, "", Secondary, false, "Clear every filter", filters.ClearAll),
            new("Open", stats.Open, "", Purple, openOn, "Show open tickets",
                () => filters.Set(TicketFilterCategory.Status, openOn ? Array.Empty<string>() : openStatuses)),
            Single("On Hold", stats.OnHold, "", Yellow, "Show tickets on hold",
                TicketFilterCategory.Status, TicketStats.OnHoldStatus),
            Single("Unassigned", stats.UnassignedCount, "", Teal, "Show tickets nobody is responsible for",
                TicketFilterCategory.Responsible, TicketStats.Unassigned),
            Single("SLA Violated", stats.SlaViolated, "", Orange, "Show tickets past their SLA",
                TicketFilterCategory.Sla, TicketFilters.SlaViolated),
            Single($"Over {TicketStats.AgingDays} days", stats.Aging, "", Indigo,
                $"Show tickets older than {TicketStats.AgingDays} days", TicketFilterCategory.Age, TicketStats.AgingBucket),
        };
    }

    /// <summary>Six small figures in a 3 × 2 grid, so they take one card's height. Each is a button.</summary>
    private static Border KpiGrid(IReadOnlyList<Tile> tiles, bool waiting)
    {
        var grid = new UniformGrid { Rows = 2, Columns = 3 };
        foreach (var t in tiles) grid.Children.Add(TileView(t, waiting));

        var card = new Border
        {
            Style = (Style)Application.Current.FindResource("CardStyle"),
            MaxHeight = WidgetCards.CardMaxHeight,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = grid,
        };
        WidgetFlowPanel.SetUnits(card, 2);
        return card;
    }

    private static UIElement TileView(Tile t, bool waiting)
    {
        var accent = Application.Current.TryFindResource("SystemAccentColor") is Color a ? a : Color.FromRgb(0, 120, 212);
        var medium = (Brush)Application.Current.FindResource("SystemControlForegroundBaseMediumBrush");

        var row = new DockPanel { LastChildFill = true };
        var glyph = new TextBlock
        {
            Text = t.Glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16,
            Foreground = new SolidColorBrush(t.Color), VerticalAlignment = VerticalAlignment.Center,
            Width = 20, Margin = new Thickness(0, 0, 8, 0),
        };
        DockPanel.SetDock(glyph, Dock.Left);
        row.Children.Add(glyph);
        row.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = waiting ? "--" : t.Value.ToString("N0"), FontSize = 18, FontWeight = FontWeights.Bold },
                new TextBlock { Text = t.Title, FontSize = 11, Foreground = medium, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        });

        var tile = new Border
        {
            Child = row,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(t.IsOn ? Color.FromArgb(36, accent.R, accent.G, accent.B) : Color.FromArgb(16, 128, 128, 128)),
            BorderBrush = new SolidColorBrush(t.IsOn ? accent : Color.FromArgb(40, 128, 128, 128)),
            BorderThickness = new Thickness(t.IsOn ? 1.5 : 1),
            Cursor = Cursors.Hand,
            ToolTip = t.Help,
        };
        AutomationPropertiesName(tile, $"{t.Title}: {t.Value}. {t.Help}");
        tile.MouseLeftButtonUp += (_, _) => t.Action();
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
                v => Faded(filters, category, v))));

    /// <summary>Cards in a row share its height (see <see cref="WidgetFlowPanel.EqualHeights"/>).</summary>
    private static Border Stretch(Border card)
    {
        card.VerticalAlignment = VerticalAlignment.Stretch;
        return card;
    }

    private static UIElement Empty(bool loading) => WidgetCards.Caption(loading ? "Loading tickets..." : "No tickets match");

    /// <summary>Values outside an active selection fade, so a chart shows what is picked and what else could be.</summary>
    internal static bool Faded(TicketFilters filters, TicketFilterCategory category, string value)
    {
        var picked = filters.Selected(category);
        return picked.Count > 0 && !picked.Contains(value);
    }

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

    internal static Color StatusColor(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("new") || n.Contains("open")) return Blue;
        if (n.Contains("progress") || n.Contains("process")) return Orange;
        if (n.Contains("hold") || n.Contains("pending") || n.Contains("waiting")) return Yellow;
        if (n.Contains("resolved") || n.Contains("completed")) return Green;
        if (n.Contains("closed")) return Gray;
        if (n.Contains("cancel")) return Orange;
        return Secondary;
    }

    internal static Color PriorityColor(string name) => name switch
    {
        "Low" => Green, "Medium" => Blue, "High" => Orange, "Emergency" => Purple, _ => Gray,
    };

    internal static Color AgeColor(string bucket) => bucket switch
    {
        "Today" => Green, "1–7 days" => Blue, "8–30 days" => Orange, "Over 30 days" => Purple, _ => Gray,
    };

    private static SKColor ToSk(Color c) => new(c.R, c.G, c.B, c.A);

    private static SKColor Fade(SKColor c, bool faded) => faded ? c.WithAlpha(77) : c;
}
