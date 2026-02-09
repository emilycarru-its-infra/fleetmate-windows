using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FleetMate.Config;
using FleetMate.Models.Snipe;
using FleetMate.Services;

namespace FleetMate.GUI.Views;

public partial class AssetsPage : Page
{
    private readonly FleetMateConfig _config;
    private SnipeService? _snipeService;
    private List<SnipeAsset> _allAssets = new();
    private SnipeAsset? _selectedAsset;

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

        var list = filtered.ToList();
        AssetListView.ItemsSource = list;
        AssetCountLabel.Text = $"{list.Count} assets";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateDisplay();
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

    private void ShowAssetDetail(SnipeAsset asset)
    {
        DetailPanel.Visibility = Visibility.Visible;
        DetailPlaceholder.Visibility = Visibility.Collapsed;

        DetailAssetName.Text = asset.DisplayName;
        DetailAssetTag.Text = $"Tag: {asset.AssetTag}";

        // Status
        DetailStatus.Text = asset.StatusLabel?.Name ?? "—";

        // Assignment
        if (asset.AssignedTo != null)
        {
            AssignmentSection.Visibility = Visibility.Visible;
            DetailAssignedName.Text = asset.AssignedTo.Name;
            DetailAssignedUsername.Text = !string.IsNullOrEmpty(asset.AssignedTo.Username)
                ? $"@{asset.AssignedTo.Username}" : "";
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

        // Custom Fields
        PopulateCustomFields(asset);
    }

    private void PopulateCustomFields(SnipeAsset asset)
    {
        CustomFieldsContainer.Children.Clear();

        if (asset.CustomFields == null || asset.CustomFields.Count == 0)
        {
            CustomFieldsSection.Visibility = Visibility.Collapsed;
            return;
        }

        CustomFieldsSection.Visibility = Visibility.Visible;

        foreach (var kvp in asset.CustomFields.OrderBy(f => f.Value.Field))
        {
            var fieldValue = kvp.Value.Value ?? "";
            if (string.IsNullOrWhiteSpace(fieldValue)) continue;

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Margin = new Thickness(0, 0, 0, 2);

            var label = new TextBlock
            {
                Text = $"{kvp.Value.Field}:",
                FontSize = 12,
                Opacity = 0.7,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(label, 0);

            var value = new TextBlock
            {
                Text = fieldValue,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(value, 1);

            var copyBtn = new Button
            {
                Content = new TextBlock { Text = "📋", FontSize = 10 },
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

            CustomFieldsContainer.Children.Add(row);
        }
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
