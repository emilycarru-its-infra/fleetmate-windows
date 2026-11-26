using FleetMate.WinUI.Pages;

namespace FleetMate.WinUI.Shell;

/// <summary>
/// Which page each tab shows. The shell builds its tabs from
/// <c>AppTabs</c> (the same order and names as FleetMate for Mac) and opens
/// the page registered here for each, or <see cref="PlaceholderPage"/> when
/// none is. A page ported in a later phase registers itself in one line in
/// <see cref="App"/>'s constructor:
///
/// <code>
/// ShellPages.Register("Devices", typeof(DevicesPage));
/// </code>
///
/// The page is navigated to with its tab tag as the parameter, and reaches
/// services through <see cref="App.Services"/>.
/// </summary>
public static class ShellPages
{
    private static readonly Dictionary<string, Type> Pages = new(StringComparer.Ordinal);

    /// <summary>The settings page; Settings is not a tab, so it sits apart from them.</summary>
    public const string Settings = "Settings";

    public static void Register(string tag, Type page) => Pages[tag] = page;

    public static Type For(string tag) => Pages.TryGetValue(tag, out var page) ? page : typeof(PlaceholderPage);
}
