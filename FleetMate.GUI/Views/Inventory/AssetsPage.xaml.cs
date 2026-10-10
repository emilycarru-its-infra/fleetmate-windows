using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Services.Inventory;
using FleetMate.GUI.Views.Shared.Widgets;

namespace FleetMate.GUI.Views.Inventory;

public partial class AssetsPage : Page
{
    private readonly FleetMateConfig _config;
    private SnipeService? _snipeService;
    private List<SnipeAsset> _allAssets = new();
    private SnipeAsset? _selectedAsset;
    private bool _isInitialLoadDone;
    private bool _isLoading;
    /// <summary>When a deep link last opened an asset. The click that picked
    /// the link can land on this list as it appears and select the wrong row,
    /// so row clicks are ignored briefly after.</summary>
    private DateTime _deepLinkOpenedAt = DateTime.MinValue;
    private static readonly TimeSpan DeepLinkClickGuard = TimeSpan.FromMilliseconds(500);

    // Default sort: most recently touched assets first (macOS parity); any
    // column header click re-sorts by that column, clicking again flips it.
    private string _sortField = "Recent";
    private bool _sortDescending = true;

    public AssetsPage()
    {
        InitializeComponent();
        InitColumns();
        AssetListView.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
            new RoutedEventHandler(OnColumnHeaderClicked));
        _config = Application.Current is App currentApp ? currentApp.Config : FleetMateConfig.Load();

        if (Application.Current is App app && app.SnipeService != null)
        {
            _snipeService = app.SnipeService;
        }

        DetailPanel.CloseRequested += (_, _) =>
        {
            AssetListView.SelectedItem = null;
            _selectedAsset = null;
            DetailHost.Visibility = Visibility.Collapsed;
        };
        DetailPanel.ReAllocateRequested += async (_, _) => await ReAllocateSelectedAsync();
        DetailPanel.Changed += async (_, _) => await LoadAssetsAsync();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        // The page is cached across tab switches, so a deep link is checked on
        // every visit, not just the first.
        if (_isInitialLoadDone)
        {
            await ApplyPendingAssetLinkAsync();
            return;
        }
        _isInitialLoadDone = true;

        if (_snipeService == null || !_snipeService.IsConfigured)
        {
            NotConfiguredText.Visibility = Visibility.Visible;
            return;
        }

        await LoadAssetsAsync();
    }

    /// <summary>
    /// Open the asset a Dashboard row linked to: clear search and filters so
    /// it can't be hidden, then select and scroll to it. While the list is
    /// still loading the link waits for the load to finish; an asset missing
    /// from the list is fetched by id.
    /// </summary>
    private async Task ApplyPendingAssetLinkAsync()
    {
        if (_isLoading || _snipeService == null) return;
        if ((Application.Current as App)?.PendingNavigateAssetId is not { } assetId) return;
        ((App)Application.Current).PendingNavigateAssetId = null;

        var asset = _allAssets.FirstOrDefault(a => a.Id == assetId);
        if (asset == null)
        {
            asset = await _snipeService.GetAssetAsync(assetId);
            if (asset == null) return;
            _allAssets.Add(asset);
            UpdateFilterOptions();
        }

        SearchBox.Text = "";
        ClearFilters();

        var row = (AssetListView.ItemsSource as IEnumerable<SnipeAsset>)?.FirstOrDefault(a => a.Id == assetId);
        if (row == null) return;
        _deepLinkOpenedAt = DateTime.UtcNow;
        _selectedAsset = row; // so the guard below lets this selection through
        AssetListView.SelectedItem = row;
        AssetListView.ScrollIntoView(row);
    }

    private async Task LoadAssetsAsync()
    {
        if (_snipeService == null) return;

        try
        {
            _isLoading = true;
            LoadingOverlay.Visibility = Visibility.Visible;
            _allAssets = await _snipeService.GetAssetsAsync(forceRefresh: true);
            NotConfiguredText.Visibility = Visibility.Collapsed;
            UpdateFilterOptions();
            UpdateDisplay();

            // Keep the open detail in sync with the fresh row.
            if (_selectedAsset != null)
            {
                var fresh = _allAssets.FirstOrDefault(a => a.Id == _selectedAsset.Id);
                if (fresh != null)
                {
                    _selectedAsset = fresh;
                    DetailPanel.Show(fresh);
                }
            }
        }
        catch (Exception ex)
        {
            // Said in the page, where the empty list would otherwise read as
            // an inventory with nothing in it.
            var reason = ex is SnipeException ? ex.Message : FleetMate.Core.Services.Inventory.SnipeService.Describe(ex);
            _allAssets = new List<SnipeAsset>();
            AssetListView.ItemsSource = _allAssets;
            AssetCountLabel.Text = WidgetCatalog.AssetsFailureHeadline(reason);
            NotConfiguredText.Text = reason;
            NotConfiguredText.Visibility = Visibility.Visible;
            if (Application.Current is App app) app.SetAssetsLoadError(reason);
        }
        finally
        {
            _isLoading = false;
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }

        await ApplyPendingAssetLinkAsync();
    }

    private void UpdateDisplay()
    {
        var searchText = SearchBox.Text?.Trim().ToLowerInvariant() ?? "";
        var filtered = _allAssets.AsEnumerable();

        if (!string.IsNullOrEmpty(searchText))
        {
            filtered = filtered.Where(a =>
                (a.DisplayName?.ToLowerInvariant().Contains(searchText) ?? false) ||
                (a.AssetTag?.ToLowerInvariant().Contains(searchText) ?? false) ||
                (a.Serial?.ToLowerInvariant().Contains(searchText) ?? false) ||
                (a.AssignedTo?.Name?.ToLowerInvariant().Contains(searchText) ?? false));
        }

        // Apply filters
        // Status holds the individual status names and Status Type the types
        // they share, as on the Mac; the Asset Status widget's wedges set
        // Status Type, so they select every asset of their type.
        var statusFilter = StatusFilterComboBox.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(statusFilter) && statusFilter != "All")
            filtered = filtered.Where(a => WidgetCatalog.AssetStatusNameFilterValue(a) == statusFilter);

        var statusTypeFilter = StatusTypeFilterComboBox.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(statusTypeFilter) && statusTypeFilter != "All")
            filtered = filtered.Where(a => WidgetCatalog.AssetStatusTypeFilterValue(a) == statusTypeFilter);

        var categoryFilter = CategoryFilterComboBox.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(categoryFilter) && categoryFilter != "All")
            filtered = filtered.Where(a => a.Category?.Name == categoryFilter);

        var platformFilter = PlatformFilterComboBox.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(platformFilter) && platformFilter != "All")
            filtered = filtered.Where(a => a.Platform == platformFilter);

        var manufacturerFilter = ManufacturerFilterComboBox.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(manufacturerFilter) && manufacturerFilter != "All")
            filtered = filtered.Where(a => a.Manufacturer?.Name == manufacturerFilter);

        var modelFilter = ModelFilterComboBox.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(modelFilter) && modelFilter != "All")
            filtered = filtered.Where(a => a.Model?.Name == modelFilter);

        var usageFilter = UsageFilterComboBox.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(usageFilter) && usageFilter != "All")
            filtered = filtered.Where(a => a.Usage == usageFilter);

        var catalogFilter = CatalogFilterComboBox.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(catalogFilter) && catalogFilter != "All")
            filtered = filtered.Where(a => a.Catalog == catalogFilter);

        var areaFilter = AreaFilterComboBox.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(areaFilter) && areaFilter != "All")
            filtered = filtered.Where(a => a.Area == areaFilter);

        var locationFilter = LocationFilterComboBox.SelectedItem?.ToString();
        filtered = filtered.Where(a => AssetLocationFilter.Matches(a, locationFilter));

        var list = ApplySort(filtered).ToList();
        AssetListView.ItemsSource = list;
        AssetCountLabel.Text = $"{list.Count} assets";
    }

    private IEnumerable<SnipeAsset> ApplySort(IEnumerable<SnipeAsset> assets)
    {
        // Snipe timestamps are "yyyy-MM-dd HH:mm:ss", which sorts correctly
        // as text — both date-backed sorts compare the raw string.
        Func<SnipeAsset, string> key = _sortField switch
        {
            "Asset Tag" => a => a.AssetTag ?? "",
            "Name" => a => a.DisplayName ?? "",
            "Serial" => a => a.Serial ?? "",
            "Model" => a => a.Model?.Name ?? "",
            "Status" => a => a.StatusLabel?.Name ?? "",
            "Assigned To" => a => a.AssignedTo?.Name ?? "",
            "Category" => a => a.Category?.Name ?? "",
            "Manufacturer" => a => a.Manufacturer?.Name ?? "",
            "Platform" => a => a.Platform ?? "",
            "Usage" => a => a.Usage ?? "",
            "Catalog" => a => a.Catalog ?? "",
            "Area" => a => a.Area ?? "",
            "Location" => a => a.LocationName ?? "",
            "Last Activity" => a => a.LastActivity?.DateTime ?? "",
            _ => a => a.LastActivity?.DateTime ?? ""
        };
        return _sortDescending
            ? assets.OrderByDescending(key, StringComparer.OrdinalIgnoreCase)
            : assets.OrderBy(key, StringComparer.OrdinalIgnoreCase);
    }

    private void OnColumnHeaderClicked(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header ||
            header.Column?.Header is not string field || string.IsNullOrEmpty(field))
            return;

        if (_sortField == field)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortField = field;
            // Date columns sort newest-first on first click.
            _sortDescending = field == "Last Activity";
        }
        UpdateDisplay();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateDisplay();

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => UpdateDisplay();

    private void OnClearFiltersClicked(object sender, RoutedEventArgs e) => ClearFilters();

    private void ClearFilters()
    {
        StatusFilterComboBox.SelectedIndex = 0;
        StatusTypeFilterComboBox.SelectedIndex = 0;
        CategoryFilterComboBox.SelectedIndex = 0;
        PlatformFilterComboBox.SelectedIndex = 0;
        ManufacturerFilterComboBox.SelectedIndex = 0;
        ModelFilterComboBox.SelectedIndex = 0;
        UsageFilterComboBox.SelectedIndex = 0;
        CatalogFilterComboBox.SelectedIndex = 0;
        AreaFilterComboBox.SelectedIndex = 0;
        LocationFilterComboBox.SelectedIndex = 0;
        UpdateDisplay();
    }

    private void UpdateFilterOptions()
    {
        var statuses = new HashSet<string> { "All" };
        var statusTypes = new HashSet<string> { "All" };
        var categories = new HashSet<string> { "All" };
        var platforms = new HashSet<string> { "All" };
        var manufacturers = new HashSet<string> { "All" };
        var models = new HashSet<string> { "All" };
        var usages = new HashSet<string> { "All" };
        var catalogs = new HashSet<string> { "All" };
        var areas = new HashSet<string> { "All" };

        foreach (var asset in _allAssets)
        {
            if (WidgetCatalog.AssetStatusNameFilterValue(asset) is { } statusName) statuses.Add(statusName);
            statusTypes.Add(WidgetCatalog.AssetStatusTypeFilterValue(asset));
            if (!string.IsNullOrEmpty(asset.Category?.Name)) categories.Add(asset.Category.Name);
            if (!string.IsNullOrEmpty(asset.Manufacturer?.Name)) manufacturers.Add(asset.Manufacturer.Name);
            if (!string.IsNullOrEmpty(asset.Model?.Name)) models.Add(asset.Model.Name);
            if (!string.IsNullOrEmpty(asset.Platform)) platforms.Add(asset.Platform!);
            if (!string.IsNullOrEmpty(asset.Usage)) usages.Add(asset.Usage!);
            if (!string.IsNullOrEmpty(asset.Catalog)) catalogs.Add(asset.Catalog!);
            if (!string.IsNullOrEmpty(asset.Area)) areas.Add(asset.Area!);
        }

        SetFilterItems(StatusFilterComboBox, statuses);
        SetFilterItems(StatusTypeFilterComboBox, statusTypes);
        SetFilterItems(CategoryFilterComboBox, categories);
        SetFilterItems(PlatformFilterComboBox, platforms);
        SetFilterItems(ManufacturerFilterComboBox, manufacturers);
        SetFilterItems(ModelFilterComboBox, models);
        SetFilterItems(UsageFilterComboBox, usages);
        SetFilterItems(CatalogFilterComboBox, catalogs);
        SetFilterItems(AreaFilterComboBox, areas);
        LocationFilterComboBox.ItemsSource = AssetLocationFilter.Options(_allAssets);
        LocationFilterComboBox.SelectedIndex = 0;
    }

    private static void SetFilterItems(ComboBox comboBox, HashSet<string> items)
    {
        comboBox.ItemsSource = items.OrderBy(s => s == "All" ? "" : s).ToList();
        comboBox.SelectedIndex = 0;
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        await LoadAssetsAsync();
    }

    // MARK: - Selection & Detail Sidebar

    private void AssetListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        FleetMate.GUI.Views.Terminal.ContextPublisher.Asset(AssetListView.SelectedItem as SnipeAsset);
        if (AssetListView.SelectedItem is SnipeAsset asset)
        {
            // A stray click right after a deep link: put the linked asset back.
            if (_selectedAsset != null && asset.Id != _selectedAsset.Id &&
                DateTime.UtcNow - _deepLinkOpenedAt < DeepLinkClickGuard)
            {
                var linked = _selectedAsset;
                Dispatcher.BeginInvoke(() => AssetListView.SelectedItem = linked);
                return;
            }
            _selectedAsset = asset;
            DetailHost.Visibility = Visibility.Visible;
            DetailPlaceholder.Visibility = Visibility.Collapsed;
            DetailPanel.Show(asset);
        }
    }

    // MARK: - Re-Allocate

    private async Task ReAllocateSelectedAsync()
    {
        if (_selectedAsset == null || _snipeService == null) return;

        var dialog = new ReAllocateDialog(_snipeService, _selectedAsset);
        var result = dialog.ShowDialog();

        if (result == true && dialog.SelectedUserId.HasValue)
        {
            try
            {
                LoadingOverlay.Visibility = Visibility.Visible;

                // Step 1: check in only when the asset is currently assigned.
                if (_selectedAsset.AssignedTo != null)
                {
                    var checkinResult = await _snipeService.CheckinAssetAsync(_selectedAsset.Id,
                        new SnipeCheckinRequest { Note = "Re-allocated via FleetMate" });

                    if (checkinResult == null || !checkinResult.IsSuccess)
                    {
                        MessageBox.Show($"Check-in failed: {checkinResult?.Messages ?? "Unknown error"}",
                            "Re-Allocate", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }

                // Step 2: check out to the new user.
                var checkoutResult = await _snipeService.CheckoutAssetAsync(_selectedAsset.Id,
                    new SnipeCheckoutRequest
                    {
                        AssignedUser = dialog.SelectedUserId.Value,
                        CheckoutToType = "user",
                        Note = dialog.Note ?? "Re-allocated via FleetMate"
                    });

                if (checkoutResult != null && checkoutResult.IsSuccess)
                {
                    await LoadAssetsAsync();
                }
                else
                {
                    MessageBox.Show($"Checkout failed: {checkoutResult?.Messages ?? "Unknown error"}",
                        "Re-Allocate", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Re-allocate failed: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }
    }
}

/// <summary>
/// Dialog for selecting a user to re-allocate an asset to
/// </summary>
public class ReAllocateDialog : Window
{
    private readonly SnipeService _snipeService;
    private readonly SnipeAsset _asset;
    private TextBox _searchBox = null!;
    private ListView _userList = null!;
    private TextBox _noteBox = null!;
    private Button _confirmButton = null!;

    public int? SelectedUserId { get; private set; }
    public string? Note { get; private set; }

    public ReAllocateDialog(SnipeService snipeService, SnipeAsset asset)
    {
        _snipeService = snipeService;
        _asset = asset;

        Title = $"Re-Allocate: {asset.DisplayName}";
        Width = 500;
        Height = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current.MainWindow;

        BuildUI();
    }

    private void BuildUI()
    {
        var root = new StackPanel { Margin = new Thickness(16) };

        root.Children.Add(new TextBlock
        {
            Text = $"Select a new user for {_asset.DisplayName} ({_asset.AssetTag})",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap
        });

        // Search
        var searchPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        _searchBox = new TextBox { Width = 350 };
        _searchBox.SetValue(ModernWpf.Controls.Primitives.ControlHelper.PlaceholderTextProperty, "Search users...");
        _searchBox.KeyDown += async (s, e) => { if (e.Key == Key.Enter) await SearchUsersAsync(); };
        var searchBtn = new Button { Content = "Search", Margin = new Thickness(8, 0, 0, 0) };
        searchBtn.Click += async (s, e) => await SearchUsersAsync();
        searchPanel.Children.Add(_searchBox);
        searchPanel.Children.Add(searchBtn);
        root.Children.Add(searchPanel);

        // User list
        _userList = new ListView { Height = 250, SelectionMode = SelectionMode.Single };
        _userList.SelectionChanged += (s, e) =>
        {
            _confirmButton.IsEnabled = _userList.SelectedItem is SnipeUser;
        };
        root.Children.Add(_userList);

        // Note
        root.Children.Add(new TextBlock
        {
            Text = "Note (optional):",
            Margin = new Thickness(0, 12, 0, 4),
            FontSize = 12
        });
        _noteBox = new TextBox { Height = 60, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        root.Children.Add(_noteBox);

        // Buttons
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancelBtn = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0) };
        cancelBtn.Click += (s, e) => { DialogResult = false; Close(); };
        _confirmButton = new Button { Content = "Re-Allocate", IsEnabled = false };
        _confirmButton.Click += (s, e) =>
        {
            if (_userList.SelectedItem is SnipeUser user)
            {
                SelectedUserId = user.Id;
                Note = string.IsNullOrWhiteSpace(_noteBox.Text) ? null : _noteBox.Text.Trim();
                DialogResult = true;
                Close();
            }
        };
        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(_confirmButton);
        root.Children.Add(btnPanel);

        Content = root;
    }

    private async Task SearchUsersAsync()
    {
        var query = _searchBox.Text?.Trim();
        if (string.IsNullOrEmpty(query)) return;

        try
        {
            var users = await _snipeService.GetUsersAsync(search: query);
            _userList.ItemsSource = users;
            _userList.DisplayMemberPath = "Name";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"User search failed: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
