using FleetMate.Core.Inventory;
using FleetMate.Core.Models.Inventory;
using FleetMate.WinUI.Shell.Widgets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Serilog;

namespace FleetMate.WinUI.Pages.Inventory;

/// <summary>
/// The Inventory tab: every Snipe-IT asset in a sortable list with the
/// columns you choose, the Filters flyout, the graphs above it, and the
/// selected asset's detail beside it (60/40, as on Mac; below 760 DIPs the
/// detail takes the list's place). The behaviour is FleetMate.GUI's
/// AssetsPage; the search, filters and sort are FleetMate.Core's
/// <see cref="AssetListQuery"/>, through <see cref="InventoryViewModel"/>.
/// </summary>
public sealed partial class InventoryPage : Page, Shell.ICaptureViews
{
    private const string Tab = "Inventory";
    private const double NarrowWidth = 760;

    private bool _hooked;
    private bool _narrow;
    private SnipeAsset? _selected;

    /// <summary>When a link last opened an asset: a stray click on the list as it appears is ignored briefly after.</summary>
    private DateTime _linkOpenedAt = DateTime.MinValue;
    private static readonly TimeSpan LinkClickGuard = TimeSpan.FromMilliseconds(500);

    public InventoryPage()
    {
        ViewModel = new InventoryViewModel(persistColumns: !App.Services.DemoMode);
        InitializeComponent();
        BuildHeader();
        ViewModel.Columns.PropertyChanged += (_, _) => BuildHeader();
        MainWindow.SearchChanged += (_, text) => ViewModel.SetSearch(text);
        Detail.CloseRequested += (_, _) => CloseDetail();
        Detail.ReAllocateRequested += async (_, _) => await ReAllocateAsync();
        Detail.Changed += async (_, _) => await App.Services.LoadAssetsAsync(force: true);
        Loaded += OnLoaded;
        Unloaded += (_, _) => InventoryUi.PublishContext(null);
    }

    public InventoryViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var services = App.Services;
        if (!_hooked)
        {
            _hooked = true;
            services.CacheChanged += OnCacheChanged;
            services.AssetLinkRequested += () => _ = TakePendingLinkAsync();
            services.HandbookChanged += () => { if (_selected != null) Detail.Show(_selected); };
            WidgetStrip.Changed += tab => { if (tab == Tab) RenderGraphs(); };
            GraphsHost.ActualThemeChanged += (_, _) => RenderGraphs();
            Render();
            if (services.CachedAssets.Count == 0 && services.AssetsLoadError == null) _ = services.LoadAssetsAsync();
        }
        RenderGraphs();
        InventoryUi.PublishContext(_selected);
        _ = TakePendingLinkAsync();
    }

    private void OnCacheChanged(string key)
    {
        if (key != InventoryGraphCards.CacheKey) return;
        Render();
        RenderGraphs();
    }

    /// <summary>Show the cache: the list, the count, the loading and failure states, and the open detail kept current.</summary>
    private void Render()
    {
        var services = App.Services;
        var loading = services.IsCacheLoading(InventoryGraphCards.CacheKey);
        ViewModel.SetAssets(services.CachedAssets, services.AssetsLoadError);

        LoadingRing.IsActive = loading;
        RefreshButton.IsEnabled = !loading && services.IsInventoryConfigured;
        var firstLoad = loading && services.CachedAssets.Count == 0;
        FirstLoadRing.IsActive = firstLoad;
        FirstLoadRing.Visibility = firstLoad ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = !firstLoad && ViewModel.EmptyMessage != null ? Visibility.Visible : Visibility.Collapsed;

        if (!services.IsInventoryConfigured)
            ShowMessage(InfoBarSeverity.Informational, "Snipe-IT is not configured",
                "Add the Snipe-IT address in Settings. FleetMate signs in with an Entra token from the Windows broker.");
        else if (services.AssetsLoadError is { } error && services.CachedAssets.Count == 0)
            ShowMessage(InfoBarSeverity.Error, InventoryFigures.AssetsFailureHeadline(error), error);
        else
            MessageBar.IsOpen = false;

        // Keep the open detail on the fresh copy of its asset.
        if (_selected != null && services.CachedAssets.FirstOrDefault(a => a.Id == _selected.Id) is { } fresh && !ReferenceEquals(fresh, _selected))
        {
            _selected = fresh;
            Detail.Show(fresh);
        }
        SelectRow(_selected);
        _ = TakePendingLinkAsync();
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string message)
    {
        MessageBar.Severity = severity;
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.IsOpen = true;
    }

    // MARK: - Columns

    /// <summary>The header row: a sort button per visible column, matching the row cells' widths.</summary>
    private void BuildHeader()
    {
        HeaderRow.Children.Clear();
        foreach (var header in ViewModel.Columns.Visible)
        {
            var sorted = ViewModel.SortField == header;
            var label = new TextBlock
            {
                Text = header,
                Style = InventoryUi.Style("CaptionTextBlockStyle"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = sorted ? InventoryUi.Primary : InventoryUi.Secondary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { label } };
            if (sorted)
                content.Children.Add(new FontIcon
                {
                    Glyph = ViewModel.SortDescending ? "" : "", FontSize = 10, Foreground = InventoryUi.Primary,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            var button = new Button
            {
                Content = content,
                Width = AssetColumnLayout.Widths[header],
                Height = 36,
                Padding = new Thickness(0, 0, 12, 0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Style = InventoryUi.Style("SubtleButtonStyle"),
                CornerRadius = new CornerRadius(0),
            };
            var direction = !sorted ? "" : ViewModel.SortDescending ? ", sorted descending" : ", sorted ascending";
            AutomationProperties.SetName(button, $"{header}{direction}");
            AutomationProperties.SetAutomationId(button, $"InventoryColumn{header.Replace(" ", "")}");
            ToolTipService.SetToolTip(button, $"Sort by {header}");
            button.Click += (_, _) =>
            {
                ViewModel.SortBy(header);
                BuildHeader();
            };
            // A right-click on any header opens the Columns menu, as in WPF.
            button.ContextFlyout = BuildColumnsFlyout();
            HeaderRow.Children.Add(button);
        }
        SizeTable();
    }

    private MenuFlyout BuildColumnsFlyout()
    {
        var menu = new MenuFlyout();
        FillColumnsMenu(menu);
        return menu;
    }

    private void ColumnsMenu_Opening(object sender, object e) => FillColumnsMenu(ColumnsMenu);

    private void FillColumnsMenu(MenuFlyout menu)
    {
        menu.Items.Clear();
        foreach (var header in InventoryColumns.All)
        {
            var item = new ToggleMenuFlyoutItem { Text = header, IsChecked = ViewModel.Columns.IsVisible(header) };
            AutomationProperties.SetAutomationId(item, $"InventoryShowColumn{header.Replace(" ", "")}");
            item.Click += (_, _) =>
            {
                // The last column showing stays.
                if (!ViewModel.Columns.Set(header, item.IsChecked)) item.IsChecked = true;
            };
            menu.Items.Add(item);
        }
    }

    /// <summary>The table is as wide as its columns, and never narrower than the pane.</summary>
    private void SizeTable()
    {
        var width = Math.Max(ViewModel.Columns.RowWidth, ListScroller.ActualWidth);
        ListTable.Width = width;
    }

    // MARK: - Layout

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _narrow = e.NewSize.Width < NarrowWidth;
        GraphsScroller.MaxHeight = Math.Max(180, e.NewSize.Height * 0.4);
        ApplyLayout();
    }

    /// <summary>
    /// No selection: the list fills the tab. A selection: 60/40, or in a
    /// narrow window the detail alone, with Close returning to the list.
    /// </summary>
    private void ApplyLayout()
    {
        var open = _selected != null;
        ListPane.Visibility = open && _narrow ? Visibility.Collapsed : Visibility.Visible;
        DetailPane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        DetailPane.Margin = _narrow ? new Thickness(0) : new Thickness(12, 0, 0, 0);
        ListColumn.Width = open && _narrow ? new GridLength(0) : new GridLength(open ? 6 : 1, GridUnitType.Star);
        DetailColumn.Width = !open ? new GridLength(0) : new GridLength(_narrow ? 1 : 4, GridUnitType.Star);
        DispatcherQueue.TryEnqueue(SizeTable);
    }

    // MARK: - Selection and detail

    private void AssetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AssetList.SelectedItem is not AssetRow row) return;
        // A stray click just after a link opened an asset: keep the linked one.
        if (_selected != null && row.Asset.Id != _selected.Id && DateTime.UtcNow - _linkOpenedAt < LinkClickGuard)
        {
            SelectRow(_selected);
            return;
        }
        Open(row.Asset);
    }

    private void Open(SnipeAsset asset)
    {
        var changed = _selected?.Id != asset.Id;
        _selected = asset;
        if (changed) Detail.Show(asset);
        InventoryUi.PublishContext(asset);
        ApplyLayout();
    }

    private void SelectRow(SnipeAsset? asset)
    {
        var row = asset == null ? null : ViewModel.Rows.FirstOrDefault(r => r.Asset.Id == asset.Id);
        if (!ReferenceEquals(AssetList.SelectedItem, row)) AssetList.SelectedItem = row;
        if (row != null) AssetList.ScrollIntoView(row);
    }

    private void CloseDetail()
    {
        _selected = null;
        AssetList.SelectedItem = null;
        InventoryUi.PublishContext(null);
        ApplyLayout();
        AssetList.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Open the asset a link asked for: clear the search and filters so it
    /// can't be hidden, then select and scroll to it. While the list is still
    /// loading the link waits for it; an asset missing from it is fetched by id.
    /// </summary>
    private async Task TakePendingLinkAsync()
    {
        var services = App.Services;
        if (services.PendingAssetId is not { } id || !IsLoaded) return;
        if (services.IsCacheLoading(InventoryGraphCards.CacheKey) && services.CachedAssets.Count == 0) return;
        services.PendingAssetId = null;

        var asset = await services.FindAssetAsync(id);
        if (asset == null)
        {
            App.Current.Window?.ShowLinkBanner($"Inventory has no asset {id}.");
            return;
        }
        ClearSearchBox();
        ViewModel.ClearFilters();
        _linkOpenedAt = DateTime.UtcNow;
        Open(asset);
        SelectRow(asset);
    }

    private static void ClearSearchBox()
    {
        if (App.Current.Window?.Content is FrameworkElement root && root.FindName("SearchBox") is AutoSuggestBox box) box.Text = "";
    }

    // MARK: - Re-Allocate

    private async Task ReAllocateAsync()
    {
        if (_selected is not { } asset || App.Services.Snipe is not { } snipe) return;
        var dialog = new ReAllocateDialog(snipe, asset) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.SelectedUserId is not { } userId) return;

        LoadingRing.IsActive = true;
        try
        {
            if (await AssetReallocation.RunAsync(snipe, asset, userId, dialog.Note) is { } failure)
                ShowMessage(InfoBarSeverity.Error, "Re-Allocate", failure);
            else
                await App.Services.LoadAssetsAsync(force: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[snipe] Re-allocate failed");
            ShowMessage(InfoBarSeverity.Error, "Re-Allocate", $"Re-allocate failed: {ex.Message}");
        }
        finally
        {
            LoadingRing.IsActive = App.Services.IsCacheLoading(InventoryGraphCards.CacheKey);
        }
        MessageBar.IsClosable = true;
    }

    // MARK: - Toolbar

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await App.Services.LoadAssetsAsync(force: true);

    private async void RefreshAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await App.Services.LoadAssetsAsync(force: true);
    }

    private void ClearFiltersButton_Click(object sender, RoutedEventArgs e) => ViewModel.ClearFilters();

    private void ClearFiltersAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.ClearFilters();
    }

    // MARK: - Graphs

    /// <summary>
    /// The graphs, when the toolbar's Graphs toggle shows them. Brushes are
    /// read when the cards are drawn, so a theme change draws them again.
    /// </summary>
    private void RenderGraphs()
    {
        var services = App.Services;
        var shown = WidgetStrip.IsShown(Tab);
        GraphsHost.Content = shown
            ? WidgetStrip.Build(Tab,
                InventoryGraphCards.Build(services.CachedAssets, services.IsCacheLoading(InventoryGraphCards.CacheKey), services.AssetsLoadError),
                filter => ViewModel.ApplyGraphFilter(filter.Category, filter.Value))
            : null;
        GraphsScroller.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }

    // MARK: - QA capture

    /// <summary>
    /// An asset's details and its history, the list filtered from a graph,
    /// then the list as it opens again, so the next window size starts there.
    /// </summary>
    public IReadOnlyList<string> CaptureViews { get; } = new[] { "detail", "history", "filtered", "list" };

    public async Task ShowCaptureViewAsync(string view)
    {
        var first = ViewModel.Rows.FirstOrDefault(r => r.Asset.AssignedTo != null) ?? ViewModel.Rows.FirstOrDefault();
        switch (view)
        {
            case "detail":
                if (first != null) { Open(first.Asset); SelectRow(first.Asset); }
                Detail.ShowHistory(false);
                break;
            case "history":
                if (first != null) { Open(first.Asset); SelectRow(first.Asset); }
                Detail.ShowHistory(true);
                await Detail.HistoryLoaded;
                break;
            case "filtered":
                Detail.ShowHistory(false);
                CloseDetail();
                ViewModel.ApplyGraphFilter(InventoryGraphCategory.Category, "Laptops");
                break;
            default:
                ViewModel.ClearFilters();
                CloseDetail();
                break;
        }
    }
}
