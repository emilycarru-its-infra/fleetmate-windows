using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
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

        var filter = HistoryFilterBox.Text?.Trim().ToLowerInvariant() ?? "";
        var shown = string.IsNullOrEmpty(filter)
            ? _history
            : _history.Where(h => h.SearchText.Contains(filter)).ToList();

        HistoryCountText.Text = shown.Count == _history.Count
            ? $"{_history.Count} event{(_history.Count == 1 ? "" : "s")}"
            : $"{shown.Count} of {_history.Count} events";

        if (shown.Count == 0)
        {
            HistoryHost.Children.Add(new TextBlock
            {
                Text = _history.Count == 0 ? "No history recorded for this asset." : "No events match the filter.",
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
            Text = FormatHistoryDate(entry.Date),
            FontSize = 11,
            Foreground = Secondary(),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(when, Dock.Right);
        header.Children.Add(when);

        var title = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        title.Inlines.Add(new FontIconRun(ActionGlyph(entry.Action), Secondary()));
        title.Inlines.Add(new Run(Capitalize(entry.Action)) { FontWeight = FontWeights.SemiBold, FontSize = 12 });
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
        line.Inlines.Add(new FontIconRun("", Secondary()));
        if (ResolveFileUrl(file.Url) is { } uri)
        {
            var link = new Hyperlink(new Run(file.Name)) { NavigateUri = uri, ToolTip = uri.ToString() };
            link.RequestNavigate += (_, e) =>
            {
                try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                catch { }
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

    private Uri? ResolveFileUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp))
            return absolute;
        if (_service?.BaseUrl is { Length: > 0 } baseUrl &&
            Uri.TryCreate(new Uri(baseUrl.TrimEnd('/') + "/"), url.TrimStart('/'), out var relative))
            return relative;
        return null;
    }

    private static string ActionGlyph(string action)
    {
        var a = action.ToLowerInvariant();
        if (a.StartsWith("checkout")) return "";
        if (a.StartsWith("checkin")) return "";
        if (a.Contains("audit")) return "";
        if (a.Contains("upload")) return "";
        if (a.Contains("create")) return "";
        if (a.Contains("delete")) return "";
        if (a.Contains("restore")) return "";
        if (a.Contains("request")) return "";
        if (a.Contains("update")) return "";
        return "";
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string FormatHistoryDate(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        return DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt)
            ? dt.ToString("MMM d, yyyy h:mm tt")
            : raw;
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
