using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Serilog;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// The toolbar search field, last in the toolbar on every tab and the only
/// search field a tab shows. In tab scope it filters the tab's list through the
/// filter box the tab registers (<see cref="ITabSearch"/>); in All scope it
/// searches everything and drops hits beneath it, grouped by category. Ctrl+F
/// filters the tab, Ctrl+K searches everything, the chip switches, Esc clears.
/// </summary>
public partial class MainWindow
{
    private CancellationTokenSource? _searchCts;
    private ToolbarSearchResult? _firstHit;
    private ToolbarSearchMode _searchMode = ToolbarSearchMode.All;
    private ITabSearch? _searchPage;
    private TabSearchScope? _tabScope;
    private string _searchTabName = "";
    private bool _syncingSearch;

    /// <summary>Ctrl+K searches everything; Ctrl+F filters the tab.</summary>
    private bool HandleSearchShortcut(Key key, ModifierKeys mods)
    {
        if (mods != ModifierKeys.Control) return false;
        if (key == Key.K) SetSearchMode(ToolbarSearchMode.All);
        else if (key == Key.F) SetSearchMode(ToolbarSearchScopes.ForFind(_tabScope != null));
        else return false;

        SearchBox.Focus();
        SearchBox.SelectAll();
        return true;
    }

    /// <summary>
    /// Point the field at the tab now showing. Switching tab resets it to the
    /// tab's own filter, showing whatever that filter already holds.
    /// </summary>
    private void AttachSearchScope(Page page, string tabName)
    {
        if (_searchPage != null) _searchPage.SearchScopeChanged -= OnTabSearchScopeChanged;
        _searchPage = page as ITabSearch;
        if (_searchPage != null) _searchPage.SearchScopeChanged += OnTabSearchScopeChanged;
        _searchTabName = tabName;

        BindTabScope(_searchPage?.SearchScope);
        _searchMode = ToolbarSearchScopes.ForTab(_tabScope != null);
        ShowSearchMode(_searchMode == ToolbarSearchMode.Tab ? _tabScope!.Box.Text : "");
    }

    /// <summary>The tab switched segment or view: follow its filter, staying in tab scope.</summary>
    private void OnTabSearchScopeChanged(object? sender, EventArgs e)
    {
        BindTabScope(_searchPage?.SearchScope);
        if (_tabScope == null) _searchMode = ToolbarSearchMode.All;
        ShowSearchMode(_searchMode == ToolbarSearchMode.Tab ? _tabScope!.Box.Text : SearchBox.Text);
    }

    private void BindTabScope(TabSearchScope? scope)
    {
        if (_tabScope != null) _tabScope.Box.TextChanged -= OnTabFilterTextChanged;
        _tabScope = scope;
        if (_tabScope != null) _tabScope.Box.TextChanged += OnTabFilterTextChanged;
    }

    /// <summary>The tab changed its own filter (cleared it, followed a link): show that.</summary>
    private void OnTabFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingSearch || _searchMode != ToolbarSearchMode.Tab || _tabScope == null) return;
        if (SearchBox.Text != _tabScope.Box.Text) SetSearchText(_tabScope.Box.Text);
    }

    private void OnSearchScopeChipClicked(object sender, RoutedEventArgs e)
    {
        SetSearchMode(ToolbarSearchScopes.Toggle(_searchMode, _tabScope != null));
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    /// <summary>Keep the typed text clear of the chip, whatever its label.</summary>
    private void OnSearchScopeChipSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var padding = SearchBox.Padding;
        SearchBox.Padding = new Thickness(SearchScopeChip.Margin.Left + e.NewSize.Width + 6, padding.Top, padding.Right, padding.Bottom);
    }

    /// <summary>
    /// Switch scope, carrying the typed text across: into the tab's filter when
    /// moving to tab scope, out of it (leaving the list unfiltered) when moving
    /// to everything, where it runs as a search.
    /// </summary>
    private void SetSearchMode(ToolbarSearchMode mode)
    {
        if (mode == ToolbarSearchMode.Tab && _tabScope == null) mode = ToolbarSearchMode.All;
        if (mode == _searchMode)
        {
            ShowSearchMode(SearchBox.Text);
            return;
        }

        var text = SearchBox.Text;
        if (_searchMode == ToolbarSearchMode.Tab) WriteTabFilter("");
        _searchMode = mode;
        ShowSearchMode(text);
    }

    /// <summary>Label, prompt and hint for the current scope; then apply <paramref name="text"/> to it.</summary>
    private void ShowSearchMode(string text)
    {
        SearchScopeChip.Content = ToolbarSearchScopes.ChipLabel(_searchMode, _searchTabName);
        ModernWpf.Controls.Primitives.ControlHelper.SetPlaceholderText(SearchBox, ToolbarSearchScopes.Placeholder(_searchMode, _tabScope?.Prompt));
        SearchHintText.Text = _searchMode == ToolbarSearchMode.Tab ? "Ctrl+F" : "Ctrl+K";

        _searchCts?.Cancel();
        SearchPopup.IsOpen = false;
        _firstHit = null;

        if (SearchBox.Text != text) SetSearchText(text);
        else ApplySearchText();
    }

    private void SetSearchText(string text)
    {
        SearchBox.Text = text;
        SearchBox.CaretIndex = text.Length;
    }

    private void WriteTabFilter(string text)
    {
        if (_tabScope == null || _tabScope.Box.Text == text) return;
        _syncingSearch = true;
        try { _tabScope.Box.Text = text; }
        finally { _syncingSearch = false; }
    }

    /// <summary>Start loading ReportMate's devices as soon as a search begins.</summary>
    private void OnSearchFocused(object sender, KeyboardFocusChangedEventArgs e) =>
        _ = FleetMate.GUI.Views.Reporting.ReportingDeviceList.LoadAsync();

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => ApplySearchText();

    private async void ApplySearchText()
    {
        var query = SearchBox.Text.Trim();
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        _searchCts?.Cancel();

        // Tab scope: the tab's own filter does the work, as you type.
        if (_searchMode == ToolbarSearchMode.Tab && _tabScope != null)
        {
            SearchPopup.IsOpen = false;
            _firstHit = null;
            WriteTabFilter(SearchBox.Text);
            return;
        }

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

            // ReportMate's devices load with the first search; once they arrive,
            // search again so they join the results already showing.
            if (ToolbarSearch.Provider is { } again && FleetMate.GUI.Views.Reporting.ReportingDeviceList.NeedsLoad)
            {
                await FleetMate.GUI.Views.Reporting.ReportingDeviceList.LoadAsync();
                if (cts.IsCancellationRequested) return;
                RenderSearchResults(await again(query, cts.Token), provider: true);
            }
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
        if (e.Key == Key.Enter && _searchMode == ToolbarSearchMode.Tab && _tabScope != null)
        {
            // A filter that runs on Enter (Identity's user lookup) runs now.
            _tabScope.Submit?.Invoke();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _firstHit != null)
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
