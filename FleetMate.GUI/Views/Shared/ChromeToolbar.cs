using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// A tab whose sub-tabs belong in the window's title bar, after Back and
/// Forward, as FleetMate for Mac puts each module's segment switch in the
/// toolbar's navigation group rather than in a row under a page heading.
/// </summary>
internal interface IChromeToolbar
{
    /// <summary>
    /// The tab's segment switch and the controls that sit with it. Declared in
    /// the tab's own markup, so its handlers and names stay the tab's; the
    /// window moves it into the title bar while the tab shows.
    /// </summary>
    FrameworkElement? ChromeToolbar { get; }
}

/// <summary>Reading and choosing the segment showing in a tab's title-bar toolbar.</summary>
internal static class ChromeSections
{
    /// <summary>The name of the segment checked in <paramref name="toolbar"/>, or null when it has none.</summary>
    internal static string? Current(FrameworkElement? toolbar) =>
        Segments(toolbar).FirstOrDefault(r => r.IsChecked == true) is { } checkedSegment ? NameOf(checkedSegment) : null;

    /// <summary>Check the segment named <paramref name="section"/>; false when it is not there to check.</summary>
    internal static bool Show(FrameworkElement? toolbar, string section)
    {
        if (Segments(toolbar).FirstOrDefault(r => NameOf(r) == section) is not { IsEnabled: true } segment
            || segment.Visibility != Visibility.Visible)
            return false;
        segment.IsChecked = true;
        return true;
    }

    internal static string NameOf(RadioButton segment) =>
        segment.Content as string is { Length: > 0 } text ? text
        : AutomationProperties.GetName(segment) is { Length: > 0 } name ? name
        : segment.Name;

    private static IEnumerable<RadioButton> Segments(DependencyObject? root)
    {
        if (root == null) yield break;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is RadioButton radio) yield return radio;
            else foreach (var nested in Segments(child)) yield return nested;
        }
    }
}
