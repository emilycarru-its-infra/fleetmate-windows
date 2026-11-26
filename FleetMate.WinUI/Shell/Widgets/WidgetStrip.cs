using FleetMate.Core.Development;
using FleetMate.Core.Shared;
using Microsoft.UI.Xaml;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace FleetMate.WinUI.Shell.Widgets;

/// <summary>
/// The widget strip every tab shares, as on FleetMate for Mac: cards of
/// figures above the tab's content, shown or hidden per tab by the toolbar's
/// Graphs button. A page that has widgets:
///
/// <code>
/// // App's constructor, beside ShellPages.Register:
/// WidgetStrip.RegisterTab("Devices");
///
/// // The page, whenever its data or WidgetStrip.Changed for its tab says so:
/// Host.Content = WidgetStrip.IsShown("Devices")
///     ? WidgetStrip.Build("Devices", DeviceGraphCards.For(devices), filter => ApplyFilter(filter))
///     : null;
/// </code>
///
/// The cards and their figures are FleetMate.Core's <see cref="GraphCard"/>
/// records, the same ones the WPF app draws; this draws them and hands each
/// click back to the page as a <see cref="GraphFilter"/>.
/// </summary>
public static class WidgetStrip
{
    private static readonly HashSet<string> Tabs = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, bool> DemoShown = new(StringComparer.Ordinal);

    /// <summary>Raised on the UI thread with the tab whose strip was shown or hidden.</summary>
    public static event Action<string>? Changed;

    /// <summary>Mark <paramref name="tab"/> as one that draws a strip, so the toolbar offers Graphs on it.</summary>
    public static void RegisterTab(string tab) => Tabs.Add(tab);

    public static bool HasWidgets(string? tab) => tab != null && Tabs.Contains(tab);

    /// <summary>
    /// Whether <paramref name="tab"/>'s strip shows, kept with the WPF app's
    /// choice (<see cref="WidgetVisibility"/>). Demo mode keeps it in memory,
    /// so a capture never writes the person's own choice.
    /// </summary>
    public static bool IsShown(string tab) =>
        App.Services.DemoMode
            ? DemoShown.TryGetValue(tab, out var shown) ? shown : WidgetVisibility.ShownByDefault(tab)
            : WidgetVisibility.IsShown(tab);

    public static void SetShown(string tab, bool shown)
    {
        if (IsShown(tab) == shown) return;
        if (App.Services.DemoMode) DemoShown[tab] = shown;
        else WidgetVisibility.SetShown(tab, shown);
        Changed?.Invoke(tab);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, StrongBox<double>> Budgets = new();

    /// <summary>
    /// Cap the strip's scroll host at <paramref name="fraction"/> of the page,
    /// snapped to whole rows of cards so none is cut through its middle: the
    /// rows that fit, or the whole first row when even that is taller. The
    /// rest scroll. Call when the page's height changes and after drawing a
    /// strip; the cap follows the cards as they lay out.
    /// </summary>
    public static void CapToRows(ScrollViewer host, double pageHeight, double fraction = 0.4, double minimum = 160)
    {
        var budget = Math.Max(minimum, pageHeight * fraction);
        Budgets.AddOrUpdate(host, new StrongBox<double>(budget));
        if (FindPanel(host.Content as DependencyObject) is not { } panel)
        {
            host.MaxHeight = budget;
            return;
        }
        panel.RowsMeasured -= OnRowsMeasured;
        panel.RowsMeasured += OnRowsMeasured;
        host.MaxHeight = Snap(panel.RowBottoms, budget);

        void OnRowsMeasured(GraphFlowPanel measured)
        {
            if (Budgets.TryGetValue(host, out var box)) host.MaxHeight = Snap(measured.RowBottoms, box.Value);
        }
    }

    /// <summary>The tallest whole-rows height within <paramref name="budget"/>, or the first row when none fits.</summary>
    internal static double Snap(IReadOnlyList<double> rowBottoms, double budget)
    {
        if (rowBottoms.Count == 0) return budget;
        var fit = rowBottoms.Where(b => b <= budget).DefaultIfEmpty(rowBottoms[0]).Max();
        // A little room for the scroll host's own rounding.
        return Math.Ceiling(fit) + 2;
    }

    private static GraphFlowPanel? FindPanel(DependencyObject? root)
    {
        if (root is GraphFlowPanel panel) return panel;
        if (root is ContentControl { Content: DependencyObject content }) return FindPanel(content);
        if (root is Panel container)
            foreach (var child in container.Children)
                if (FindPanel(child) is { } found) return found;
        if (root is Border { Child: DependencyObject inner }) return FindPanel(inner);
        return null;
    }

    /// <summary>
    /// Draw <paramref name="cards"/> as a strip for <paramref name="tab"/>.
    /// A card that fails to draw is left out and logged; the rest still show.
    /// </summary>
    public static FrameworkElement Build(string tab, IEnumerable<GraphCard> cards, Action<GraphFilter> open)
    {
        var panel = new GraphFlowPanel { Margin = new Thickness(0, 0, 0, 4) };
        AutomationProperties.SetName(panel, $"{tab} graphs");
        AutomationProperties.SetAutomationId(panel, $"{tab}Graphs");
        GraphCardView.IdPrefix = $"{tab}Graph";
        foreach (var card in cards)
        {
            try
            {
                panel.Children.Add(GraphCardView.Build(card, open));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[widgets] Could not draw the {Tab} {Card} graph", tab, card.Title);
            }
        }
        return panel;
    }
}
