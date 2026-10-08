using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using FleetMate.Core.Knowledge;

namespace FleetMate.GUI.Views.Knowledge;

/// <summary>
/// The "Handbook" card: the pages about the thing on screen, found from the
/// words that describe it (macOS parity). Each opens in FleetMate's reader.
/// Null when the Handbook isn't configured or nothing relevant turns up, so
/// the card simply isn't there.
/// </summary>
public static class HandbookRelatedCard
{
    public static FrameworkElement? Build(IEnumerable<string> terms, int limit = 5)
    {
        var app = Application.Current as App;
        if (app?.Handbook is not { IsConfigured: true } store) return null;
        var pages = store.Index.Related(terms, limit);
        if (pages.Count == 0) return null;

        var panel = new StackPanel();
        var heading = new TextBlock { Text = "Handbook", FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        panel.Children.Add(heading);

        var list = new StackPanel();
        foreach (var page in pages)
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 3) };
            var link = new Hyperlink(new Run(page.Title)) { ToolTip = page.Path };
            link.Click += (_, _) => app.OpenHandbookPage(page);
            text.Inlines.Add(link);
            if (page.Breadcrumb.Length > 0)
            {
                var crumb = new Run("  " + page.Breadcrumb) { FontSize = 11 };
                crumb.SetResourceReference(TextElement.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
                text.Inlines.Add(crumb);
            }
            list.Children.Add(text);
        }
        var card = new Border { Child = list };
        card.SetResourceReference(FrameworkElement.StyleProperty, "CardStyle");
        panel.Children.Add(card);
        return panel;
    }
}
