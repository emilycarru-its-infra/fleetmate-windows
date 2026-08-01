using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Services.Tickets;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Reporting;

namespace FleetMate.GUI.Views.Tickets;

public partial class TicketsPage : Page
{
    private readonly App? _app;
    private readonly TdxService? _tdxService;
    private ObservableCollection<TdxTicket> _filteredTickets = new();

    /// <summary>
    /// Rows actually rendered by the list — the filtered tickets as an outline,
    /// with collapsed subtrees omitted.
    /// </summary>
    private readonly ObservableCollection<TicketRowViewModel> _ticketRows = new();

    /// <summary>
    /// Folded parents, shared by the list and the board. One set rather than one
    /// per view, so switching modes does not silently re-expand everything.
    /// </summary>
    private readonly HashSet<int> _collapsedTickets = new();

    /// <summary>Unsent comments, keyed by ticket. A draft belongs to its ticket.</summary>
    private readonly Dictionary<int, string> _commentDrafts = new();

    /// <summary>
    /// Filter entry for tickets nobody has picked up. Not a real responsible
    /// name, so it is matched by identity rather than compared as one.
    /// </summary>
    internal const string UnassignedFilterLabel = "(Unassigned)";
    private List<TdxFeedEntry> _ticketFeed = new();
    private TdxTicket? _selectedTicket;
    private bool _sortAscending = false;  // Default descending (newest first)
    private string _sortField = "Modified";  // Default to Modified date
    private bool _detailPanelVisible = false;
    private bool _showClosed = false;  // Default to hiding closed tickets (Show Closed unchecked)
    private bool _isBoardView = false;  // List vs Board view mode
    private string _feedFilter = "Comments";  // Comments (default), Activity, All
    private int _maxResults = 500;  // Default max results
    private bool _isInitialLoadDone;
    
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

        TicketsListView.ItemsSource = _ticketRows;

        Loaded += async (s, e) => 
        {
            if (!_isInitialLoadDone)
            {
                _isInitialLoadDone = true;
                UpdateSsoState();
                await LoadTicketsAsync();
            }
            // Deep link: check on every navigation (page is cached)
            if (_app?.PendingNavigateTicketId is { } ticketId)
            {
                _app.PendingNavigateTicketId = null;
                RevealTicket(ticketId);
            }
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
        // Reachable during BAML load: LimitComboBox marks an item IsSelected in
        // markup, so SelectionChanged fires mid-parse and lands here before
        // NotConfiguredText and the rest of the tree exist. Because
        // OnLimitChanged is async void, the resulting NullReferenceException
        // surfaces on the dispatcher as unhandled and kills the process — the
        // Tickets tab took the whole app down with it.
        if (!IsInitialized) return;

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
            var search = new TicketSearchRequest { MaxResults = _maxResults };
            
            // Apply group filter from config if set
            if (_app.Config.Tdx?.ResponsibleGroupId > 0)
            {
                search.ResponsibleGroupIds = new List<int> { _app.Config.Tdx.ResponsibleGroupId };
            }
            
            var tickets = await _tdxService.SearchTicketsAsync(search, _maxResults);
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
        
        // Unassigned is offered explicitly. Tickets nobody has picked up are
        // the ones most worth finding, and they were unreachable through a
        // filter built only from names that exist.
        var responsibleOptions = responsible.OrderBy(s => s == "All" ? "" : s).ToList();
        if (_allTickets.Any(t => string.IsNullOrWhiteSpace(t.ResponsibleFullName)))
        {
            responsibleOptions.Add(UnassignedFilterLabel);
        }

        ResponsibleFilterComboBox.ItemsSource = responsibleOptions;
        ResponsibleFilterComboBox.SelectedIndex = 0;
    }

    private void ApplyFiltersAndSort()
    {
        // Reached during BAML load too: SortComboBox marks an item IsSelected in
        // markup, so SelectionChanged fires while the tree is half-built and the
        // controls this touches — TicketsListView in particular — are still null.
        // Nothing here is meaningful before the page exists.
        if (!IsInitialized) return;

        var filtered = _allTickets.AsEnumerable();

        // Filter show closed (hide when unchecked)
        if (!_showClosed)
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
            filtered = responsibleFilter == UnassignedFilterLabel
                ? filtered.Where(t => string.IsNullOrWhiteSpace(t.ResponsibleFullName))
                : filtered.Where(t => t.ResponsibleFullName == responsibleFilter);
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

        RebuildOutline();

        // Count the tickets, not the visible rows — a folded subtree must not
        // read as work that disappeared.
        TicketCountText.Text = $"{_filteredTickets.Count} of {_allTickets.Count}";
        
        // Update board view if active
        if (_isBoardView)
        {
            UpdateBoardView();
        }
    }
    
    /// <summary>
    /// Rebuild the visible rows from the filtered tickets and the current fold
    /// state, preserving the selection across the swap.
    /// </summary>
    private void RebuildOutline()
    {
        // Belt and braces: every caller is guarded, but this one dereferences
        // the list directly and a future caller added before load would fault.
        if (TicketsListView is null) return;

        // ItemsSource is replaced wholesale, so WPF drops the selection. Losing
        // the open ticket every time a filter changes would close the detail
        // pane out from under the operator.
        var selectedId = (TicketsListView.SelectedItem as TicketRowViewModel)?.Id
                         ?? _selectedTicket?.Id;

        var tree = TicketHierarchy.Build(_filteredTickets);
        var rows = TicketHierarchy.Flatten(tree, _collapsedTickets);

        _ticketRows.Clear();
        foreach (var row in rows) _ticketRows.Add(new TicketRowViewModel { Row = row });

        if (selectedId is { } id)
        {
            var match = _ticketRows.FirstOrDefault(r => r.Id == id);
            if (match != null) TicketsListView.SelectedItem = match;
        }
    }

    /// <summary>
    /// Select and scroll to a ticket, unfolding whatever hides it.
    ///
    /// A deep link to a child ticket lands on nothing if its parent is
    /// collapsed, so the ancestors are expanded first rather than the link
    /// silently doing nothing.
    /// </summary>
    private void RevealTicket(int ticketId)
    {
        var ticket = _filteredTickets.FirstOrDefault(t => t.Id == ticketId);
        if (ticket == null) return;

        var byId = _filteredTickets.ToDictionary(t => t.Id);
        var cursor = TicketHierarchy.ParentTicketId(ticket);
        var guard = new HashSet<int>();

        while (cursor is { } parentId && guard.Add(parentId))
        {
            _collapsedTickets.Remove(parentId);
            cursor = byId.TryGetValue(parentId, out var parent)
                ? TicketHierarchy.ParentTicketId(parent)
                : null;
        }

        RebuildOutline();

        var row = _ticketRows.FirstOrDefault(r => r.Id == ticketId);
        if (row == null) return;

        TicketsListView.SelectedItem = row;
        TicketsListView.ScrollIntoView(row);
    }

    private void OnToggleTicketFold(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TicketRowViewModel row }) return;

        if (!_collapsedTickets.Remove(row.Id)) _collapsedTickets.Add(row.Id);

        RebuildOutline();
        if (_isBoardView) UpdateBoardView();
    }

    private void OnCollapseAllClicked(object sender, RoutedEventArgs e)
    {
        foreach (var id in TicketHierarchy.AllParentIds(TicketHierarchy.Build(_filteredTickets)))
            _collapsedTickets.Add(id);

        RebuildOutline();
        if (_isBoardView) UpdateBoardView();
    }

    private void OnExpandAllClicked(object sender, RoutedEventArgs e)
    {
        _collapsedTickets.Clear();

        RebuildOutline();
        if (_isBoardView) UpdateBoardView();
    }

    private void OnViewModeChanged(object sender, RoutedEventArgs e)
    {
        // ListViewRadio carries IsChecked="True", so its Checked event fires
        // while the XAML is still being parsed — before BoardViewRadio and the
        // panels below it exist. Dereferencing them there took the whole app
        // down with a NullReferenceException the moment Tickets was opened.
        // IsInitialized is false until InitializeComponent finishes, which is
        // exactly the window we need to sit out.
        if (!IsInitialized) return;

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
                // The count is of tickets in the column, not visible cards — a
                // folded subtree must not read as work that disappeared.
                Count = g.Count(),
                Rows = TicketHierarchy
                    .Flatten(TicketHierarchy.Build(g), _collapsedTickets)
                    .Select(row => new TicketRowViewModel { Row = row })
                    .ToList(),
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

    private void OnShowClosedChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkbox)
        {
            _showClosed = checkbox.IsChecked == true;
            ApplyFiltersAndSort();
        }
    }

    private async void OnLimitChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem item)
        {
            if (int.TryParse(item.Content?.ToString(), out int limit))
            {
                _maxResults = limit;
                await LoadTicketsAsync();
            }
        }
    }

    private void OnFeedFilterChanged(object sender, RoutedEventArgs e)
    {
        // FeedFilterComments is checked in markup, so this fires before its
        // sibling radios exist.
        if (!IsInitialized) return;

        if (FeedFilterComments.IsChecked == true)
            _feedFilter = "Comments";
        else if (FeedFilterActivity.IsChecked == true)
            _feedFilter = "Activity";
        else if (FeedFilterAll.IsChecked == true)
            _feedFilter = "All";
        
        UpdateFeedPanel();
    }

    private void OnOpenInWebClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedTicket == null || _app?.Config?.Tdx == null) return;
        
        var url = GetTicketUrl();
        if (string.IsNullOrEmpty(url)) return;
        
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowActionMessage($"Failed to open browser: {ex.Message}", isError: true);
        }
    }

    private void OnCopyTicketIdClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedTicket == null) return;
        try
        {
            Clipboard.SetText(_selectedTicket.Id.ToString());
            ShowActionMessage("Ticket number copied");
        }
        catch (Exception ex)
        {
            ShowActionMessage($"Copy failed: {ex.Message}", isError: true);
        }
    }

    private void OnCopyTicketLinkClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedTicket == null) return;
        var url = GetTicketUrl();
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            Clipboard.SetText(url);
            ShowActionMessage("Ticket link copied");
        }
        catch (Exception ex)
        {
            ShowActionMessage($"Copy failed: {ex.Message}", isError: true);
        }
    }

    private string? GetTicketUrl()
    {
        if (_selectedTicket == null || _app?.Config?.Tdx == null) return null;
        
        var uri = _selectedTicket.Uri;
        if (string.IsNullOrEmpty(uri)) return null;
        
        var baseUrl = _app.Config.Tdx.BaseUrl ?? "";
        var webBaseUrl = baseUrl.Replace("/TDWebApi", "");
        return webBaseUrl + uri;
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
        if (TicketsListView.SelectedItem is TicketRowViewModel { Ticket: { } ticket })
        {
            // A draft belongs to the ticket it was written on. The box used to
            // persist across selections, so half-written text followed you to
            // the next ticket and Post sent it there — the worst kind of bug,
            // because it looks like it worked.
            StashCommentDraft();

            _selectedTicket = ticket;
            RestoreCommentDraft(ticket.Id);

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

    /// <summary>
    /// Keep the current ticket's unsent comment so it comes back if the
    /// operator returns to it. Losing typed work on a stray click is worse than
    /// carrying a few strings.
    /// </summary>
    private void StashCommentDraft()
    {
        if (_selectedTicket is not { } previous) return;

        var draft = CommentTextBox.Text?.Trim();

        if (string.IsNullOrEmpty(draft)) _commentDrafts.Remove(previous.Id);
        else _commentDrafts[previous.Id] = CommentTextBox.Text ?? "";
    }

    private void RestoreCommentDraft(int ticketId)
    {
        CommentTextBox.Text = _commentDrafts.GetValueOrDefault(ticketId) ?? "";
    }

    /// <summary>
    /// Seed the comment box with the selected entry, quoted.
    ///
    /// Quote rather than Reply because the TDX Web API has no route for a
    /// threaded reply — posting one creates another top-level entry with the
    /// named parent's reply count still zero. Quoting is the closest thing the
    /// API actually supports, so it is what the button offers.
    /// </summary>
    private void OnQuoteFeedEntry(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FeedDisplayItem entry }) return;

        var quoted = BuildQuote(entry);
        var existing = CommentTextBox.Text ?? "";

        // Append rather than replace — quoting a second comment into a reply
        // already being written must not discard what is there.
        CommentTextBox.Text = string.IsNullOrWhiteSpace(existing)
            ? quoted
            : existing.TrimEnd() + "\n\n" + quoted;

        CommentTextBox.Focus();
        CommentTextBox.CaretIndex = CommentTextBox.Text.Length;
    }

    internal static string BuildQuote(FeedDisplayItem entry)
    {
        var author = string.IsNullOrWhiteSpace(entry.CreatedFullName) ? "someone" : entry.CreatedFullName;

        // The body is already stripped for display, so quoting it gives plain
        // text rather than a block of markup nobody wants in their comment.
        var quoted = string.Join("\n",
            (entry.StrippedBody ?? "").Split('\n').Select(line => $"> {line.TrimEnd()}"));

        return $"{author} wrote:\n{quoted}";
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
        DetailModifiedText.Text = FormatDate(ticket.ModifiedDate as DateTime?);

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
        // Filter feed based on current filter mode
        IEnumerable<TdxFeedEntry> filteredFeed = _feedFilter switch
        {
            "Comments" => _ticketFeed.Where(f => f.CreatedFullName != "System" && !string.IsNullOrWhiteSpace(f.Body)),
            "Activity" => _ticketFeed.Where(f => f.CreatedFullName != "System"),
            "All" => _ticketFeed,
            _ => _ticketFeed.Where(f => f.CreatedFullName != "System" && !string.IsNullOrWhiteSpace(f.Body))
        };

        var feedList = filteredFeed.ToList();
        
        if (feedList.Count > 0)
        {
            FeedHeaderText.Text = $"Activity ({feedList.Count})";
            
            // Convert feed entries for display
            var displayFeed = feedList.Take(20).Select(f => new FeedDisplayItem
            {
                CreatedFullName = f.CreatedFullName ?? "Unknown",
                FormattedDate = FormatDate(f.CreatedDate),
                StrippedBody = DecodeHtml(f.Body ?? ""),
                IsPrivate = f.IsPrivate
            }).ToList();
            
            FeedItemsControl.ItemsSource = displayFeed;
            FeedSection.Visibility = Visibility.Visible;
        }
        else
        {
            FeedHeaderText.Text = "Activity";
            FeedItemsControl.ItemsSource = null;
            FeedSection.Visibility = Visibility.Visible;
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
        if (!string.IsNullOrEmpty(ticket.RequestorName) && ticket.RequestorUid != null && ticket.RequestorUid != Guid.Empty)
        {
            options.Add(($"user:{ticket.RequestorUid}", $"Requestor: {ticket.RequestorName}"));
        }
        
        // Responsible person (if different from requestor)
        if (!string.IsNullOrEmpty(ticket.ResponsibleFullName) && ticket.ResponsibleUid != null && ticket.ResponsibleUid != Guid.Empty)
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
            await _tdxService.AddCommentAsync(
                _selectedTicket.Id,
                comment,
                PrivateCheckBox.IsChecked == true);

            ShowActionMessage("Comment added successfully");
            CommentTextBox.Text = "";

            // Drop the stashed copy too, or navigating away and back would
            // resurrect a comment that was already posted.
            _commentDrafts.Remove(_selectedTicket.Id);

            // Refresh feed
            _ticketFeed = await _tdxService.GetTicketFeedAsync(_selectedTicket.Id);
            UpdateFeedPanel();
        }
        catch (TdxCommentException ex)
        {
            // Only clear the box on success — the comment is the operator's
            // work, and losing it to a rejected post is worse than the failure.
            ShowActionMessage(ex.Message, isError: true);
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

    private static string FormatDate(DateTime? dateTime)
    {
        if (dateTime == null) return "-";
        
        var dt = dateTime.Value;
        var localDt = dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt;
        return localDt.ToString("MMM d 'at' h:mm tt");
    }

    private static string FormatDate(DateTime dateTime)
    {
        var localDt = dateTime.Kind == DateTimeKind.Utc ? dateTime.ToLocalTime() : dateTime;
        return localDt.ToString("MMM d 'at' h:mm tt");
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

    /// <summary>
    /// Cards as an outline, so children indent under their parent here too.
    /// A child whose parent sits in another status column has no parent to nest
    /// under and renders as a root — which is the honest reading, since the
    /// parent genuinely is not in this column.
    /// </summary>
    public List<TicketRowViewModel> Rows { get; set; } = new();
}
