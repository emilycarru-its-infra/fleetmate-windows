using FleetMate.Core.Development;
using Microsoft.UI.Xaml;

namespace FleetMate.WinUI.Pages.Development.Graphs;

/// <summary>
/// Development's Graphs strip: one set of cards per sub-tab, built from what
/// the tab has already loaded, and Repos' Insights. A placeholder until the
/// graphs are ported: nothing is drawn yet.
/// </summary>
public static class DevelopmentGraphs
{
    /// <summary>Whether the strip shows. Development opens on the work itself, so it starts hidden.</summary>
    public static bool IsShown { get; set; }

    /// <summary>Whether a change to the <paramref name="cacheKey"/> cache redraws <paramref name="segment"/>'s strip.</summary>
    public static bool Follows(DevelopmentSegment segment, string cacheKey) => false;

    /// <summary>The strip for <paramref name="segment"/>, or null when it has none.</summary>
    public static UIElement? Build(DevelopmentPage page, DevelopmentSegment segment) =>
        segment == DevelopmentSegment.Repos ? page.Repos.Insights : null;
}
