using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using FleetMate.Core.Knowledge;
using FleetMate.Core.Models.Inventory;
using FleetMate.GUI.Knowledge;

namespace FleetMate.GUI.Views.Inventory;

/// <summary>
/// The Handbook card (macOS parity): the Handbook pages about this asset,
/// found from the words that describe it and grouped by which of them matched.
/// Hidden when the Handbook isn't configured or nothing relevant turns up.
/// </summary>
public partial class AssetDetailPanel
{
    private HandbookStore? _handbookSubscribed;

    private Border? HandbookCard(SnipeAsset asset)
    {
        var store = (Application.Current as App)?.Handbook;
        if (store is not { IsConfigured: true }) return null;
        SubscribeHandbook(store);

        var facets = HandbookAssetFacets.For(asset.Model?.Name, asset.Category?.Name, asset.Platform,
            asset.CustomFieldByName("Device Management Service"), asset.CustomFieldByName("Fleet"));
        var groups = store.Index.RelatedByFacet(facets);
        if (groups.Count == 0) return null;

        var card = Card("Handbook", "");
        var host = (StackPanel)card.Child!;
        foreach (var group in groups)
        {
            host.Children.Add(new TextBlock
            {
                Text = group.Label,
                FontSize = 11,
                Foreground = Secondary(),
                Margin = new Thickness(0, 6, 0, 2)
            });
            foreach (var page in group.Pages)
                host.Children.Add(PageLink(store, page));
        }
        return card;
    }

    private FrameworkElement PageLink(HandbookStore store, HandbookPage page)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 3) };
        // Pages open in FleetMate's reader, as on macOS; it offers the site from there.
        var link = new Hyperlink(new Run(page.Title)) { ToolTip = page.Path };
        link.Click += (_, _) => (Application.Current as App)?.OpenHandbookPage(page);
        text.Inlines.Add(link);
        if (page.Breadcrumb.Length > 0)
            text.Inlines.Add(new Run("  " + page.Breadcrumb) { FontSize = 11, Foreground = Secondary() });
        return text;
    }

    /// <summary>Redraw when the Handbook finishes loading, so the card appears without reselecting.</summary>
    private void SubscribeHandbook(HandbookStore store)
    {
        if (_handbookSubscribed == store) return;
        _handbookSubscribed = store;
        store.Changed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (_asset != null && IsLoaded) Render();
        });
    }
}
