using FleetMate.Core.Inventory;
using FleetMate.Core.Knowledge;
using FleetMate.Core.Models.Inventory;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FleetMate.WinUI.Pages.Inventory;

/// <summary>
/// The Handbook card (macOS parity): the Handbook pages about this asset,
/// found from the words that describe it and grouped by which of them
/// matched. Hidden when the Handbook isn't configured or nothing turns up.
/// A page opens in FleetMate's reader (<see cref="HandbookReaderDialog"/>).
/// </summary>
public sealed partial class AssetDetailPane
{
    private FrameworkElement? HandbookCard(SnipeAsset asset)
    {
        if (App.Services.HandbookIndex is not { } index) return null;
        var facets = HandbookAssetFacets.For(asset.Model?.Name, asset.Category?.Name, asset.Platform,
            asset.CustomFieldByName("Device Management Service"), asset.CustomFieldByName("Fleet"));
        var groups = index.RelatedByFacet(facets);
        if (groups.Count == 0) return null;

        var (card, host) = Card("Handbook", AssetDetailLayout.HandbookGlyph);
        foreach (var group in groups)
        {
            host.Children.Add(new TextBlock
            {
                Text = group.Label,
                Style = InventoryUi.Style("CaptionTextBlockStyle"),
                Foreground = InventoryUi.Secondary,
                Margin = new Thickness(0, 6, 0, 0),
            });
            foreach (var page in group.Pages) host.Children.Add(PageLink(page));
        }
        return card;
    }

    private FrameworkElement PageLink(HandbookPage page)
    {
        var link = new HyperlinkButton
        {
            Content = new TextBlock { Text = page.Title, TextWrapping = TextWrapping.Wrap },
            Padding = new Thickness(0, 2, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(link, $"Open {page.Title} in the Handbook");
        ToolTipService.SetToolTip(link, page.Path);
        link.Click += async (_, _) => await HandbookReaderDialog.ShowAsync(page, XamlRoot);
        var panel = new StackPanel { Children = { link } };
        if (page.Breadcrumb.Length > 0)
            panel.Children.Add(new TextBlock
            {
                Text = page.Breadcrumb, Style = InventoryUi.Style("CaptionTextBlockStyle"), Foreground = InventoryUi.Secondary,
                TextWrapping = TextWrapping.Wrap,
            });
        return panel;
    }
}
