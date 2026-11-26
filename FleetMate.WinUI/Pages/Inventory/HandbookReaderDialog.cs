using FleetMate.Core.Knowledge;
using FleetMate.WinUI.Pages.Development;
using FleetMate.WinUI.Pages.Development.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FleetMate.WinUI.Pages.Inventory;

/// <summary>
/// A Handbook page read inside FleetMate, as on macOS: its Markdown in a
/// dialog, with the published page a button away when the site is
/// configured. Stands in for FleetMate.GUI's reader window until the
/// Knowledge tab is ported.
/// </summary>
internal static class HandbookReaderDialog
{
    public static async Task ShowAsync(HandbookPage page, XamlRoot root)
    {
        var store = App.Services.Handbook;
        var full = store is { IsConfigured: true } ? store.FullPage(page) : page;
        var site = store?.SiteUrl(full);

        var view = new MarkdownView();
        view.Show(full.Body);
        var content = new ScrollViewer
        {
            MaxHeight = 560,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = full.Breadcrumb, Style = InventoryUi.Style("CaptionTextBlockStyle"),
                        Foreground = InventoryUi.Secondary, TextWrapping = TextWrapping.Wrap,
                    },
                    view,
                },
            },
        };
        var dialog = new ContentDialog
        {
            Title = full.Title,
            Content = content,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root,
        };
        if (site != null) dialog.PrimaryButtonText = "Open in Browser";
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && site != null) DevelopmentUi.OpenUrl(site.AbsoluteUri);
    }
}
