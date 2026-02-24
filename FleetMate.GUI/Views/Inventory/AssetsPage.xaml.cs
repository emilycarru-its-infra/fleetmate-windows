using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Services.Tickets;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Reporting;

namespace FleetMate.GUI.Views.Inventory;

public partial class AssetsPage : Page
{
    private readonly FleetMateConfig _config;
    private SnipeService? _snipeService;
    private List<SnipeAsset> _allAssets = new();
    private SnipeAsset? _selectedAsset;
    private bool _isInitialLoadDone;
    private List<SnipeStatusLabelFull> _statusLabels = new();
    private bool _hasEdits;
    private bool _isLoadingDetail;
    private int? _editStatusId;

    private static readonly HashSet<string> HardwareFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Platform", "Chip", "CPU", "GPU", "NPU", "Memory", "Storage", "Display"
    };

    private static readonly HashSet<string> ManagementFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Micro ID", "Intune ID", "Object ID"
    };

    private static readonly HashSet<string> FinancialFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Invoice Number", "PO Number", "Lease Contract ID", "Lease Contract Name",
        "Lease End Date", "Ownership Type", "Purchase Cost", "Purchase Date"
    };

    private static readonly HashSet<string> HiddenFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Username"
    };

    public AssetsPage()
    {
        InitializeComponent();
        _config = FleetMateConfig.Load();

        if (Application.Current is App app && app.SnipeService != null)
        {
            _snipeService = app.SnipeService;
        }
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialLoadDone) return;
        _isInitialLoadDone = true;

        if (_snipeService == null || !_snipeService.IsConfigured)
        {
            NotConfiguredText.Visibility = Visibility.Visible;
            return;
        }

        await LoadAssetsAsync();
    }

    private async Task LoadAssetsAsync()
    {
        if (_snipeService == null) return;

        try
        {
            LoadingOverlay.Visibility = Visibility.Visible;
            _allAssets = await _snipeService.GetAssetsAsync(forceRefresh: true);
            UpdateFilterOptions();
            UpdateDisplay();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load assets: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
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
        var statusFilter = StatusFilterComboBox.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(statusFilter) && statusFilter != "All")
            filtered = filtered.Where(a => a.StatusLabel?.Name == statusFilter);

        var categoryFilter = CategoryFilterComboBox.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(categoryFilter) && categoryFilter != "All")
            filtered = filtered.Where(a => a.Category?.Name == categoryFilter);

        var platformFilter = PlatformFilterComboBox.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(platformFilter) && platformFilter != "All")
            filtered = filtered.Where(a => GetCustomFieldValue(a, "Platform") == platformFilter);

        var manufacturerFilter = ManufacturerFilterComboBox.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(manufacturerFilter) && manufacturerFilter != "All")
            filtered = filtered.Where(a => a.Manufacturer?.Name == manufacturerFilter);

        var modelFilter = ModelFilterComboBox.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(modelFilter) && modelFilter != "All")
            filtered = filtered.Where(a => a.Model?.Name == modelFilter);

        var usageFilter = UsageFilterComboBox.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(usageFilter) && usageFilter != "All")
            filtered = filtered.Where(a => GetCustomFieldValue(a, "Usage") == usageFilter);

        var catalogFilter = CatalogFilterComboBox.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(catalogFilter) && catalogFilter != "All")
            filtered = filtered.Where(a => GetCustomFieldValue(a, "Catalog") == catalogFilter);

        var areaFilter = AreaFilterComboBox.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(areaFilter) && areaFilter != "All")
            filtered = filtered.Where(a => GetCustomFieldValue(a, "Area") == areaFilter);

        var list = filtered.ToList();
        AssetListView.ItemsSource = list;
        AssetCountLabel.Text = $"{list.Count} assets";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateDisplay();
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDisplay();
    }

    private void OnClearFiltersClicked(object sender, RoutedEventArgs e)
    {
        StatusFilterComboBox.SelectedIndex = 0;
        CategoryFilterComboBox.SelectedIndex = 0;
        PlatformFilterComboBox.SelectedIndex = 0;
        ManufacturerFilterComboBox.SelectedIndex = 0;
        ModelFilterComboBox.SelectedIndex = 0;
        UsageFilterComboBox.SelectedIndex = 0;
        CatalogFilterComboBox.SelectedIndex = 0;
        AreaFilterComboBox.SelectedIndex = 0;
        UpdateDisplay();
    }

    private void UpdateFilterOptions()
    {
        var statuses = new HashSet<string> { "All" };
        var categories = new HashSet<string> { "All" };
        var platforms = new HashSet<string> { "All" };
        var manufacturers = new HashSet<string> { "All" };
        var models = new HashSet<string> { "All" };
        var usages = new HashSet<string> { "All" };
        var catalogs = new HashSet<string> { "All" };
        var areas = new HashSet<string> { "All" };

        foreach (var asset in _allAssets)
        {
            if (!string.IsNullOrEmpty(asset.StatusLabel?.Name)) statuses.Add(asset.StatusLabel.Name);
            if (!string.IsNullOrEmpty(asset.Category?.Name)) categories.Add(asset.Category.Name);
            if (!string.IsNullOrEmpty(asset.Manufacturer?.Name)) manufacturers.Add(asset.Manufacturer.Name);
            if (!string.IsNullOrEmpty(asset.Model?.Name)) models.Add(asset.Model.Name);

            var platform = GetCustomFieldValue(asset, "Platform");
            if (!string.IsNullOrEmpty(platform)) platforms.Add(platform);
            var usage = GetCustomFieldValue(asset, "Usage");
            if (!string.IsNullOrEmpty(usage)) usages.Add(usage);
            var catalog = GetCustomFieldValue(asset, "Catalog");
            if (!string.IsNullOrEmpty(catalog)) catalogs.Add(catalog);
            var area = GetCustomFieldValue(asset, "Area");
            if (!string.IsNullOrEmpty(area)) areas.Add(area);
        }

        SetFilterItems(StatusFilterComboBox, statuses);
        SetFilterItems(CategoryFilterComboBox, categories);
        SetFilterItems(PlatformFilterComboBox, platforms);
        SetFilterItems(ManufacturerFilterComboBox, manufacturers);
        SetFilterItems(ModelFilterComboBox, models);
        SetFilterItems(UsageFilterComboBox, usages);
        SetFilterItems(CatalogFilterComboBox, catalogs);
        SetFilterItems(AreaFilterComboBox, areas);
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
        if (AssetListView.SelectedItem is SnipeAsset asset)
        {
            _selectedAsset = asset;
            ShowAssetDetail(asset);
        }
    }

    private async void ShowAssetDetail(SnipeAsset asset)
    {
        _isLoadingDetail = true;
        _hasEdits = false;
        _editStatusId = null;
        SaveChangesButton.Visibility = Visibility.Collapsed;

        DetailPanel.Visibility = Visibility.Visible;
        DetailPlaceholder.Visibility = Visibility.Collapsed;

        DetailAssetName.Text = asset.DisplayName;
        DetailAssetTag.Text = $"Tag: {asset.AssetTag}";

        // Status dropdown
        if (_statusLabels.Count == 0 && _snipeService != null)
        {
            _statusLabels = await _snipeService.GetStatusLabelsAsync();
        }
        DetailStatusComboBox.ItemsSource = _statusLabels;
        var currentStatus = _statusLabels.FirstOrDefault(s => s.Id == asset.StatusLabel?.Id);
        DetailStatusComboBox.SelectedItem = currentStatus;

        // Assignment (no username)
        if (asset.AssignedTo != null)
        {
            AssignmentSection.Visibility = Visibility.Visible;
            DetailAssignedName.Text = asset.AssignedTo.Name;
            DetailAssignedEmployee.Text = !string.IsNullOrEmpty(asset.AssignedTo.EmployeeNumber)
                ? $"Employee #{asset.AssignedTo.EmployeeNumber}" : "";
            ReAllocateButton.Visibility = Visibility.Visible;
        }
        else
        {
            AssignmentSection.Visibility = Visibility.Collapsed;
            ReAllocateButton.Visibility = Visibility.Collapsed;
        }

        // Hardware
        DetailSerial.Text = asset.Serial ?? "—";
        DetailModel.Text = asset.Model?.Name ?? "—";
        DetailCategory.Text = asset.Category?.Name ?? "—";
        DetailManufacturer.Text = asset.Manufacturer?.Name ?? "—";

        // Location
        if (asset.Location != null)
        {
            LocationSection.Visibility = Visibility.Visible;
            DetailLocation.Text = asset.Location.Name;
        }
        else
        {
            LocationSection.Visibility = Visibility.Collapsed;
        }

        // Dates
        DetailPurchaseDate.Text = asset.PurchaseDate?.Formatted ?? "—";
        DetailLastCheckout.Text = asset.LastCheckout?.Formatted ?? "—";
        DetailLastAudit.Text = asset.LastAuditDate ?? "—";
        DetailCreated.Text = asset.CreatedAt?.Formatted ?? "—";
        DetailUpdated.Text = asset.UpdatedAt?.Formatted ?? "—";

        // Notes
        if (!string.IsNullOrEmpty(asset.Notes))
        {
            NotesSection.Visibility = Visibility.Visible;
            DetailNotes.Text = asset.Notes;
        }
        else
        {
            NotesSection.Visibility = Visibility.Collapsed;
        }

        // Custom Fields (sectioned)
        PopulateCustomFields(asset);
        _isLoadingDetail = false;
    }

    private void PopulateCustomFields(SnipeAsset asset)
    {
        HardwareFieldsContainer.Children.Clear();
        ManagementFieldsContainer.Children.Clear();
        FinancialFieldsContainer.Children.Clear();
        OtherFieldsContainer.Children.Clear();

        HardwareFieldsSection.Visibility = Visibility.Collapsed;
        ManagementFieldsSection.Visibility = Visibility.Collapsed;
        FinancialFieldsSection.Visibility = Visibility.Collapsed;
        OtherFieldsSection.Visibility = Visibility.Collapsed;

        if (asset.CustomFields == null || asset.CustomFields.Count == 0)
            return;

        foreach (var kvp in asset.CustomFields.OrderBy(f => f.Value.Field))
        {
            var fieldName = kvp.Value.Field ?? "";
            var fieldValue = kvp.Value.Value ?? "";
            if (string.IsNullOrWhiteSpace(fieldValue)) continue;
            if (HiddenFieldNames.Contains(fieldName)) continue;

            StackPanel targetContainer;
            StackPanel targetSection;

            if (HardwareFieldNames.Contains(fieldName))
            {
                targetContainer = HardwareFieldsContainer;
                targetSection = HardwareFieldsSection;
            }
            else if (ManagementFieldNames.Contains(fieldName))
            {
                targetContainer = ManagementFieldsContainer;
                targetSection = ManagementFieldsSection;
            }
            else if (FinancialFieldNames.Contains(fieldName))
            {
                targetContainer = FinancialFieldsContainer;
                targetSection = FinancialFieldsSection;
            }
            else
            {
                targetContainer = OtherFieldsContainer;
                targetSection = OtherFieldsSection;
            }

            targetSection.Visibility = Visibility.Visible;

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Margin = new Thickness(0, 0, 0, 2);

            var label = new TextBlock
            {
                Text = $"{fieldName}:",
                FontSize = 18,
                Opacity = 0.7,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(label, 0);

            var value = new TextBlock
            {
                Text = fieldValue,
                FontSize = 18,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(value, 1);

            var copyBtn = new Button
            {
                Content = new TextBlock { Text = "📋", FontSize = 14 },
                Padding = new Thickness(2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ToolTip = "Copy",
                Tag = fieldValue
            };
            copyBtn.Click += OnCopyCustomFieldClicked;
            Grid.SetColumn(copyBtn, 2);

            row.Children.Add(label);
            row.Children.Add(value);
            row.Children.Add(copyBtn);

            targetContainer.Children.Add(row);
        }
    }

    // MARK: - Status Edit & Save

    private void OnDetailStatusChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingDetail || _selectedAsset == null) return;

        if (DetailStatusComboBox.SelectedItem is SnipeStatusLabelFull selected)
        {
            if (selected.Id != _selectedAsset.StatusLabel?.Id)
            {
                _editStatusId = selected.Id;
                _hasEdits = true;
                SaveChangesButton.Visibility = Visibility.Visible;
            }
            else
            {
                _editStatusId = null;
                _hasEdits = false;
                SaveChangesButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async void OnSaveChangesClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedAsset == null || _snipeService == null || !_hasEdits) return;

        try
        {
            SaveChangesButton.IsEnabled = false;
            SaveChangesButton.Content = "Saving...";

            var request = new SnipeAssetRequest
            {
                AssetTag = _selectedAsset.AssetTag,
                StatusId = _editStatusId ?? _selectedAsset.StatusLabel?.Id ?? 0,
                ModelId = _selectedAsset.Model?.Id ?? 0
            };

            var result = await _snipeService.UpdateAssetAsync(_selectedAsset.Id, request);

            if (result != null && result.IsSuccess)
            {
                _hasEdits = false;
                _editStatusId = null;
                SaveChangesButton.Visibility = Visibility.Collapsed;
                await LoadAssetsAsync();
            }
            else
            {
                MessageBox.Show($"Update failed: {result?.Messages ?? "Unknown error"}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Save failed: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SaveChangesButton.IsEnabled = true;
            SaveChangesButton.Content = "Save Changes";
        }
    }

    // MARK: - Helpers

    private static string GetCustomFieldValue(SnipeAsset asset, string displayName)
    {
        if (asset.CustomFields == null) return "";
        var field = asset.CustomFields.Values.FirstOrDefault(f =>
            string.Equals(f.Field, displayName, StringComparison.OrdinalIgnoreCase));
        return field?.Value ?? "";
    }

    // MARK: - Actions

    private void OnOpenInSnipeClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedAsset == null || _snipeService == null) return;

        var url = $"{_snipeService.BaseUrl}/hardware/{_selectedAsset.Id}";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { }
    }

    private void OnCopySerialClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedAsset?.Serial != null)
        {
            Clipboard.SetText(_selectedAsset.Serial);
        }
    }

    private void OnCopyCustomFieldClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string value)
        {
            Clipboard.SetText(value);
        }
    }

    // MARK: - Re-Allocate

    private async void OnReAllocateClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedAsset == null || _snipeService == null) return;

        var dialog = new ReAllocateDialog(_snipeService, _selectedAsset);
        var result = dialog.ShowDialog();

        if (result == true && dialog.SelectedUserId.HasValue)
        {
            try
            {
                LoadingOverlay.Visibility = Visibility.Visible;

                // Step 1: Checkin
                var checkinResult = await _snipeService.CheckinAssetAsync(_selectedAsset.Id,
                    new SnipeCheckinRequest { Note = "Re-allocated via FleetMate" });

                if (checkinResult == null || !checkinResult.IsSuccess)
                {
                    MessageBox.Show($"Check-in failed: {checkinResult?.Messages ?? "Unknown error"}", 
                        "Re-Allocate", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Step 2: Checkout to new user
                var checkoutResult = await _snipeService.CheckoutAssetAsync(_selectedAsset.Id,
                    new SnipeCheckoutRequest
                    {
                        AssignedUser = dialog.SelectedUserId.Value,
                        CheckoutToType = "user",
                        Note = dialog.Note ?? "Re-allocated via FleetMate"
                    });

                if (checkoutResult != null && checkoutResult.IsSuccess)
                {
                    MessageBox.Show("Asset re-allocated successfully.", "Re-Allocate",
                        MessageBoxButton.OK, MessageBoxImage.Information);

                    // Refresh
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

/// <summary>
/// Converts a CustomFields dictionary to a display value by looking up the field display name.
/// Usage: Converter={StaticResource CustomFieldConverter}, ConverterParameter=Platform
/// </summary>
public class CustomFieldValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Dictionary<string, SnipeCustomField> fields && parameter is string fieldName)
        {
            var match = fields.Values.FirstOrDefault(f =>
                string.Equals(f.Field, fieldName, StringComparison.OrdinalIgnoreCase));
            return match?.Value ?? "";
        }
        return "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
