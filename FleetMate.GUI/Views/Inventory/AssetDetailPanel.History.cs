using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using FleetMate.Core.Inventory;
using FleetMate.Core.Models.Inventory;
using ModernWpf.Controls;

namespace FleetMate.GUI.Views.Inventory;

/// <summary>
/// The History segment — the macOS asset history, ported. One card per event
/// (icon by action, action, who, target, when), a line per field change with
/// the old value struck through, notes and attached files under it.
/// </summary>
public partial class AssetDetailPanel
{
    /// <summary>Which segment is showing; static so it carries across assets.</summary>
    private static bool _showHistory;

    private List<SnipeHistoryEntry> _history = new();
    private int? _historyAssetId;

    private void OnSegmentChanged(object sender, RoutedEventArgs e)
    {
        // Fires once from InitializeComponent, before the other segment exists.
        if (HistorySegment == null || DetailsView == null) return;
        _showHistory = HistorySegment.IsChecked == true;
        ShowSegment();
    }

    private void ShowSegment()
    {
        if (HistorySegment.IsChecked != _showHistory)
            (_showHistory ? HistorySegment : DetailsSegment).IsChecked = true;

        DetailsView.Visibility = _showHistory ? Visibility.Collapsed : Visibility.Visible;
        HistoryView.Visibility = _showHistory ? Visibility.Visible : Visibility.Collapsed;

        if (_showHistory && _asset != null && _historyAssetId != _asset.Id)
            _ = LoadHistoryAsync(_asset.Id);
    }

    private async Task LoadHistoryAsync(int assetId)
    {
        if (_service == null) return;

        _historyAssetId = assetId;
        _history = new();
        HistoryHost.Children.Clear();
        HistoryCountText.Text = "";
        HistoryLoadingRing.IsActive = true;
        HistoryLoadingRing.Visibility = Visibility.Visible;

        var rows = await _service.GetAssetHistoryAsync(assetId);

        // Another asset was opened while this one loaded — drop the stale reply.
        if (_asset?.Id != assetId) return;

        HistoryLoadingRing.IsActive = false;
        HistoryLoadingRing.Visibility = Visibility.Collapsed;
        _history = rows.Select(SnipeHistory.ToEntry).ToList();
        RenderHistory();
    }

    private void OnHistoryFilterChanged(object sender, TextChangedEventArgs e) => RenderHistory();

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
                FontSize = 12,
                Foreground = Secondary(),
                Margin = new Thickness(0, 8, 0, 0)
            });
            return;
        }

        foreach (var entry in shown)
            HistoryHost.Children.Add(HistoryCard(entry));
    }

    private Border HistoryCard(SnipeHistoryEntry entry)
    {
        var body = new StackPanel();

        var header = new DockPanel();
        var when = new TextBlock
        {
            Text = AssetDetailLayout.FormatHistoryDate(entry.Date),
            FontSize = 11,
            Foreground = Secondary(),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(when, Dock.Right);
        header.Children.Add(when);

        var title = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        title.Inlines.Add(new FontIconRun(AssetDetailLayout.ActionGlyph(entry.Action), Secondary()));
        title.Inlines.Add(new Run(AssetDetailLayout.ActionTitle(entry.Action)) { FontWeight = FontWeights.SemiBold, FontSize = 12 });
        if (entry.By != null)
            title.Inlines.Add(new Run($"  by {entry.By}") { FontSize = 12, Foreground = Secondary() });
        if (entry.Target != null)
            title.Inlines.Add(new Run($"  → {entry.Target}") { FontSize = 12 });
        header.Children.Add(title);
        body.Children.Add(header);

        foreach (var change in entry.Changes)
            body.Children.Add(ChangeLine(change));

        if (entry.Note != null)
        {
            body.Children.Add(new TextBlock
            {
                Text = entry.Note,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Secondary(),
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }

        if (entry.File is { } file)
            body.Children.Add(FileLine(file));

        return new Border
        {
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)FindResource("CardBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = body
        };
    }

    /// <summary>"Field: old → new", the old value struck through.</summary>
    private TextBlock ChangeLine(SnipeFieldChange change)
    {
        var line = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(18, 3, 0, 0) };
        line.Inlines.Add(new Run($"{change.Field}: ") { Foreground = Secondary() });
        if (!string.IsNullOrEmpty(change.Old))
        {
            line.Inlines.Add(new Run(change.Old) { TextDecorations = TextDecorations.Strikethrough, Foreground = Secondary() });
            line.Inlines.Add(new Run(" → ") { Foreground = Secondary() });
        }
        line.Inlines.Add(new Run(string.IsNullOrEmpty(change.New) ? "—" : change.New));
        return line;
    }

    private TextBlock FileLine(SnipeHistoryFile file)
    {
        var line = new TextBlock { FontSize = 12, Margin = new Thickness(18, 4, 0, 0) };
        line.Inlines.Add(new FontIconRun(AssetDetailLayout.AttachmentGlyph, Secondary()));
        if (AssetDetailLayout.ResolveFileUrl(file.Url, _service?.BaseUrl) is { } uri)
        {
            var link = new Hyperlink(new Run(file.Name)) { NavigateUri = uri, ToolTip = uri.ToString() };
            link.RequestNavigate += (_, e) =>
            {
                FleetMate.Core.Links.ExternalBrowser.Open(e.Uri.AbsoluteUri);
                e.Handled = true;
            };
            line.Inlines.Add(link);
        }
        else
        {
            line.Inlines.Add(new Run(file.Name));
        }
        return line;
    }

    /// <summary>A Segoe Fluent glyph inline in a TextBlock, ahead of its text.</summary>
    private sealed class FontIconRun : InlineUIContainer
    {
        public FontIconRun(string glyph, Brush foreground)
        {
            BaselineAlignment = BaselineAlignment.Center;
            Child = new FontIcon
            {
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                Glyph = glyph,
                FontSize = 12,
                Foreground = foreground,
                Margin = new Thickness(0, 0, 6, 0)
            };
        }
    }
}
