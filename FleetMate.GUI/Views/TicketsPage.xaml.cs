using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Models.Tdx;
using FleetMate.Services;

namespace FleetMate.GUI.Views;

public partial class TicketsPage : Page
{
    private readonly App? _app;
    private readonly TdxService? _tdxService;
    private ObservableCollection<TdxTicket> _filteredTickets = new();
    private List<TdxFeedEntry> _ticketFeed = new();
    private TdxTicket? _selectedTicket;
    private bool _sortAscending = false;  // Default descending (newest first)
    private string _sortField = "Modified";  // Default to Modified date
    private bool _detailPanelVisible = false;
    private bool _hideClosed = true;  // Default to hiding closed tickets
    private bool _isBoardView = false;  // List vs Board view mode
    
    // Use cached tickets from App
    private List<TdxTicket> _allTickets => _app?.CachedTickets ?? new();
    
    // Status colors for board columns
    private static readonly Dictionary<string, SolidColorBrush> StatusColors = new()
    {
        { "New", new SolidColorBrush(Color.FromRgb(0x40, 0x80, 0xFF)) },
        { "Open", new SolidColorBrush(Color.FromRgb(0x40, 0x80, 0xFF)) },
        { "In Progress", new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x40)) },
        { "In Process", new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x40)) },
        { "On Hold", new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)) },
        { "Awaiting Response", new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)) },
        { "Resolved", new SolidColorBrush(Color.FromRgb(0x40, 0xC0, 0x40)) },
        { "Closed", new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)) },
        { "Cancelled", new SolidColorBrush(Color.FromRgb(0x80, 0x60, 0x60)) },
    };

    public TicketsPage()
    {
        InitializeComponent();

        // Get services from App
        if (Application.Current is App app)
        {
            _app = app;
            _tdxService = app.TdxService;
        }

        TicketsListView.ItemsSource = _filteredTickets;

        Loaded += async (s, e) => 
        {
            UpdateSsoState();
            await LoadTicketsAsync();
        };
    }
    
    private void UpdateSsoState()
    {
        if (_app == null || _tdxService == null) return;
        
        // Check if SSO should be shown
        if (_tdxService.ShouldAttemptSso)
        {
            if (_tdxService.IsSsoAuthenticated)
            {
                // Show user info
                SsoUserBorder.Visibility = Visibility.Visible;
                SsoUserNameText.Text = _tdxService.AuthenticatedUserName ?? "Signed In";
                SsoLoginButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Show login button
                SsoUserBorder.Visibility = Visibility.Collapsed;
                SsoLoginButton.Visibility = Visibility.Visible;
            }
        }
        else
        {
            // SSO not enabled, hide both
            SsoUserBorder.Visibility = Visibility.Collapsed;
            SsoLoginButton.Visibility = Visibility.Collapsed;
        }
    }
    
    private void OnSsoLoginClicked(object sender, RoutedEventArgs e)
    {
        _app?.ShowTdxSsoLogin(success =>
        {
            UpdateSsoState();
            if (success)
            {
                // Reload tickets with new auth
                _ = LoadTicketsAsync();
            }
        });
    }
    
    private void OnSsoSignOutClicked(object sender, RoutedEventArgs e)
    {
        _app?.SignOutTdxSso();
        UpdateSsoState();
    }

    private async Task LoadTicketsAsync()
    {
        if (_tdxService == null || _app == null)
        {
            NotConfiguredText.Visibility = Visibility.Visible;
            return;
        }
        
        // Use cache if valid
        if (_app.IsTicketsCacheValid && _app.CachedTickets.Count > 0)
        {
            UpdateFilterOptions();
            ApplyFiltersAndSort();
            return;
        }

        LoadingPanel.Visibility = Visibility.Visible;
        NotConfiguredText.Visibility = Visibility.Collapsed;

        try
        {
            var search = new TicketSearchRequest { MaxResults = 500 };
            
            // Apply group filter from config if set
            if (_app.Config.Tdx?.ResponsibleGroupId > 0)
            {
                search.ResponsibleGroupIds = new List<int> { _app.Config.Tdx.ResponsibleGroupId };
            }
            
            var tickets = await _tdxService.SearchTicketsAsync(search, 500);
            _app.UpdateTicketsCache(tickets);
            UpdateFilterOptions();
            ApplyFiltersAndSort();
        }
        catch (Exception ex)
        {
            ShowActionMessage($"Error: {ex.Message}", isError: true);
        }
        finally
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateFilterOptions()
    {
        var statuses = new HashSet<string> { "All" };
        var groups = new HashSet<string> { "All" };
        var responsible = new HashSet<string> { "All" };

        foreach (var ticket in _allTickets)
        {
            if (!string.IsNullOrEmpty(ticket.StatusName)) statuses.Add(ticket.StatusName);
            if (!string.IsNullOrEmpty(ticket.ResponsibleGroupName)) groups.Add(ticket.ResponsibleGroupName);
            if (!string.IsNullOrEmpty(ticket.ResponsibleFullName)) responsible.Add(ticket.ResponsibleFullName);
        }

        StatusFilterComboBox.ItemsSource = statuses.OrderBy(s => s == "All" ? "" : s).ToList();
        StatusFilterComboBox.SelectedIndex = 0;
        
        GroupFilterComboBox.ItemsSource = groups.OrderBy(s => s == "All" ? "" : s).ToList();
        GroupFilterComboBox.SelectedIndex = 0;
        
        ResponsibleFilterComboBox.ItemsSource = responsible.OrderBy(s => s == "All" ? "" : s).ToList();
        ResponsibleFilterComboBox.SelectedIndex = 0;
    }

    private void ApplyFiltersAndSort()
    {
        var filtered = _allTickets.AsEnumerable();

        // Filter hide closed (before status filter)
        if (_hideClosed)
        {
            filtered = filtered.Where(t => 
                t.StatusName?.ToLower() != "closed" && 
                t.StatusName?.ToLower() != "cancelled" &&
                t.StatusName?.ToLower() != "canceled");
        }

        // Filter by status
        var statusFilter = StatusFilterComboBox.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(statusFilter) && statusFilter != "All")
        {
            filtered = filtered.Where(t => t.StatusName == statusFilter);
        }

        // Filter by group
        var groupFilter = GroupFilterComboBox.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(groupFilter) && groupFilter != "All")
        {
            filtered = filtered.Where(t => t.ResponsibleGroupName == groupFilter);
        }

        // Filter by responsible
        var responsibleFilter = ResponsibleFilterComboBox.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(responsibleFilter) && responsibleFilter != "All")
        {
            filtered = filtered.Where(t => t.ResponsibleFullName == responsibleFilter);
        }

        // Filter by search text
        var searchText = SearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(searchText))
        {
            filtered = filtered.Where(t =>
                (t.Title?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true) ||
                (t.RequestorName?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true) ||
                t.Id.ToString().Contains(searchText));
        }

        // Sort
        filtered = _sortField switch
        {
            "Modified" => _sortAscending ? filtered.OrderBy(t => t.ModifiedDate) : filtered.OrderByDescending(t => t.ModifiedDate),
            "Created" => _sortAscending ? filtered.OrderBy(t => t.CreatedDate) : filtered.OrderByDescending(t => t.CreatedDate),
            "Title" => _sortAscending ? filtered.OrderBy(t => t.Title) : filtered.OrderByDescending(t => t.Title),
            "Status" => _sortAscending ? filtered.OrderBy(t => t.StatusName) : filtered.OrderByDescending(t => t.StatusName),
            "Priority" => _sortAscending ? filtered.OrderBy(t => t.PriorityName) : filtered.OrderByDescending(t => t.PriorityName),
            "Requestor" => _sortAscending ? filtered.OrderBy(t => t.RequestorName) : filtered.OrderByDescending(t => t.RequestorName),
            "Responsible" => _sortAscending ? filtered.OrderBy(t => t.ResponsibleFullName) : filtered.OrderByDescending(t => t.ResponsibleFullName),
            _ => _sortAscending ? filtered.OrderBy(t => t.ModifiedDate) : filtered.OrderByDescending(t => t.ModifiedDate)
        };

        _filteredTickets.Clear();
        foreach (var ticket in filtered)
        {
            _filteredTickets.Add(ticket);
        }
        
        // Update ticket count display
        TicketCountText.Text = $"{_filteredTickets.Count} of {_allTickets.Count} tickets";
        
        // Update board view if active
        if (_isBoardView)
        {
            UpdateBoardView();
        }
    }
    
    private void OnViewModeChanged(object sender, RoutedEventArgs e)
    {
        _isBoardView = BoardViewRadio.IsChecked == true;
        
        ListViewPanel.Visibility = _isBoardView ? Visibility.Collapsed : Visibility.Visible;
        BoardViewPanel.Visibility = _isBoardView ? Visibility.Visible : Visibility.Collapsed;
        
        if (_isBoardView)
        {
            UpdateBoardView();
        }
    }
    
    private void UpdateBoardView()
    {
        // Group filtered tickets by status
        var columns = _filteredTickets
            .GroupBy(t => t.StatusName ?? "Unknown")
            .OrderBy(g => GetStatusOrder(g.Key))
            .Select(g => new BoardColumn
            {
                StatusName = g.Key,
                HeaderColor = GetStatusColor(g.Key),
                Count = g.Count(),
                Tickets = g.ToList()
            })
            .ToList();
        
        BoardColumnsControl.ItemsSource = columns;
    }
    
    private static SolidColorBrush GetStatusColor(string status)
    {
        return StatusColors.TryGetValue(status, out var color) 
            ? color 
            : new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
    }
    
    private static int GetStatusOrder(string status)
    {
        return status.ToLower() switch
        {
            "new" => 0,
            "open" => 1,
            "in progress" or "in process" => 2,
            "awaiting response" => 3,
            "on hold" => 4,
            "resolved" => 5,
            "closed" => 6,
            "cancelled" or "canceled" => 7,
            _ => 5
        };
    }
    
    private async void OnBoardCardClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is int ticketId)
        {
            var ticket = _filteredTickets.FirstOrDefault(t => t.Id == ticketId);
            if (ticket != null)
            {
                _selectedTicket = ticket;
                SelectionInfoText.Text = $"• #{ticket.Id} selected";
                
                // Show detail panel
                if (!_detailPanelVisible)
                {
                    _detailPanelVisible = true;
                    DetailPanel.Visibility = Visibility.Visible;
                    DetailPanelColumn.Width = new GridLength(360);
                }
                
                await LoadTicketDetailAsync(ticket.Id);
            }
        }
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyFiltersAndSort();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFiltersAndSort();
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortComboBox.SelectedItem is ComboBoxItem item)
        {
            _sortField = item.Content?.ToString() ?? "Created";
            ApplyFiltersAndSort();
        }
    }

    private void OnSortDirectionClicked(object sender, RoutedEventArgs e)
    {
        _sortAscending = !_sortAscending;
        SortDirectionButton.Content = _sortAscending ? "↑" : "↓";
        ApplyFiltersAndSort();
    }

    private void OnHideClosedChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkbox)
        {
            _hideClosed = checkbox.IsChecked == true;
            ApplyFiltersAndSort();
        }
    }

    private void OnOpenInWebClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedTicket == null || _app?.Config?.Tdx == null) return;
        
        var uri = _selectedTicket.Uri;
        if (string.IsNullOrEmpty(uri)) return;
        
        // Build full URL: baseUrl without /TDWebApi + uri
        var baseUrl = _app.Config.Tdx.BaseUrl ?? "";
        var webBaseUrl = baseUrl.Replace("/TDWebApi", "");
        var fullUrl = webBaseUrl + uri;
        
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fullUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowActionMessage($"Failed to open browser: {ex.Message}", isError: true);
        }
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        // Invalidate cache to force reload
        if (_app != null)
        {
            _app.CachedTickets.Clear();
        }
        await LoadTicketsAsync();
    }

    private void OnToggleDetailPanel(object sender, RoutedEventArgs e)
    {
        _detailPanelVisible = !_detailPanelVisible;
        DetailPanel.Visibility = _detailPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        DetailPanelColumn.Width = _detailPanelVisible ? new GridLength(360) : new GridLength(0);
    }

    private void OnCloseDetailPanel(object sender, RoutedEventArgs e)
    {
        _detailPanelVisible = false;
        DetailPanel.Visibility = Visibility.Collapsed;
        DetailPanelColumn.Width = new GridLength(0);
    }

    private async void OnTicketSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TicketsListView.SelectedItem is TdxTicket ticket)
        {
            _selectedTicket = ticket;
            SelectionInfoText.Text = $"• #{ticket.Id} selected";
            
            // Show detail panel if not already visible
            if (!_detailPanelVisible)
            {
                _detailPanelVisible = true;
                DetailPanel.Visibility = Visibility.Visible;
                DetailPanelColumn.Width = new GridLength(360);
            }
            
            await LoadTicketDetailAsync(ticket.Id);
        }
    }

    private async Task LoadTicketDetailAsync(int ticketId)
    {
        if (_tdxService == null) return;

        NoSelectionPanel.Visibility = Visibility.Collapsed;
        DetailContent.Visibility = Visibility.Visible;
        
        DetailTicketIdText.Text = $"#{ticketId}";
        
        try
        {
            // Get fresh ticket details
            var ticket = await _tdxService.GetTicketAsync(ticketId);
            if (ticket != null)
            {
                _selectedTicket = ticket;
                UpdateDetailPanel(ticket);
            }
            
            // Get feed/comments
            _ticketFeed = await _tdxService.GetTicketFeedAsync(ticketId);
            UpdateFeedPanel();
        }
        catch (Exception ex)
        {
            ShowActionMessage($"Error loading details: {ex.Message}", isError: true);
        }
    }

    private void UpdateDetailPanel(TdxTicket ticket)
    {
        DetailTitleText.Text = ticket.Title ?? "Untitled";
        DetailStatusText.Text = ticket.StatusName ?? "Unknown";
        DetailPriorityText.Text = ticket.PriorityName ?? "-";
        DetailRequestorText.Text = ticket.RequestorName ?? "-";
        DetailEmailText.Text = ticket.RequestorEmail ?? "-";
        DetailGroupText.Text = ticket.ResponsibleGroupName ?? "-";
        DetailResponsibleText.Text = ticket.ResponsibleFullName ?? "-";
        DetailCreatedText.Text = FormatDate(ticket.CreatedDate);
        DetailModifiedText.Text = FormatDate(ticket.ModifiedDate);

        // Status badge color
        var statusBrush = ticket.StatusName?.ToLower() switch
        {
            "new" or "open" => new SolidColorBrush(Color.FromRgb(0x40, 0x80, 0xFF)),
            "in progress" or "in process" => new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x40)),
            "resolved" or "closed" => new SolidColorBrush(Color.FromRgb(0x40, 0xC0, 0x40)),
            _ => new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0))
        };
        DetailStatusBadge.Background = statusBrush;

        // Description with proper HTML decoding
        if (!string.IsNullOrEmpty(ticket.Description))
        {
            DetailDescriptionText.Text = DecodeHtml(ticket.Description);
            DescriptionSection.Visibility = Visibility.Visible;
        }
        else
        {
            DescriptionSection.Visibility = Visibility.Collapsed;
        }
        
        // Update notify options
        UpdateNotifyOptions(ticket);
    }

    private void UpdateFeedPanel()
    {
        // Filter out System activity entries
        var filteredFeed = _ticketFeed.Where(f => f.CreatedFullName != "System").ToList();
        
        if (filteredFeed.Count > 0)
        {
            FeedHeaderText.Text = $"Activity ({filteredFeed.Count})";
            
            // Convert feed entries for display
            var displayFeed = filteredFeed.Take(20).Select(f => new FeedDisplayItem
            {
                CreatedFullName = f.CreatedFullName ?? "Unknown",
                FormattedDate = FormatDate(f.CreatedDate),
                StrippedBody = DecodeHtml(f.Body ?? ""),
                IsPrivate = f.IsPrivate ?? false
            }).ToList();
            
            FeedItemsControl.ItemsSource = displayFeed;
            FeedSection.Visibility = Visibility.Visible;
        }
        else
        {
            FeedSection.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateNotifyOptions(TdxTicket ticket)
    {
        NotifyCheckboxesPanel.Children.Clear();
        
        // Build notify options from ticket properties
        var options = new List<(string id, string label)>();
        
        // Responsible Group
        if (!string.IsNullOrEmpty(ticket.ResponsibleGroupName) && ticket.ResponsibleGroupId > 0)
        {
            options.Add(($"group:{ticket.ResponsibleGroupId}", $"Group: {ticket.ResponsibleGroupName}"));
        }
        
        // Requestor
        if (!string.IsNullOrEmpty(ticket.RequestorName) && !string.IsNullOrEmpty(ticket.RequestorUid))
        {
            options.Add(($"user:{ticket.RequestorUid}", $"Requestor: {ticket.RequestorName}"));
        }
        
        // Responsible person (if different from requestor)
        if (!string.IsNullOrEmpty(ticket.ResponsibleFullName) && !string.IsNullOrEmpty(ticket.ResponsibleUid))
        {
            if (ticket.ResponsibleUid != ticket.RequestorUid)
            {
                options.Add(($"user:{ticket.ResponsibleUid}", $"Responsible: {ticket.ResponsibleFullName}"));
            }
        }
        
        // Create checkboxes
        foreach (var (id, label) in options)
        {
            var checkbox = new CheckBox
            {
                Content = label,
                Tag = id,
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = 12
            };
            NotifyCheckboxesPanel.Children.Add(checkbox);
        }
    }

    private async void OnRefreshDetailClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedTicket != null)
        {
            await LoadTicketDetailAsync(_selectedTicket.Id);
        }
    }

    private async void OnPostCommentClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedTicket == null || _tdxService == null) return;
        
        var comment = CommentTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(comment)) return;

        ShowActionMessage("Adding comment...", isLoading: true);
        PostCommentButton.IsEnabled = false;

        try
        {
            var success = await _tdxService.AddCommentAsync(
                _selectedTicket.Id,
                comment,
                PrivateCheckBox.IsChecked == true);

            if (success)
            {
                ShowActionMessage("Comment added successfully");
                CommentTextBox.Text = "";
                
                // Refresh feed
                _ticketFeed = await _tdxService.GetTicketFeedAsync(_selectedTicket.Id);
                UpdateFeedPanel();
            }
            else
            {
                ShowActionMessage("Failed to add comment", isError: true);
            }
        }
        catch (Exception ex)
        {
            ShowActionMessage($"Error: {ex.Message}", isError: true);
        }
        finally
        {
            PostCommentButton.IsEnabled = true;
        }
    }

    private void ShowActionMessage(string message, bool isError = false, bool isLoading = false)
    {
        ActionMessageBorder.Visibility = Visibility.Visible;
        ActionMessageText.Text = message;
        ActionProgressRing.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        ActionProgressRing.IsActive = isLoading;
        ActionMessageBorder.Background = isError 
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC0, 0xC0))
            : (isLoading ? new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xFF))
                         : new SolidColorBrush(Color.FromRgb(0xC0, 0xFF, 0xC0)));
    }

    private static string FormatDate(string? dateString)
    {
        if (string.IsNullOrEmpty(dateString)) return "-";
        
        // Parse ISO8601 date and convert to local timezone
        if (DateTime.TryParse(dateString, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
        {
            var localDt = dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt;
            return localDt.ToString("MMM d 'at' h:mm tt");
        }
        
        // Fallback: just format the string
        return dateString.Length >= 16 ? dateString[..16].Replace("T", " ") : dateString;
    }

    private static string StripHtml(string html)
    {
        return Regex.Replace(html, "<[^>]+>", "");
    }

    /// <summary>
    /// Decode HTML entities and convert line breaks for display
    /// </summary>
    private static string DecodeHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        
        var result = html;
        
        // Convert <br> and </p> to newlines before stripping tags
        result = Regex.Replace(result, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"</p>", "\n", RegexOptions.IgnoreCase);
        
        // Strip all remaining HTML tags
        result = Regex.Replace(result, @"<[^>]+>", "");
        
        // Decode common HTML entities
        result = result.Replace("&nbsp;", " ");
        result = result.Replace("&amp;", "&");
        result = result.Replace("&lt;", "<");
        result = result.Replace("&gt;", ">");
        result = result.Replace("&quot;", "\"");
        result = result.Replace("&#39;", "'");
        result = result.Replace("&apos;", "'");
        
        // Clean up multiple consecutive newlines
        result = Regex.Replace(result, @"\n{3,}", "\n\n");
        
        return result.Trim();
    }
}

// Helper class for feed display
public class FeedDisplayItem
{
    public string CreatedFullName { get; set; } = "";
    public string FormattedDate { get; set; } = "";
    public string StrippedBody { get; set; } = "";
    public bool IsPrivate { get; set; }
}

// Helper class for board columns
public class BoardColumn
{
    public string StatusName { get; set; } = "";
    public SolidColorBrush HeaderColor { get; set; } = new(Colors.Gray);
    public int Count { get; set; }
    public List<TdxTicket> Tickets { get; set; } = new();
}
