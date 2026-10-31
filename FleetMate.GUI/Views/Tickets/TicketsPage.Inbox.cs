using System.Windows;
using System.Windows.Controls;
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

    private readonly TicketInboxStore _inboxStore = new();
    private TicketInboxPerson? _inboxPerson;
    private TicketInboxState? _inboxState;
    private DispatcherTimer? _inboxTimer;
    private CancellationTokenSource? _inboxPassCts;
    private Task? _inboxPass;
    private DateTime _inboxLastPassUtc = DateTime.MinValue;
    private string? _inboxError;
    private bool _inboxSelecting;

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
        RenderInbox();
        // Scan silently from launch, whichever view is showing.
        StartInboxPass();
    }

    /// <summary>Inbox shown: refresh now if due, and every five minutes while it stays open.</summary>
    private void OnInboxVisibilityChanged()
    {
        if (!_isInboxView)
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
        _inboxTimer?.Stop();
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
        if (_tdxService == null || _inboxPerson is not { } person || _inboxState is not { } state)
        {
            RenderInbox();
            return;
        }

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
            await scanner.RunPassAsync(person, state, progress, () =>
            {
                if (!cts.IsCancellationRequested && ReferenceEquals(state, _inboxState)) RenderInboxItems();
            }, cts.Token);
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
                    : _inboxState?.ScannedThrough is { } through
                        ? $"Checked {through.ToLocalTime():MMM d 'at' h:mm tt}. Refreshes every 5 minutes."
                        : "Not checked yet.";
    }

    private void RenderInboxItems()
    {
        var state = _inboxState;
        var unread = state?.UnreadCount ?? 0;
        InboxViewRadio.Content = unread > 0 ? $"Inbox ({unread})" : "Inbox";

        // Nobody sees a partial inbox: the mode appears once this computer's
        // first full pass has finished.
        var ready = state?.ScannedThrough != null;
        InboxViewRadio.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        if (!ready && InboxViewRadio.IsChecked == true) BoardViewRadio.IsChecked = true;
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
        SaveInbox(person, state);
        RenderInboxItems();
    }

    private async void OnInboxItemSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_inboxSelecting || InboxListView.SelectedItem is not InboxRow row) return;
        if (_inboxState is { } state && _inboxPerson is { } person && !state.IsRead(row.Item.FeedId))
        {
            state.MarkRead(row.Item.FeedId);
            SaveInbox(person, state);
            RenderInboxItems();
        }

        var ticket = _allTickets.FirstOrDefault(t => t.Id == row.Item.TicketId)
                     ?? (_tdxService == null ? null : await _tdxService.GetTicketAsync(row.Item.TicketId));
        if (ticket == null)
        {
            InboxStatusText.Text = $"Ticket {row.Item.TicketId} could not be opened.";
            return;
        }
        await OpenTicketInDetailAsync(ticket);
    }

    private void SaveInbox(TicketInboxPerson person, TicketInboxState state)
    {
        try { _inboxStore.Save(person.Uid, state); }
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
