namespace FleetMate.Core.Config;

/// <summary>
/// The top-level tabs every window shows, shared by the WPF and WinUI apps and
/// kept in step with FleetMate for Mac (<c>AppTab</c>): declaration order is
/// display order and the Ctrl+1…Ctrl+9 order, Development leads and is the
/// launch tab, and Tickets goes last. TicketsMate carries the Tickets tab alone.
/// </summary>
public static class AppTabs
{
    /// <summary>Every tab, in tab-bar order.</summary>
    public static IReadOnlyList<string> Order { get; } = AppModules.Tabs.Select(m => m.Tag).ToList();

    /// <summary>The tabs showing for <paramref name="edition"/> once <paramref name="hidden"/> modules are left out.</summary>
    public static IReadOnlyList<string> Visible(AppEdition edition, IReadOnlySet<string> hidden) =>
        edition.IsTicketsOnly ? edition.Tabs(Order) : AppModules.Visible(Order, hidden);

    /// <summary>Where the app opens: Development, or Tickets in TicketsMate.</summary>
    public static string LaunchTab(AppEdition edition) => edition.IsTicketsOnly ? "Tickets" : "Development";

    /// <summary>
    /// The tab <paramref name="visible"/> opens on, the launch tab when it is
    /// showing and otherwise the first that is.
    /// </summary>
    public static string? StartTab(AppEdition edition, IReadOnlyList<string> visible) =>
        visible.Contains(LaunchTab(edition)) ? LaunchTab(edition) : visible.FirstOrDefault();

    /// <summary>The tab Ctrl+<paramref name="number"/> opens among the visible ones, or null.</summary>
    public static string? ForShortcut(IReadOnlyList<string> visible, int number) =>
        number is >= 1 and <= 9 && number <= visible.Count ? visible[number - 1] : null;

    /// <summary>The Segoe Fluent Icons glyph for a tab.</summary>
    public static string Glyph(string tag) =>
        AppModules.Tabs.FirstOrDefault(m => m.Tag == tag)?.Glyph ?? "";
}
