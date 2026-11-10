using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using Serilog;

namespace FleetMate.GUI.Views.Tickets;

public partial class TicketsPage
{
    private ActiveTicketRefreshLoop? _ticketRefreshLoop;
    private Window? _ticketRefreshHost;
    private CancellationTokenSource? _ticketDetailRefreshCts;
    private DateTime _boardAttemptUtc = DateTime.MinValue;
    private DateTime _detailAttemptUtc = DateTime.MinValue;
    private DateTime _boardRetryUtc = DateTime.MinValue;
    private DateTime _detailRetryUtc = DateTime.MinValue;
    private bool _ticketRefreshSuspended;
    private bool _backgroundFeedRenderPending;
    private readonly Queue<int> _replyRefreshQueue = new();
    private readonly HashSet<int> _replyRefreshPending = new();

    private void AttachTicketFreshness()
    {
        _ticketRefreshSuspended = false;
        var host = Window.GetWindow(this);
        if (!ReferenceEquals(host, _ticketRefreshHost))
        {
            DetachTicketFreshness();
            _ticketRefreshHost = host;
            if (host != null)
            {
                host.Activated += OnTicketRefreshActivity;
                host.Deactivated += OnTicketRefreshActivity;
                host.StateChanged += OnTicketRefreshActivity;
                SystemEvents.PowerModeChanged += OnTicketRefreshPowerChanged;
            }
        }
        _ticketRefreshLoop ??= new ActiveTicketRefreshLoop((activation, ct) =>
            Dispatcher.InvokeAsync(() => RefreshActiveTicketsAsync(activation, ct)).Task.Unwrap());
        UpdateTicketFreshnessActivity();
    }
    private void DetachTicketFreshness()
    {
        _ticketRefreshLoop?.SetActive(false);
        _ticketDetailRefreshCts?.Cancel();
        if (_ticketRefreshHost is { } host)
        {
            host.Activated -= OnTicketRefreshActivity;
            host.Deactivated -= OnTicketRefreshActivity;
            host.StateChanged -= OnTicketRefreshActivity;
            SystemEvents.PowerModeChanged -= OnTicketRefreshPowerChanged;
            _ticketRefreshHost = null;
        }
    }
    private bool TicketRefreshIsActive => IsLoaded && IsVisible && !_ticketRefreshSuspended
        && _ticketRefreshHost is { IsActive: true, WindowState: not WindowState.Minimized };
    private void UpdateTicketFreshnessActivity() => _ticketRefreshLoop?.SetActive(TicketRefreshIsActive);
    private void OnTicketRefreshActivity(object? sender, EventArgs e) => UpdateTicketFreshnessActivity();
    private void OnTicketRefreshPowerChanged(object sender, PowerModeChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        _ticketRefreshSuspended = e.Mode == PowerModes.Suspend;
        UpdateTicketFreshnessActivity();
    });

    private async Task RefreshActiveTicketsAsync(bool activation, CancellationToken ct)
    {
        if (!TicketRefreshIsActive || _app == null || _tdxService?.IsSsoAuthenticated != true) return;
        var now = DateTime.UtcNow;
        var listAge = activation ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(5);
        var lastList = _app.LastTicketsRefreshUtc ?? DateTime.MinValue;
        if (!_isLoading && now >= _boardRetryUtc && now - lastList >= listAge
            && now - _boardAttemptUtc >= TimeSpan.FromMinutes(2))
        {
            _boardAttemptUtc = now;
            try
            {
                await _app.LoadBoardTicketsAsync(ct);
                _ticketsLoadError = null; _boardRetryUtc = DateTime.MinValue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _boardRetryUtc = DateTime.UtcNow.AddMinutes(5);
                Log.Debug(ex, "[tickets] Background board refresh deferred");
            }
        }
        ct.ThrowIfCancellationRequested();
        now = DateTime.UtcNow;
        if (now >= _detailRetryUtc && now - _detailAttemptUtc >= TimeSpan.FromSeconds(60))
            await RefreshActiveTicketDetailAsync(ct);
    }

    private async Task RefreshActiveTicketDetailAsync(CancellationToken ct)
    {
        if (_tdxService == null || _selectedTicket is not { } previous || _detailLoads.Current is not { } request
            || _postingComment || _savingEdits || _savingDescription || !TicketRefreshIsActive) return;
        _detailAttemptUtc = DateTime.UtcNow;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ticketDetailRefreshCts?.Cancel(); _ticketDetailRefreshCts = cancellation;
        var account = _tdxService.AuthenticatedUserId;
        try
        {
            var ticket = await _tdxService.GetTicketOrThrowAsync(previous.Id, cancellation.Token);
            var feed = await _tdxService.GetTicketFeedOrThrowAsync(previous.Id, cancellation.Token);
            if (!_detailLoads.IsCurrent(request) || _selectedTicket?.Id != previous.Id) return;
            var old = _ticketFeed.ToDictionary(entry => entry.Id);
            var visible = VisibleFeedForRefresh(feed).ToList();
            foreach (var entry in visible.Where(entry => (entry.RepliesCount ?? 0) > 0))
            {
                if ((ticket.ModifiedDate != previous.ModifiedDate || !old.TryGetValue(entry.Id, out var prior)
                    || prior.ReplyList.Count != entry.RepliesCount) && _replyRefreshPending.Add(entry.Id))
                    _replyRefreshQueue.Enqueue(entry.Id);
            }
            var fetched = new Dictionary<int, List<TdxFeedEntry>>();
            var fetchIds = new List<int>();
            while (fetchIds.Count < 2 && _replyRefreshQueue.TryDequeue(out var id))
            {
                _replyRefreshPending.Remove(id);
                if (!visible.Any(entry => entry.Id == id)) continue;
                fetchIds.Add(id);
            }
            foreach (var id in fetchIds)
            {
                var thread = await _tdxService.GetFeedEntryOrThrowAsync(id, cancellation.Token);
                fetched[id] = thread.ReplyList.ToList();
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (!TicketRefreshIsActive || !_detailLoads.IsCurrent(request) || _selectedTicket?.Id != previous.Id
                || !string.Equals(account, _tdxService.AuthenticatedUserId, StringComparison.OrdinalIgnoreCase)) return;
            var merged = TicketFeedRefresh.Merge(feed, _ticketFeed, fetched);
            ApplyBackgroundTicketRefresh(request, ticket, merged);
            _detailRetryUtc = DateTime.MinValue;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (cancellation.IsCancellationRequested || !_detailLoads.IsCurrent(request)) return;
            _detailRetryUtc = DateTime.UtcNow.AddMinutes(5);
            Log.Debug(ex, "[tickets] Background detail refresh deferred");
        }
        finally { if (ReferenceEquals(_ticketDetailRefreshCts, cancellation)) _ticketDetailRefreshCts = null; }
    }

    internal bool ApplyBackgroundTicketRefresh(TicketDetailLoadState.Request request, TdxTicket ticket, List<TdxFeedEntry> feed)
    {
        if (!_detailLoads.IsCurrent(request) || _selectedTicket is not { } previous || previous.Id != ticket.Id) return false;
        _selectedTicket = ticket;
        if (ticket.ModifiedDate != previous.ModifiedDate)
        {
            var scroller = TicketListScroller(TicketsListView);
            var offset = scroller?.VerticalOffset;
            UpdateDetailPanel(ticket);
            if (_app != null)
            {
                var index = _app.CachedTickets.FindIndex(item => item.Id == ticket.Id);
                if (index >= 0) _app.CachedTickets[index] = ticket;
                ApplyFiltersAndSort();
            }
            if (offset is { } top) Dispatcher.BeginInvoke(() => scroller?.ScrollToVerticalOffset(top), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        var changed = !TicketFeedRefresh.Same(_ticketFeed, feed);
        _ticketFeed = feed;
        if (_replyingToEntryId.HasValue) _backgroundFeedRenderPending |= changed;
        else if (changed || _backgroundFeedRenderPending) UpdateFeedPanel();
        return true;
    }

    private IEnumerable<TdxFeedEntry> VisibleFeedForRefresh(IEnumerable<TdxFeedEntry> feed) => (_feedFilter switch
    {
        "All" => feed,
        "Activity" => feed.Where(entry => entry.CreatedFullName != "System"),
        _ => feed.Where(entry => entry.CreatedFullName != "System" && !string.IsNullOrWhiteSpace(entry.Body)),
    }).Take(20);

    private void NoteManualDetailRefresh()
    {
        _ticketDetailRefreshCts?.Cancel();
        _detailAttemptUtc = DateTime.UtcNow; _detailRetryUtc = DateTime.MinValue;
        _replyRefreshQueue.Clear(); _replyRefreshPending.Clear();
    }

    private static ScrollViewer? TicketListScroller(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (TicketListScroller(child) is { } nested) return nested;
        }
        return null;
    }
}
