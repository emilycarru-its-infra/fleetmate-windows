using FleetMate.Core.Inventory;
using FleetMate.Core.Models.Inventory;
using FleetMate.WinUI.Pages.Development;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Serilog;

namespace FleetMate.WinUI.Pages.Inventory;

/// <summary>
/// The History segment, the macOS asset history: a card per event (icon by
/// action, action, who, target, when), a line per field change with the old
/// value struck through, then the note and any attached file.
/// </summary>
public sealed partial class AssetDetailPane
{
    /// <summary>Which segment shows; static so it carries across assets.</summary>
    private static bool _showHistory;

    private List<SnipeHistoryEntry> _history = new();
    private int? _historyAssetId;
    private TaskCompletionSource _historyLoaded = NewLoad(done: true);

    /// <summary>Completes when the History list has drawn, for the QA capture.</summary>
    internal Task HistoryLoaded => _historyLoaded.Task;

    private static TaskCompletionSource NewLoad(bool done)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (done) tcs.TrySetResult();
        return tcs;
    }

    /// <summary>Show History or Details.</summary>
    internal void ShowHistory(bool history)
    {
        _showHistory = history;
        ShowSegment();
    }

    private void SegmentBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _showHistory = sender.SelectedItem == HistoryItem;
        ShowSegment();
    }

    private void ShowSegment()
    {
        var item = _showHistory ? HistoryItem : DetailsItem;
        if (SegmentBar.SelectedItem != item) SegmentBar.SelectedItem = item;
        DetailsView.Visibility = DevelopmentUi.Show(!_showHistory);
        HistoryView.Visibility = DevelopmentUi.Show(_showHistory);
        if (_showHistory && _asset != null && _historyAssetId != _asset.Id) _ = LoadHistoryAsync(_asset);
    }

    private async Task LoadHistoryAsync(SnipeAsset asset)
    {
        _historyAssetId = asset.Id;
        _historyLoaded = NewLoad(done: false);
        _history = new();
        HistoryHost.Children.Clear();
        HistoryCountText.Text = "";
        HistoryLoadingRing.IsActive = true;
        try
        {
            var rows = await App.Services.GetAssetHistoryAsync(asset);
            // Another asset was opened while this one loaded: drop the stale reply.
            if (_asset?.Id != asset.Id) return;
            _history = rows;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[snipe] Asset history failed");
            if (_asset?.Id == asset.Id) ShowError($"History could not be loaded: {ex.Message}");
        }
        finally
        {
            if (_asset?.Id == asset.Id)
            {
                HistoryLoadingRing.IsActive = false;
                RenderHistory();
            }
            _historyLoaded.TrySetResult();
        }
    }

    private void HistoryFilterBox_TextChanged(object sender, TextChangedEventArgs e) => RenderHistory();

    private void RenderHistory()
    {
        HistoryHost.Children.Clear();
        var shown = AssetDetailLayout.FilterHistory(_history, HistoryFilterBox.Text);
        HistoryCountText.Text = AssetDetailLayout.HistoryCount(shown.Count, _history.Count);
        if (shown.Count == 0)
        {
            HistoryHost.Children.Add(new TextBlock
            {
                Text = AssetDetailLayout.HistoryEmpty(_history.Count),
                Style = InventoryUi.Style("BodyTextBlockStyle"),
                Foreground = InventoryUi.Secondary,
                Margin = new Thickness(0, 8, 0, 0),
            });
            return;
        }
        foreach (var entry in shown) HistoryHost.Children.Add(HistoryCard(entry));
    }

    private FrameworkElement HistoryCard(SnipeHistoryEntry entry)
    {
        var title = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Style = InventoryUi.Style("BodyTextBlockStyle") };
        title.Inlines.Add(new Run { Text = AssetDetailLayout.ActionTitle(entry.Action), FontWeight = FontWeights.SemiBold });
        if (entry.By != null) title.Inlines.Add(new Run { Text = $"  by {entry.By}", Foreground = InventoryUi.Secondary });
        if (entry.Target != null) title.Inlines.Add(new Run { Text = $"  → {entry.Target}" });

        var when = new TextBlock
        {
            Text = AssetDetailLayout.FormatHistoryDate(entry.Date),
            Style = InventoryUi.Style("CaptionTextBlockStyle"),
            Foreground = InventoryUi.Secondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var icon = new FontIcon { Glyph = AssetDetailLayout.ActionGlyph(entry.Action), FontSize = 14, Foreground = InventoryUi.Secondary, VerticalAlignment = VerticalAlignment.Center };
        var header = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new() { Width = GridLength.Auto }, new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } },
            Children = { icon, title, when },
        };
        Grid.SetColumn(title, 1);
        Grid.SetColumn(when, 2);

        var body = new StackPanel { Spacing = 4, Children = { header } };
        foreach (var change in entry.Changes) body.Children.Add(ChangeLine(change));
        if (entry.Note != null)
            body.Children.Add(new TextBlock
            {
                Text = entry.Note, TextWrapping = TextWrapping.Wrap, FontStyle = Windows.UI.Text.FontStyle.Italic,
                Foreground = InventoryUi.Secondary, Margin = new Thickness(22, 0, 0, 0), Style = InventoryUi.Style("BodyTextBlockStyle"),
                IsTextSelectionEnabled = true,
            });
        if (entry.File is { } file) body.Children.Add(FileLine(file));

        var card = new Border { Style = InventoryUi.Style("CardStyle"), Padding = new Thickness(12, 8, 12, 8), Child = body };
        AutomationProperties.SetName(card, $"{AssetDetailLayout.ActionTitle(entry.Action)}, {AssetDetailLayout.FormatHistoryDate(entry.Date)}");
        return card;
    }

    /// <summary>"Field: old → new", the old value struck through.</summary>
    private static TextBlock ChangeLine(SnipeFieldChange change)
    {
        var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 0, 0, 0), Style = InventoryUi.Style("BodyTextBlockStyle"), IsTextSelectionEnabled = true };
        line.Inlines.Add(new Run { Text = $"{change.Field}: ", Foreground = InventoryUi.Secondary });
        if (!string.IsNullOrEmpty(change.Old))
        {
            line.Inlines.Add(new Run { Text = change.Old, TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough, Foreground = InventoryUi.Secondary });
            line.Inlines.Add(new Run { Text = " → ", Foreground = InventoryUi.Secondary });
        }
        line.Inlines.Add(new Run { Text = string.IsNullOrEmpty(change.New) ? "—" : change.New });
        return line;
    }

    private static FrameworkElement FileLine(SnipeHistoryFile file)
    {
        var icon = new FontIcon { Glyph = AssetDetailLayout.AttachmentGlyph, FontSize = 12, Foreground = InventoryUi.Secondary, VerticalAlignment = VerticalAlignment.Center };
        FrameworkElement name;
        if (AssetDetailLayout.ResolveFileUrl(file.Url, App.Services.SnipeWebUrl) is { } uri)
        {
            var link = new HyperlinkButton { Content = file.Name, Padding = new Thickness(4, 0, 4, 0) };
            link.Click += (_, _) => DevelopmentUi.OpenUrl(uri.AbsoluteUri);
            ToolTipService.SetToolTip(link, uri.ToString());
            AutomationProperties.SetName(link, $"Open {file.Name}");
            name = link;
        }
        else
        {
            name = new TextBlock { Text = file.Name, Style = InventoryUi.Style("BodyTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center };
        }
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(22, 0, 0, 0), Children = { icon, name } };
    }
}
