using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Serilog;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// The toolbar search field, last in the toolbar on every tab: Ctrl+K focuses
/// it, typing searches, Enter opens the first hit, Esc clears. Hits drop down
/// beneath it, grouped by category.
/// </summary>
public partial class MainWindow
{
    private CancellationTokenSource? _searchCts;
    private ToolbarSearchResult? _firstHit;

    /// <summary>Ctrl+K from anywhere in the window.</summary>
    private bool HandleSearchShortcut(Key key, ModifierKeys mods)
    {
        if (key != Key.K || mods != ModifierKeys.Control) return false;
        SearchBox.Focus();
        SearchBox.SelectAll();
        return true;
    }

    private async void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        _searchCts?.Cancel();
        if (query.Length == 0)
        {
            SearchPopup.IsOpen = false;
            _firstHit = null;
            return;
        }

        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            // A short pause so a fast typist does not run a search per key.
            await Task.Delay(150, cts.Token);

            IReadOnlyList<ToolbarSearchResult> hits = ToolbarSearch.Provider is { } provider
                ? await provider(query, cts.Token)
                : Array.Empty<ToolbarSearchResult>();

            if (cts.IsCancellationRequested) return;
            RenderSearchResults(hits, provider: ToolbarSearch.Provider != null);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[search] query failed");
        }
    }

    private void RenderSearchResults(IReadOnlyList<ToolbarSearchResult> hits, bool provider)
    {
        SearchResults.Children.Clear();
        _firstHit = hits.FirstOrDefault();
        var medium = (Brush)FindResource("SystemControlForegroundBaseMediumBrush");

        if (hits.Count == 0)
        {
            SearchResults.Children.Add(new TextBlock
            {
                Text = provider ? "No matches." : "Search is not available yet.",
                Foreground = medium, Margin = new Thickness(4, 6, 4, 6),
            });
        }

        foreach (var (category, group) in ToolbarSearch.Group(hits))
        {
            SearchResults.Children.Add(new TextBlock
            {
                Text = category, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = medium,
                Margin = new Thickness(4, 10, 4, 2),
            });

            foreach (var hit in group)
            {
                var row = new StackPanel { Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent, Cursor = Cursors.Hand };
                var inner = new StackPanel { Margin = new Thickness(6, 3, 6, 3) };
                inner.Children.Add(new TextBlock { Text = hit.Title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
                if (!string.IsNullOrEmpty(hit.Detail))
                    inner.Children.Add(new TextBlock { Text = hit.Detail, FontSize = 10, Foreground = medium, TextTrimming = TextTrimming.CharacterEllipsis });
                var border = new Border { CornerRadius = new CornerRadius(4), Child = inner };
                border.MouseEnter += (_, _) => border.Background = (Brush)FindResource("SubtleFillBrush");
                border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
                row.Children.Add(border);
                row.MouseLeftButtonUp += (_, _) => OpenSearchHit(hit);
                SearchResults.Children.Add(row);
            }
        }

        SearchPopup.IsOpen = true;
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _firstHit != null)
        {
            OpenSearchHit(_firstHit);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ClearSearch();
            e.Handled = true;
        }
    }

    private void OpenSearchHit(ToolbarSearchResult hit)
    {
        ClearSearch();
        try { hit.Open(); }
        catch (Exception ex) { Log.Warning(ex, "[search] could not open {Title}", hit.Title); }
    }

    private void ClearSearch()
    {
        _searchCts?.Cancel();
        SearchBox.Text = "";
        SearchPopup.IsOpen = false;
        _firstHit = null;
        Keyboard.ClearFocus();
    }
}
