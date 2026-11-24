using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FleetMate.GUI.Views.Shared;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using Serilog;

namespace FleetMate.GUI.Views.Tickets;

// The Inbox view: every comment of the last 90 days, on any ticket, that
// notified the signed-in TDX person, as a sidebar. Built by TicketInboxScanner and saved per
// person; read state lives only on this PC, since TDX keeps none.
public partial class TicketsPage
{
    private static readonly TimeSpan InboxRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InboxReloadInterval = TimeSpan.FromMinutes(1);

    private TicketInboxStore _inboxStore = new();
    private string? _inboxService;
    private IDisposable? _inboxReadWatcher;
    private DispatcherTimer? _inboxReadDebounce;
    private Window? _inboxActivationWindow;
    private bool _inboxPreferencesAttached;
    private Task _inboxWriteTail = Task.CompletedTask;
    private Task? _inboxReadReloadTask;
    private bool _inboxReadReloadAgain;
    private Task? _inboxDiskReloadTask;
    private bool _inboxDiskReloadAgain;
    private TicketInboxPerson? _inboxPerson;
    private TicketInboxState? _inboxState;
    private DispatcherTimer? _inboxTimer;
    /// <summary>Reads the saved inbox every minute, so the background helper's finds show up.</summary>
    private DispatcherTimer? _inboxReloadTimer;
    /// <summary>The last pass found the background helper scanning, so it reloaded instead.</summary>
    private bool _inboxScannedElsewhere;
    private CancellationTokenSource? _inboxPassCts;
    private Task? _inboxPass;
    private DateTime _inboxLastPassUtc = DateTime.MinValue;
    private string? _inboxError;
    private bool _inboxSelecting;
    private bool _inboxSidebarVisible = UserPreferences.TicketsInboxSidebarVisible;
    private double _inboxSidebarWidth = UserPreferences.TicketsInboxSidebarWidth ?? TicketsToolbarRules.InboxSidebarDefaultWidth;

    /// <summary>The signed-in person was found, changed or lost: load their saved inbox.</summary>
    private void OnInboxPersonChanged()
    {
        var person = _tdxService != null && _me?.Uid is { } uid && uid != Guid.Empty
            ? new TicketInboxPerson(uid, _me.FullName ?? _me.DisplayName, _me.PrimaryEmail)
            : null;
        var service = person == null ? null : _tdxService?.InboxServiceUrl;
        if (person?.Uid == _inboxPerson?.Uid && service == _inboxService) return;
        _inboxReadWatcher?.Dispose();
        _inboxReadWatcher = null;
        _inboxService = service;
        _inboxStore = service == null ? new() : new(serviceUrl: service, legacyServiceKey: _tdxService?.IsSsoAuthenticated == true ? _app?.Config?.TicketInboxLegacyServiceKey : null);

        CancelInboxPass();
        _inboxPerson = person;
        _inboxState = person == null ? null : new TicketInboxState();
        if (person != null && service != null)
        {
            _inboxReadDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _inboxReadDebounce.Stop();
            _inboxReadDebounce.Tick -= OnInboxReadDebounce;
            _inboxReadDebounce.Tick += OnInboxReadDebounce;
            _inboxReadWatcher = _inboxStore.WatchRead(person.Uid, () => Dispatcher.BeginInvoke(() =>
            {
                if (person.Uid != _inboxPerson?.Uid || service != _inboxService) return;
                _inboxReadDebounce.Stop();
                _inboxReadDebounce.Start();
            }));
        }
        _inboxLastPassUtc = DateTime.MinValue;
        _inboxError = null;
        _inboxScannedElsewhere = false;
        RenderInbox();

        if (_inboxReloadTimer == null)
        {
            _inboxReloadTimer = new DispatcherTimer { Interval = InboxReloadInterval };
            _inboxReloadTimer.Tick += (_, _) => _ = ReloadInboxFromDisk();
        }
        if (person == null) _inboxReloadTimer.Stop(); else _inboxReloadTimer.Start();

        // Scan silently from launch, whichever view is showing.
        StartInboxPass();
        UpdateInboxTimer();
    }

    /// <summary>Coalesced disk I/O never blocks the Dispatcher or replaces a running scan's items.</summary>
    private Task ReloadInboxFromDisk()
    {
        if (_inboxPass is { IsCompleted: false }) return ReloadInboxReadMarks();
        _inboxDiskReloadAgain = true;
        return _inboxDiskReloadTask is { IsCompleted: false } running ? running
            : _inboxDiskReloadTask = ReloadInboxFromDiskAsync();
    }

    private async Task ReloadInboxFromDiskAsync()
    {
        do
        {
            _inboxDiskReloadAgain = false;
            if (_inboxPerson is not { } person) return;
            var store = _inboxStore;
            var service = _inboxService;
            try
            {
                var state = await Task.Run(() => store.Load(person.Uid));
                if (!ReferenceEquals(store, _inboxStore) || person.Uid != _inboxPerson?.Uid || service != _inboxService) continue;
                if (_inboxPass is { IsCompleted: false }) ApplyInboxReadMarks(state.ReadMarks);
                else { _inboxState = state; RenderInbox(); }
            }
            catch (Exception ex) { Log.Warning(ex, "Ticket Inbox cache could not be reloaded"); }
        } while (_inboxDiskReloadAgain);
    }

    private void OnInboxReadDebounce(object? sender, EventArgs e)
    {
        _inboxReadDebounce?.Stop();
        _ = ReloadInboxReadMarks();
    }

    private Task ReloadInboxReadMarks()
    {
        _inboxReadReloadAgain = true;
        return _inboxReadReloadTask is { IsCompleted: false } running ? running
            : _inboxReadReloadTask = ReloadInboxReadMarksAsync();
    }

    private async Task ReloadInboxReadMarksAsync()
    {
        do
        {
            _inboxReadReloadAgain = false;
            if (_inboxPerson is not { } person || _inboxState == null) return;
            var store = _inboxStore;
            var service = _inboxService;
            try
            {
                var marks = await Task.Run(() => store.LoadReadMarks(person.Uid));
                if (ReferenceEquals(store, _inboxStore) && person.Uid == _inboxPerson?.Uid && service == _inboxService)
                    ApplyInboxReadMarks(marks);
            }
            catch (Exception ex) { Log.Warning(ex, "Ticket Inbox read metadata could not be reloaded"); }
        } while (_inboxReadReloadAgain);
    }

    private void ApplyInboxReadMarks(TicketInboxReadMarks marks)
    {
        if (_inboxState is not { } state) return;
        state.ApplyReadMarks(marks);
        RefreshInboxReadRows(state);
    }

    private void RefreshInboxReadRows(TicketInboxState state)
    {
        UpdateInboxBadge(state.UnreadCount);
        if (InboxListView.ItemsSource is IEnumerable<InboxRow> rows)
            foreach (var row in rows) row.SetRead(state.IsRead(row.Item));
        InboxMarkAllReadButton.IsEnabled = state.UnreadCount > 0;
    }

    private void OnInboxWindowActivated(object? sender, EventArgs e) => _ = ReloadInboxFromDisk();
    private void OnInboxBadgePreferenceChanged() => Dispatcher.BeginInvoke(() => UpdateInboxBadge(_inboxState?.UnreadCount ?? 0));

    /// <summary>While the sidebar is showing, refresh when due and every five minutes.</summary>
    private void UpdateInboxTimer()
    {
        if (!_inboxSidebarVisible || _inboxPerson == null)
        {
            _inboxTimer?.Stop();
            return;
        }

        if (_inboxTimer == null)
        {
            _inboxTimer = new DispatcherTimer { Interval = InboxRefreshInterval };
            _inboxTimer.Tick += (_, _) => StartInboxPass();
        }
        _inboxTimer.Start();
        if (DateTime.UtcNow - _inboxLastPassUtc >= InboxRefreshInterval) StartInboxPass();
    }

    private void StopInbox()
    {
        _inboxTimer?.Stop();
        _inboxReloadTimer?.Stop();
        CancelInboxPass();
        _inboxReadWatcher?.Dispose();
        _inboxReadWatcher = null;
        _inboxPerson = null;
        _inboxState = null;
        _inboxService = null;
        _inboxReadDebounce?.Stop();
        if (_inboxActivationWindow != null) _inboxActivationWindow.Activated -= OnInboxWindowActivated;
        _inboxActivationWindow = null;
        UserPreferences.Changed -= OnInboxBadgePreferenceChanged;
        _inboxPreferencesAttached = false;
    }

    private void CancelInboxPass()
    {
        _inboxPassCts?.Cancel();
        _inboxPassCts = null;
        _inboxPass = null;
    }

    private void StartInboxPass()
    {
        if (_inboxPass is { IsCompleted: false }) return;
        if (_tdxService == null || _inboxPerson is not { } person)
        {
            RenderInbox();
            return;
        }

        // Start from what is on disk: the background helper may have scanned last.
        var store = _inboxStore;
        var cts = _inboxPassCts = new CancellationTokenSource();
        _inboxPass = RunInboxPassAsync(person, store, cts);
    }

    private async Task RunInboxPassAsync(TicketInboxPerson person, TicketInboxStore store, CancellationTokenSource cts)
    {
        TicketInboxState? state = null;
        var account = _tdxService?.AuthenticatedUserId;
        var progress = new Progress<TicketInboxProgress>(p =>
        {
            if (cts.IsCancellationRequested) return;
            InboxStatusText.Text = p.Total == 0
                ? "Checking for new comments…"
                : $"Scanning {Math.Min(p.Done + 1, p.Total)} of {p.Total} tickets";
        });

        _inboxError = null;
        InboxProgressRing.Visibility = Visibility.Visible;
        InboxProgressRing.IsActive = true;
        InboxRefreshButton.IsEnabled = false;
        InboxStatusText.Text = "Checking for new comments…";
        try
        {
            state = await Task.Run(() => store.Load(person.Uid), cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(store, _inboxStore) || person.Uid != _inboxPerson?.Uid) return;
            _inboxState = state;
            RenderInboxItems();
            var scanner = new TicketInboxScanner(_tdxService!, store, stillCurrent: () => ReferenceEquals(store, _inboxStore)
                && string.Equals(account, _tdxService?.AuthenticatedUserId, StringComparison.OrdinalIgnoreCase));
            var ran = await scanner.TryRunPassAsync(person, state, progress, () =>
            {
                if (!cts.IsCancellationRequested && ReferenceEquals(state, _inboxState)) RenderInboxItems();
            }, cts.Token);
            _inboxScannedElsewhere = !ran;
            _inboxLastPassUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested
            || !string.Equals(account, _tdxService?.AuthenticatedUserId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested || !ReferenceEquals(store, _inboxStore)) return;
            Log.Warning(ex, "Ticket inbox pass failed");
            _inboxError = ex.Message;
            _inboxLastPassUtc = DateTime.UtcNow;
        }
        finally
        {
            if (ReferenceEquals(_inboxPassCts, cts))
            {
                InboxProgressRing.IsActive = false;
                InboxProgressRing.Visibility = Visibility.Collapsed;
                InboxRefreshButton.IsEnabled = true;
            }
        }

        if (state != null && ReferenceEquals(state, _inboxState)) RenderInbox();
    }

    private void RenderInbox()
    {
        RenderInboxItems();
        if (_inboxPass is { IsCompleted: false }) return;

        InboxStatusText.Text = _tdxService == null
            ? "TeamDynamix is not configured."
            : _inboxPerson == null
                ? "Sign in to TeamDynamix to build your inbox. It needs your TeamDynamix person, found from your sign-in email."
                : _inboxError != null
                    ? $"Last check failed: {_inboxError}"
                    : _inboxScannedElsewhere && _inboxState?.ScannedThrough is { } helperThrough
                        ? $"Kept current by the background helper. Checked {helperThrough.ToLocalTime():MMM d 'at' h:mm tt}."
                    : _inboxState?.ScannedThrough is { } through
                        ? $"Checked {through.ToLocalTime():MMM d 'at' h:mm tt}. Refreshes every 5 minutes."
                        : "Not checked yet.";
    }

    private void RenderInboxItems()
    {
        var state = _inboxState;
        var unread = state?.UnreadCount ?? 0;
        UpdateInboxBadge(unread);

        // The envelope remains available while scanning or signed out; status lives in the sidebar.
        InboxButtonHost.Visibility = Visibility.Visible;
        InboxMarkAllReadButton.IsEnabled = unread > 0;

        var rows = (state?.Items ?? new List<TicketInboxItem>())
            .OrderByDescending(i => i.Created)
            .Select(i => new InboxRow(i, state!.IsRead(i)))
            .ToList();

        // A refresh that changed nothing keeps the rows, so the selection,
        // focus and scroll position stay exactly where they were.
        if (InboxListView.ItemsSource is IReadOnlyList<InboxRow> current && current.SequenceEqual(rows, InboxRowContent.Instance))
        {
            foreach (var (row, next) in current.Zip(rows)) row.SetRead(next.IsRead);
        }
        else
        {
            var selectedFeedId = (InboxListView.SelectedItem as InboxRow)?.Item.FeedId;
            var hadFocus = InboxListView.IsKeyboardFocusWithin;
            var scroller = FindInboxScroller(InboxListView);
            var offset = scroller?.VerticalOffset ?? 0;
            _inboxSelecting = true;
            InboxListView.ItemsSource = rows;
            InboxListView.SelectedItem = selectedFeedId is { } id ? rows.FirstOrDefault(r => r.Item.FeedId == id) : null;
            _inboxSelecting = false;
            if (scroller != null || hadFocus)
                Dispatcher.BeginInvoke(() =>
                {
                    scroller?.ScrollToVerticalOffset(offset);
                    if (hadFocus && InboxListView.SelectedItem is { } selected
                        && InboxListView.ItemContainerGenerator.ContainerFromItem(selected) is ListViewItem container)
                        container.Focus();
                    else if (hadFocus) InboxListView.Focus();
                }, DispatcherPriority.Loaded);
        }

        InboxEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InboxEmptyText.Text = _tdxService == null
            ? "TeamDynamix is not configured."
            : _inboxPerson == null
                ? "Sign in to TeamDynamix to see comments that notified you."
                : _inboxError != null
                    ? "The inbox could not be checked. Refresh to try again."
                    : state?.ScannedThrough == null
                        ? "Checking recent comments. Notifications will appear here."
                        : "No comments have notified you in the last 90 days.";
    }

    private void UpdateInboxBadge(int unread)
    {
        var enabled = UserPreferences.TicketsBadgesEnabled(_app?.Config?.TicketsBadgesEnabled
            ?? FleetMate.Core.Config.AppEdition.Current.IsTicketsOnly);
        var visibleUnread = enabled ? unread : 0;
        InboxBadgeText.Text = TicketInboxBadge.Label(visibleUnread);
        InboxBadge.Visibility = visibleUnread > 0 ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetItemStatus(InboxButton, enabled ? TicketInboxBadge.AccessibleValue(unread) : "");
        InboxButton.ToolTip = TicketsToolbarRules.InboxToolTip(visibleUnread);
    }

    private void OnInboxRefreshClicked(object sender, RoutedEventArgs e) => StartInboxPass();

    private async void OnInboxMarkAllReadClicked(object sender, RoutedEventArgs e)
    {
        if (_inboxState is not { } state || _inboxPerson is not { } person) return;
        await SaveReadMarksAsync(person, state, state.Items.Select(item => item.FeedId), true);
        if (ReferenceEquals(state, _inboxState)) RenderInboxItems();
    }

    private void OnInboxButtonClicked(object sender, RoutedEventArgs e) =>
        SetInboxSidebarVisible(InboxButton.IsChecked == true, moveFocus: false);

    private void SetInboxSidebarVisible(bool visible, bool moveFocus)
    {
        _inboxSidebarVisible = visible;
        UserPreferences.SetTicketsInboxSidebarVisible(visible);
        ApplyInboxSidebar();
        if (visible) { RenderInbox(); UpdateInboxTimer(); }
        else _inboxTimer?.Stop();
        if (!moveFocus) return;
        if (visible) Dispatcher.BeginInvoke(() => Keyboard.Focus(InboxListView), DispatcherPriority.Input);
        else Keyboard.Focus(InboxButton);
    }

    private void ApplyInboxSidebar()
    {
        InboxButton.IsChecked = _inboxSidebarVisible;
        var visibility = _inboxSidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        InboxSidebar.Visibility = visibility;
        InboxSidebarSplitter.Visibility = visibility;
        InboxSidebarColumn.MinWidth = _inboxSidebarVisible ? TicketsToolbarRules.InboxSidebarMinWidth : 0;
        InboxSidebarColumn.MaxWidth = _inboxSidebarVisible ? TicketsToolbarRules.InboxSidebarMaxWidth : double.PositiveInfinity;
        ListPanelColumn.MinWidth = _inboxSidebarVisible ? TicketsToolbarRules.ListMinWidthBesideInbox : 480;
        FitInboxSidebar();
    }

    /// <summary>Narrow the sidebar for the window without changing the width that was dragged.</summary>
    private void FitInboxSidebar()
    {
        var width = _inboxSidebarVisible
            ? TicketsToolbarRules.InboxSidebarWidth(_inboxSidebarWidth, ActualWidth, _detailPanelVisible)
            : 0;
        InboxSidebarColumn.Width = new GridLength(width);
    }

    private void OnInboxSidebarDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _inboxSidebarWidth = Math.Clamp(InboxSidebarColumn.ActualWidth,
            TicketsToolbarRules.InboxSidebarMinWidth, TicketsToolbarRules.InboxSidebarMaxWidth);
        UserPreferences.SetTicketsInboxSidebarWidth(_inboxSidebarWidth);
        FitInboxSidebar();
    }

    private static ScrollViewer? FindInboxScroller(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindInboxScroller(child) is { } nested) return nested;
        }
        return null;
    }

    private async void OnInboxItemClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left
            && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(InboxListView, source) is ListViewItem)
            await ActivateInboxSelectionAsync();
    }

    private async void OnInboxItemsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ActivateInboxSelectionAsync();
    }

    internal async Task ActivateInboxSelectionAsync()
    {
        if (_inboxSelecting || InboxListView.SelectedItem is not InboxRow row) return;
        var ticket = _allTickets.FirstOrDefault(t => t.Id == row.Item.TicketId)
                     ?? (_tdxService == null ? null : await _tdxService.GetTicketAsync(row.Item.TicketId));
        if (ticket == null)
        {
            ShowActionMessage($"Ticket {row.Item.TicketId} could not be opened.");
            return;
        }
        await OpenTicketInDetailAsync(ticket);
    }

    private async void OnInboxMarkUnreadClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: InboxRow row } && _inboxState is { } state && _inboxPerson is { } person)
        {
            await SaveReadMarksAsync(person, state, new[] { row.Item.FeedId }, false);
            if (ReferenceEquals(state, _inboxState)) await ReloadInboxReadMarks();
        }
    }

    /// <summary>
    /// A ticket was shown, by any route: the comments on it up to now are read,
    /// and one that arrives later comes in unread.
    /// </summary>
    private void RecordInboxTicketOpened(int ticketId)
    {
        if (InboxListView.SelectedItem is InboxRow { Item.TicketId: var selectedTicket } && selectedTicket != ticketId)
            InboxListView.SelectedItem = null;
        if (_inboxState is not { } state || _inboxPerson is not { } person) return;
        var opened = DateTime.UtcNow;
        var ids = state.Items.Where(i => i.TicketId == ticketId).Select(i => i.FeedId).ToArray();
        state.MarkTicketOpened(ticketId, ids, opened);
        RefreshInboxReadRows(state);
        _ = SaveInboxChangeAsync(person, state, store => store.RecordTicketOpened(person.Uid, ticketId, ids, opened));
    }

    /// <summary>Only the requested decisions are changed; peer decisions remain intact.</summary>
    private Task SaveReadMarksAsync(TicketInboxPerson person, TicketInboxState state, IEnumerable<int> ids, bool read)
    {
        var decisions = ids.ToArray();
        return SaveInboxChangeAsync(person, state, store => store.SetRead(person.Uid, decisions, read));
    }

    private async Task SaveInboxChangeAsync(TicketInboxPerson person, TicketInboxState state, Action<TicketInboxStore> change)
    {
        var store = _inboxStore;
        try
        {
            var previous = _inboxWriteTail;
            var write = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); }
                catch { /* A failed decision must not prevent later explicit decisions. */ }
                change(store);
            });
            _inboxWriteTail = write;
            await write;
            if (ReferenceEquals(store, _inboxStore) && ReferenceEquals(state, _inboxState) && person.Uid == _inboxPerson?.Uid)
                await ReloadInboxReadMarks();
        }
        catch (Exception ex) { Log.Warning(ex, "Ticket inbox read state could not be saved"); }
    }
}

/// <summary>Rows that would draw the same comment, ignoring read state.</summary>
internal sealed class InboxRowContent : IEqualityComparer<InboxRow>
{
    public static readonly InboxRowContent Instance = new();

    public bool Equals(InboxRow? a, InboxRow? b) =>
        a != null && b != null && a.Item.FeedId == b.Item.FeedId && a.Item.TicketId == b.Item.TicketId
        && a.Item.Created == b.Item.Created && a.Item.TicketTitle == b.Item.TicketTitle
        && a.Item.Author == b.Item.Author && a.Item.Body == b.Item.Body && a.Item.IsPrivate == b.Item.IsPrivate;

    public int GetHashCode(InboxRow row) => row.Item.FeedId;
}

/// <summary>One row of the Inbox list.</summary>
public sealed class InboxRow : System.ComponentModel.INotifyPropertyChanged
{
    public InboxRow(TicketInboxItem item, bool isRead)
    {
        Item = item;
        IsRead = isRead;
    }

    public TicketInboxItem Item { get; }
    public bool IsRead { get; private set; }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public void SetRead(bool read)
    {
        if (IsRead == read) return;
        IsRead = read;
        PropertyChanged?.Invoke(this, new(nameof(IsRead)));
        PropertyChanged?.Invoke(this, new(nameof(HeadingWeight)));
        PropertyChanged?.Invoke(this, new(nameof(UnreadVisibility)));
    }

    public string Heading => $"#{Item.TicketId} · {Item.TicketTitle ?? "Untitled"}";
    public FontWeight HeadingWeight => IsRead ? FontWeights.Normal : FontWeights.SemiBold;
    public string Byline => (Item.Author ?? "Unknown") + (Item.IsPrivate ? " · Private" : "");
    public string Preview => (Item.Body ?? "").Replace('\n', ' ');
    public string FormattedDate => Item.Created.ToLocalTime().ToString("MMM d 'at' h:mm tt");
    public Visibility UnreadVisibility => IsRead ? Visibility.Hidden : Visibility.Visible;
}
