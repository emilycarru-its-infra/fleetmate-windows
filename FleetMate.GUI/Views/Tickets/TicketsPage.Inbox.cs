using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using Serilog;

namespace FleetMate.GUI.Views.Tickets;

// The Inbox view: every comment of the last 90 days, on any ticket, that
// notified the signed-in TDX person. Built by TicketInboxScanner and saved per
// person; read state lives only on this PC, since TDX keeps none.
public partial class TicketsPage
{
    private static readonly TimeSpan InboxRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InboxReloadInterval = TimeSpan.FromMinutes(1);

    private readonly TicketInboxStore _inboxStore = new();
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
    private DateTime _inboxClosedAt;
    private Window? _inboxHostWindow;

    /// <summary>The signed-in person was found, changed or lost: load their saved inbox.</summary>
    private void OnInboxPersonChanged()
    {
        var person = _me?.Uid is { } uid && uid != Guid.Empty
            ? new TicketInboxPerson(uid, _me.FullName ?? _me.DisplayName, _me.PrimaryEmail)
            : null;
        if (person?.Uid == _inboxPerson?.Uid) return;

        CancelInboxPass();
        _inboxPerson = person;
        _inboxState = person == null ? null : _inboxStore.Load(person.Uid);
        _inboxLastPassUtc = DateTime.MinValue;
        _inboxError = null;
        _inboxScannedElsewhere = false;
        RenderInbox();

        if (_inboxReloadTimer == null)
        {
            _inboxReloadTimer = new DispatcherTimer { Interval = InboxReloadInterval };
            _inboxReloadTimer.Tick += (_, _) => ReloadInboxFromDisk();
        }
        if (person == null) _inboxReloadTimer.Stop(); else _inboxReloadTimer.Start();

        // Scan silently from launch, whichever view is showing.
        StartInboxPass();
    }

    /// <summary>Re-read the saved inbox, unless this window is mid-pass and holds the newest copy.</summary>
    private void ReloadInboxFromDisk()
    {
        if (_inboxPass is { IsCompleted: false } || _inboxPerson is not { } person) return;
        _inboxState = _inboxStore.Load(person.Uid);
        RenderInbox();
    }

    /// <summary>Inbox shown: refresh now if due, and every five minutes while it stays open.</summary>
    private void OnInboxVisibilityChanged()
    {
        if (!InboxPopup.IsOpen)
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
        RenderInbox();
        if (DateTime.UtcNow - _inboxLastPassUtc >= InboxRefreshInterval) StartInboxPass();
    }

    private void StopInbox()
    {
        InboxPopup.IsOpen = false;
        _inboxTimer?.Stop();
        _inboxReloadTimer?.Stop();
        CancelInboxPass();
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
        var state = _inboxState = _inboxStore.Load(person.Uid);
        var cts = _inboxPassCts = new CancellationTokenSource();
        _inboxPass = RunInboxPassAsync(person, state, cts);
    }

    private async Task RunInboxPassAsync(TicketInboxPerson person, TicketInboxState state, CancellationTokenSource cts)
    {
        var scanner = new TicketInboxScanner(_tdxService!, _inboxStore);
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
            var ran = await scanner.TryRunPassAsync(person, state, progress, () =>
            {
                if (!cts.IsCancellationRequested && ReferenceEquals(state, _inboxState)) RenderInboxItems();
            }, cts.Token);
            _inboxScannedElsewhere = !ran;
            _inboxLastPassUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
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

        if (ReferenceEquals(state, _inboxState)) RenderInbox();
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
        InboxBadgeText.Text = TicketInboxBadge.Label(unread);
        InboxBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetItemStatus(InboxButton, TicketInboxBadge.AccessibleValue(unread));
        InboxButton.ToolTip = TicketsToolbarRules.InboxToolTip(unread);

        // Nobody sees a partial inbox: the mode appears once this computer's
        // first full pass has finished, as the Mac's TicketInboxModel.isReady has it.
        var ready = state?.ScannedThrough != null;
        InboxButtonHost.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        if (!ready) InboxPopup.IsOpen = false;
        InboxMarkAllReadButton.IsEnabled = unread > 0;

        var rows = (state?.Items ?? new List<TicketInboxItem>())
            .OrderByDescending(i => i.Created)
            .Select(i => new InboxRow(i, state!.IsRead(i.FeedId)))
            .ToList();

        _inboxSelecting = true;
        InboxListView.ItemsSource = rows;
        InboxListView.SelectedItem = null;
        _inboxSelecting = false;

        InboxEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InboxEmptyText.Text = _inboxPerson == null
            ? "Your inbox appears once you are signed in to TeamDynamix."
            : "No comments have notified you in the last 90 days.";
    }

    private void OnInboxRefreshClicked(object sender, RoutedEventArgs e) => StartInboxPass();

    private void OnInboxMarkAllReadClicked(object sender, RoutedEventArgs e)
    {
        if (_inboxState is not { } state || _inboxPerson is not { } person) return;
        state.MarkAllRead();
        SaveReadMarks(person, state);
        RenderInboxItems();
    }

    private void OnInboxButtonClicked(object sender, RoutedEventArgs e)
    {
        if ((DateTime.UtcNow - _inboxClosedAt).TotalMilliseconds < 250)
        {
            InboxButton.IsChecked = false;
            return;
        }
        InboxPopup.IsOpen = InboxButton.IsChecked == true;
    }

    private void OnInboxPopupOpened(object? sender, EventArgs e)
    {
        InboxButton.IsChecked = true;
        _inboxHostWindow = Window.GetWindow(this);
        if (_inboxHostWindow != null)
        {
            _inboxHostWindow.SizeChanged += OnInboxHostLayoutChanged;
            _inboxHostWindow.LocationChanged += OnInboxHostLayoutChanged;
            _inboxHostWindow.StateChanged += OnInboxHostLayoutChanged;
        }
        UpdateInboxPopoverSize();
        OnInboxVisibilityChanged();
        Dispatcher.BeginInvoke(() =>
        {
            if (InboxPopup.IsOpen) Keyboard.Focus(InboxListView);
        }, DispatcherPriority.Input);
    }

    private void OnInboxPopupClosed(object? sender, EventArgs e)
    {
        _inboxClosedAt = DateTime.UtcNow;
        InboxButton.IsChecked = false;
        if (_inboxHostWindow != null)
        {
            _inboxHostWindow.SizeChanged -= OnInboxHostLayoutChanged;
            _inboxHostWindow.LocationChanged -= OnInboxHostLayoutChanged;
            _inboxHostWindow.StateChanged -= OnInboxHostLayoutChanged;
            _inboxHostWindow = null;
        }
        OnInboxVisibilityChanged();
    }

    private void OnInboxHostLayoutChanged(object? sender, EventArgs e)
    {
        if (_inboxHostWindow?.WindowState == WindowState.Minimized) { InboxPopup.IsOpen = false; return; }
        UpdateInboxPopoverSize();
        var offset = InboxPopup.HorizontalOffset;
        InboxPopup.HorizontalOffset = offset + 0.5;
        InboxPopup.HorizontalOffset = offset;
    }

    private void UpdateInboxPopoverSize()
    {
        if ((_inboxHostWindow ?? Window.GetWindow(this)) is not { } window) return;
        var anchorBottom = InboxButton.TranslatePoint(new Point(0, InboxButton.ActualHeight), window).Y;
        InboxPopover.Width = Math.Clamp(window.ActualWidth - 32, 280, 560);
        InboxPopover.Height = Math.Max(180, window.ActualHeight - anchorBottom - InboxPopup.VerticalOffset);
    }

    private void OnInboxCloseClicked(object sender, RoutedEventArgs e)
    {
        InboxPopup.IsOpen = false;
        Keyboard.Focus(InboxButton);
    }

    private void OnInboxPopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        InboxPopup.IsOpen = false;
        Keyboard.Focus(InboxButton);
        e.Handled = true;
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
        InboxPopup.IsOpen = false;
        if (_inboxState is { } state && _inboxPerson is { } person && !state.IsRead(row.Item.FeedId))
        {
            state.MarkRead(row.Item.FeedId);
            SaveReadMarks(person, state);
            RenderInboxItems();
        }

        var ticket = _allTickets.FirstOrDefault(t => t.Id == row.Item.TicketId)
                     ?? (_tdxService == null ? null : await _tdxService.GetTicketAsync(row.Item.TicketId));
        if (ticket == null)
        {
            ShowActionMessage($"Ticket {row.Item.TicketId} could not be opened.");
            return;
        }
        await OpenTicketInDetailAsync(ticket);
    }

    /// <summary>Read marks go to their own file, which only the window writes.</summary>
    private void SaveReadMarks(TicketInboxPerson person, TicketInboxState state)
    {
        try { _inboxStore.SaveRead(person.Uid, state); }
        catch (Exception ex) { Log.Warning(ex, "Ticket inbox read state could not be saved"); }
    }
}

/// <summary>One row of the Inbox list.</summary>
public sealed class InboxRow
{
    public InboxRow(TicketInboxItem item, bool isRead)
    {
        Item = item;
        IsRead = isRead;
    }

    public TicketInboxItem Item { get; }
    public bool IsRead { get; }

    public string Heading => $"#{Item.TicketId} · {Item.TicketTitle ?? "Untitled"}";
    public FontWeight HeadingWeight => IsRead ? FontWeights.Normal : FontWeights.SemiBold;
    public string Byline => (Item.Author ?? "Unknown") + (Item.IsPrivate ? " · Private" : "");
    public string Preview => (Item.Body ?? "").Replace('\n', ' ');
    public string FormattedDate => Item.Created.ToLocalTime().ToString("MMM d 'at' h:mm tt");
    public Visibility UnreadVisibility => IsRead ? Visibility.Hidden : Visibility.Visible;
}
