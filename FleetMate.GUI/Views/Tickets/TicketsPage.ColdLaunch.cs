using System.Windows;
using FleetMate.Core.Services.Tickets;
using Serilog;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>
/// A link can open the app before TeamDynamix sign-in finishes. The list, the
/// linked ticket and its feed all fail then, so this holds what failed, loads
/// it again once sign-in completes, and says why the list is empty meanwhile.
/// </summary>
public partial class TicketsPage
{
    /// <summary>A linked ticket that could not be fetched before sign-in; opened once signed in.</summary>
    private int? _ticketAwaitingSignIn;

    /// <summary>Why the last list load failed, shown in place of an empty list.</summary>
    private string? _ticketsLoadError;

    /// <summary>Sign-in finished during a load; load again if that one came back empty or failed.</summary>
    private bool _reloadAfterLoad;

    /// <summary>
    /// Sign-in completed: open a linked ticket that was waiting for it, or
    /// reload the open ticket, its feed, workflow and tasks, which may have
    /// failed before the session existed.
    /// </summary>
    private async Task ResumeAfterSignInAsync()
    {
        if (_ticketAwaitingSignIn is { } linked)
        {
            _ticketAwaitingSignIn = null;
            Log.Information("[tickets] Signed in; opening linked ticket #{Id}", linked);
            ShowActionMessage($"Signed in; opening ticket #{linked}.");
            await OpenLinkedTicketAsync(linked);
            return;
        }
        if (_selectedTicket is { } open && _detailPanelVisible)
            await LoadTicketDetailAsync(open.Id);
    }

    /// <summary>Hold a linked ticket until sign-in, if that is why it could not be fetched.</summary>
    private bool HoldLinkedTicket(int ticketId)
    {
        if (_tdxService == null || !TicketsPageSync.HoldLinkForSignIn(_tdxService.ShouldAttemptSso, _tdxService.IsSsoAuthenticated))
            return false;
        _ticketAwaitingSignIn = ticketId;
        Log.Information("[tickets] Ticket #{Id} opens once TeamDynamix sign-in finishes", ticketId);
        ShowActionMessage($"Ticket #{ticketId} opens once TeamDynamix sign-in finishes.");
        return true;
    }

    /// <summary>Show, over the list and board, why nothing is listed.</summary>
    private void UpdateEmptyState()
    {
        if (!IsInitialized || EmptyStatePanel == null) return;

        var reason = _tdxService == null || _app == null || _isInboxView
            ? TicketsEmptyReason.None
            : TicketsPageSync.EmptyReason(
                loadingShown: LoadingPanel.Visibility == Visibility.Visible,
                ticketCount: _allTickets.Count,
                listedCount: _filteredTickets.Count,
                ssoEnabled: _tdxService.ShouldAttemptSso,
                signedIn: _tdxService.IsSsoAuthenticated,
                loadError: _ticketsLoadError,
                searching: !string.IsNullOrWhiteSpace(SearchBox?.Text),
                assignedToMe: _assignedToMe && _me != null);

        EmptyStatePanel.Visibility = reason == TicketsEmptyReason.None ? Visibility.Collapsed : Visibility.Visible;
        if (reason == TicketsEmptyReason.None) return;

        var narrowed = _filters.HasActiveFilters || _app?.TicketDatePreset != TicketDateRangePreset.CurrentTerm;
        (string glyph, string title, string description) = reason switch
        {
            TicketsEmptyReason.NotSignedIn => ("", "Not Signed In", "Sign in to TeamDynamix to see tickets."),
            TicketsEmptyReason.LoadFailed => ("", "Tickets Didn't Load", _ticketsLoadError ?? ""),
            TicketsEmptyReason.NoTickets => ("", "No Tickets", "There are no tickets in this date range."),
            TicketsEmptyReason.Search => ("", "No Results", $"No tickets match “{SearchBox?.Text?.Trim()}”."),
            TicketsEmptyReason.AssignedToMe => ("", "No Matching Tickets", "No open tickets are assigned to you."),
            _ => ("", "No Matching Tickets", "No tickets match the current filters."),
        };
        EmptyStateIcon.Glyph = glyph;
        EmptyStateTitle.Text = title;
        EmptyStateDescription.Text = description;

        var filtering = reason is TicketsEmptyReason.AssignedToMe or TicketsEmptyReason.Filters;
        Show(EmptyStateSignInButton, reason == TicketsEmptyReason.NotSignedIn);
        Show(EmptyStateRetryButton, reason is TicketsEmptyReason.NotSignedIn or TicketsEmptyReason.LoadFailed);
        Show(EmptyStateShowAllButton, reason == TicketsEmptyReason.AssignedToMe);
        Show(EmptyStateClearFiltersButton, filtering && narrowed);
        Show(EmptyStateShowClosedButton, filtering && !_showClosed);

        static void Show(UIElement element, bool visible) =>
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnEmptyStateRetryClicked(object sender, RoutedEventArgs e)
    {
        if (_app == null) return;
        _app.CachedTickets.Clear();
        await LoadTicketsAsync();
    }

    private void OnEmptyStateShowAllClicked(object sender, RoutedEventArgs e) =>
        AssignedToMeToggle.IsChecked = false;

    private void OnEmptyStateShowClosedClicked(object sender, RoutedEventArgs e) =>
        ShowClosedCheckBox.IsChecked = true;

    /// <summary>Every filter cleared and the date range back to the current term.</summary>
    private void OnEmptyStateClearFiltersClicked(object sender, RoutedEventArgs e)
    {
        _filters.ClearAll();
        if (DateRangeComboBox.ItemsSource is IEnumerable<DateRangeOption> options
            && options.FirstOrDefault(o => o.Preset == TicketDateRangePreset.CurrentTerm) is { } term)
            DateRangeComboBox.SelectedItem = term;
    }
}
