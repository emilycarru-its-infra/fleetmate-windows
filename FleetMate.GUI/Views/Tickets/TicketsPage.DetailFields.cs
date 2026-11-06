using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FleetMate.Core.Models.Tickets;

namespace FleetMate.GUI.Views.Tickets;

// The detail pane's header and fields, as on macOS: the actions that keep
// their labels, field picks held for Save, the Requestor picker and the
// Description editor.
public partial class TicketsPage
{
    // ── Header actions ────────────────────────────────────────────

    private bool _detailActionsIconOnly;
    private bool _detailActionsOwnRow;
    private double _detailActionsFullWidth;

    private void OnDetailHeaderSizeChanged(object sender, SizeChangedEventArgs e) => FitDetailActions();

    /// <summary>
    /// Place the ticket actions so they keep their labels. They sit on the
    /// number row when it has room, as on the Mac; otherwise they move to
    /// their own row under it, and drop to icons (their names stay in the
    /// tooltip and for screen readers) only when even that row is too narrow.
    /// </summary>
    private void FitDetailActions()
    {
        var rowWidth = DetailHeaderRow.ActualWidth;
        if (rowWidth <= 0) return;
        if (!_detailActionsIconOnly)
        {
            var measured = DetailActionsWidth();
            if (measured > 0) _detailActionsFullWidth = measured;
        }
        if (_detailActionsFullWidth <= 0) return;

        var editWidth = DetailEditButtonsPanel.Visibility == Visibility.Visible
            ? DetailEditButtonsPanel.DesiredSize.Width
            : 0;
        var inlineRoom = rowWidth - DetailIdentityPanel.ActualWidth
            - DetailIdentityPanel.Margin.Left - DetailIdentityPanel.Margin.Right - editWidth;

        var placement = TicketDetailActionsFit.Place(_detailActionsFullWidth, inlineRoom, rowWidth);
        PlaceDetailActions(ownRow: placement != DetailActionsFit.Inline);
        var iconOnly = placement == DetailActionsFit.OwnRowIconsOnly;
        if (iconOnly != _detailActionsIconOnly) ShowDetailActionLabels(!iconOnly);
    }

    private void PlaceDetailActions(bool ownRow)
    {
        if (ownRow == _detailActionsOwnRow) return;
        _detailActionsOwnRow = ownRow;
        Grid.SetRow(DetailActionsPanel, ownRow ? 1 : 0);
        Grid.SetColumn(DetailActionsPanel, ownRow ? 0 : 3);
        Grid.SetColumnSpan(DetailActionsPanel, ownRow ? 4 : 1);
        DetailActionsPanel.HorizontalAlignment = ownRow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        // The first button's leading gap lines up with the number row's edge.
        DetailActionsPanel.Margin = ownRow ? new Thickness(-6, 4, 0, 0) : new Thickness(0);
    }

    private double DetailActionsWidth() => DetailActionsPanel.Children.OfType<FrameworkElement>()
        .Where(c => c.Visibility != Visibility.Collapsed)
        .Sum(c => c.DesiredSize.Width);

    private void ShowDetailActionLabels(bool show)
    {
        _detailActionsIconOnly = !show;
        foreach (var button in DetailActionsPanel.Children.OfType<Button>())
        {
            if (button.Content is not Panel content) continue;
            foreach (var label in content.Children.OfType<TextBlock>())
                label.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (content.Children.OfType<ModernWpf.Controls.FontIcon>().FirstOrDefault() is { } icon)
                icon.Margin = show ? new Thickness(0, 0, 6, 0) : new Thickness(0);
            button.Padding = show ? new Thickness(10, 0, 10, 0) : new Thickness(8, 0, 8, 0);
        }
    }

    // ── Pending field edits ───────────────────────────────────────
    // Status, Priority, Classification, Form, Service, Type, Group and
    // Requestor picks, and Assign to me, Reassign and Unassign, wait here
    // until Save sends them as one PATCH, as on the Mac. They belong to one ticket: opening another drops them unasked, as
    // the Mac does, while a redraw of the same ticket keeps them.

    private TicketPendingEdits? _pendingEdits;
    private bool _savingEdits;

    /// <summary>True while ticket <paramref name="ticketId"/> holds unsaved picks.</summary>
    private bool HasPendingEdits(int ticketId) => _pendingEdits is { HasEdits: true } p && p.TicketId == ticketId;

    /// <summary>
    /// Follow a newer copy of the ticket on screen. The same ticket keeps its
    /// picks; another one starts with none.
    /// </summary>
    private void SyncPendingEdits(TdxTicket ticket)
    {
        if (_pendingEdits?.TicketId == ticket.Id) _pendingEdits.Rebase(ticket);
        else _pendingEdits = new TicketPendingEdits(ticket);
        UpdateEditButtons();
    }

    /// <summary>Drop the picks when a different ticket, or none, takes the pane.</summary>
    private void DropPendingEditsUnlessFor(int? ticketId)
    {
        if (_pendingEdits == null || _pendingEdits.TicketId == ticketId) return;
        _pendingEdits = null;
        UpdateEditButtons();
    }

    private void HoldEdit(string field, object value, string? requestorName = null)
    {
        if (_selectedTicket is not { } ticket) return;
        if (_pendingEdits?.TicketId != ticket.Id) _pendingEdits = new TicketPendingEdits(ticket);
        _pendingEdits.Set(field, value, requestorName);
        if (field == TicketDetailFields.Requestor) ShowRequestor(ticket);
        UpdateEditButtons();
    }

    private void ShowRequestor(TdxTicket ticket)
    {
        var picked = _pendingEdits?.TicketId == ticket.Id && _pendingEdits.IsEdited(TicketDetailFields.Requestor);
        DetailRequestorText.Text = picked ? _pendingEdits!.RequestorName ?? "-" : ticket.RequestorName ?? "-";
        DetailEmailText.Text = picked ? "-" : ticket.RequestorEmail ?? "-";
    }

    private void UpdateEditButtons()
    {
        var show = _selectedTicket is { } ticket && HasPendingEdits(ticket.Id);
        DetailEditButtonsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SaveEditsButton.IsEnabled = !_savingEdits;
        DiscardEditsButton.IsEnabled = !_savingEdits;
        DetailSaveProgress.IsActive = _savingEdits;
        DetailSaveProgress.Visibility = _savingEdits ? Visibility.Visible : Visibility.Collapsed;
        // Save and Discard take room on the number row; place the actions again.
        Dispatcher.BeginInvoke(FitDetailActions, DispatcherPriority.Loaded);
    }

    private void OnDiscardEditsClicked(object sender, RoutedEventArgs e) => DiscardEdits();

    private void DiscardEdits()
    {
        if (_selectedTicket is not { } ticket || _savingEdits) return;
        _pendingEdits?.Clear();
        UpdateDetailPanel(ticket);
        DetailStatusCombo.Focus();
    }

    private async void OnSaveEditsClicked(object sender, RoutedEventArgs e) => await SaveEditsAsync();

    /// <summary>
    /// Send every unsaved pick as one PATCH. The ticket is read again first,
    /// as on the Mac, so only fields that still differ from TeamDynamix are
    /// sent. The picks stay when TeamDynamix refuses them, so nothing is lost.
    /// </summary>
    private async Task SaveEditsAsync()
    {
        if (_savingEdits || _tdxService == null || _selectedTicket is not { } ticket) return;
        if (_pendingEdits is not { HasEdits: true } edits || edits.TicketId != ticket.Id) return;

        _savingEdits = true;
        UpdateEditButtons();
        try
        {
            ShowActionMessage($"Saving #{ticket.Id}…", isLoading: true);
            var latest = await _tdxService.GetTicketAsync(ticket.Id) ?? ticket;
            var patch = edits.Patch(latest);
            // As on the Mac, the new responsible is emailed only when the box
            // is ticked and Save really changes who is responsible.
            var notify = patch != null && edits.ShouldNotify(latest);
            var updated = patch == null ? latest : await _tdxService.UpdateTicketAsync(ticket.Id, patch, notifyNewResponsible: notify);
            if (updated == null)
            {
                ShowActionMessage("TeamDynamix did not accept the changes. See the log for the response.", isError: true);
                return;
            }
            // Picks made while the save ran are newer work and stay.
            if (edits.Patch(updated) == null) edits.Clear();
            ReplaceCachedTicket(updated);
            // A new group brings new members to the People section and the notify list.
            if (patch?.ContainsKey(TicketDetailFields.Group) == true && _selectedTicket?.Id == ticket.Id)
                _ = LoadTicketPeopleAsync(updated);
            ShowActionMessage(patch == null ? $"#{ticket.Id} already had these values"
                : notify ? $"Saved #{ticket.Id} and notified the new responsible"
                : $"Saved #{ticket.Id}");
        }
        catch (Exception ex)
        {
            ShowActionMessage($"Could not save #{ticket.Id}: {ex.Message}", isError: true);
        }
        finally
        {
            _savingEdits = false;
            UpdateEditButtons();
        }
    }

    // ── Requestor ─────────────────────────────────────────────────

    private CancellationTokenSource? _requestorSearchCts;
    private const int RequestorMinimumQuery = 2;

    private void OnChangeRequestorClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedTicket == null) return;
        RequestorPopup.IsOpen = !RequestorPopup.IsOpen;
    }

    private void OnRequestorPopupOpened(object? sender, EventArgs e)
    {
        _requestorSearchCts?.Cancel();
        RequestorSearchBox.Text = "";
        RequestorResultsList.ItemsSource = null;
        SetRequestorBusy(false);
        RequestorStatusText.Text = $"Type at least {RequestorMinimumQuery} letters of a name or email.";
        Dispatcher.BeginInvoke(() => Keyboard.Focus(RequestorSearchBox), DispatcherPriority.Input);
    }

    private async void OnRequestorSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_tdxService == null) return;
        _requestorSearchCts?.Cancel();
        var cts = _requestorSearchCts = new CancellationTokenSource();
        var query = RequestorSearchBox.Text?.Trim() ?? "";

        if (query.Length < RequestorMinimumQuery)
        {
            RequestorResultsList.ItemsSource = null;
            SetRequestorBusy(false);
            RequestorStatusText.Text = $"Type at least {RequestorMinimumQuery} letters of a name or email.";
            return;
        }

        // One lookup per pause in typing, not one per keystroke.
        try { await Task.Delay(300, cts.Token); }
        catch (TaskCanceledException) { return; }

        SetRequestorBusy(true);
        RequestorStatusText.Text = "Searching…";
        var people = await _tdxService.SearchPeopleAsync(query, 15);
        if (cts.IsCancellationRequested) return;
        SetRequestorBusy(false);

        var pickable = people.Where(p => p.Uid is { } u && u != Guid.Empty).ToList();
        RequestorResultsList.ItemsSource = pickable;
        RequestorStatusText.Text = pickable.Count == 0 ? "No matching people." : "Pick the new requestor.";
    }

    private void SetRequestorBusy(bool busy)
    {
        RequestorProgress.IsActive = busy;
        RequestorProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnRequestorSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && RequestorResultsList.Items.Count > 0)
        {
            RequestorResultsList.SelectedIndex = 0;
            (RequestorResultsList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && RequestorResultsList.Items.Count > 0)
        {
            e.Handled = true;
            if (RequestorResultsList.Items[0] is TdxPerson person) await ChangeRequestorAsync(person);
        }
        else if (e.Key == Key.Escape)
        {
            RequestorPopup.IsOpen = false;
            ChangeRequestorButton.Focus();
            e.Handled = true;
        }
    }

    private async void OnRequestorResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && RequestorResultsList.SelectedItem is TdxPerson person)
        {
            e.Handled = true;
            await ChangeRequestorAsync(person);
        }
        else if (e.Key == Key.Escape)
        {
            RequestorPopup.IsOpen = false;
            ChangeRequestorButton.Focus();
            e.Handled = true;
        }
    }

    private async void OnRequestorResultPicked(object sender, MouseButtonEventArgs e)
    {
        if (RequestorResultsList.SelectedItem is TdxPerson person) await ChangeRequestorAsync(person);
    }

    private Task ChangeRequestorAsync(TdxPerson person)
    {
        RequestorPopup.IsOpen = false;
        ChangeRequestorButton.Focus();
        // Held for Save with the other fields, as on the Mac.
        if (person.Uid is { } uid) HoldEdit(TicketDetailFields.Requestor, uid, person.DisplayName);
        return Task.CompletedTask;
    }

    // ── Description ───────────────────────────────────────────────

    /// <summary>The ticket whose description is open for editing, if any.</summary>
    private int? _descriptionEditTicketId;
    private bool _savingDescription;

    /// <summary>
    /// Show the ticket's description as text. An editor open on this same
    /// ticket is left alone, so a refresh after another change never throws
    /// away what is being typed; one open on another ticket is closed.
    /// </summary>
    private void ShowDescription(TdxTicket ticket)
    {
        DetailDescriptionText.Text = DecodeHtml(ticket.Description ?? "");
        SetDescriptionEditing(_descriptionEditTicketId == ticket.Id);
    }

    private void SetDescriptionEditing(bool editing)
    {
        _descriptionEditTicketId = editing ? _selectedTicket?.Id : null;
        var reading = editing ? Visibility.Collapsed : Visibility.Visible;
        DescriptionEditor.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        CancelDescriptionButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        CopyDescriptionButton.Visibility = reading;
        EditDescriptionIcon.Glyph = editing ? "" : "";
        var tip = editing ? "Save description (Ctrl+Enter)" : "Edit description";
        EditDescriptionButton.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(EditDescriptionButton, editing ? "Save description" : "Edit description");
        if (editing)
        {
            DetailDescriptionText.Visibility = Visibility.Collapsed;
            NoDescriptionText.Visibility = Visibility.Collapsed;
        }
        else
        {
            var hasText = DetailDescriptionText.Text.Length > 0;
            DetailDescriptionText.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
            NoDescriptionText.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private async void OnEditDescriptionClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedTicket == null) return;
        if (_descriptionEditTicketId == _selectedTicket.Id)
        {
            await SaveDescriptionAsync();
            return;
        }
        DescriptionEditor.Text = DetailDescriptionText.Text;
        SetDescriptionEditing(true);
        Dispatcher.BeginInvoke(() =>
        {
            Keyboard.Focus(DescriptionEditor);
            DescriptionEditor.CaretIndex = DescriptionEditor.Text.Length;
        }, DispatcherPriority.Input);
    }

    private void OnCancelDescriptionClicked(object sender, RoutedEventArgs e)
    {
        SetDescriptionEditing(false);
        EditDescriptionButton.Focus();
    }

    private async void OnDescriptionEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            SetDescriptionEditing(false);
            EditDescriptionButton.Focus();
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await SaveDescriptionAsync();
        }
    }

    /// <summary>
    /// Send the edited description as one PATCH, as the Mac does, only when it
    /// reads differently from the saved one. The editor stays open with the
    /// text when TeamDynamix refuses it, so nothing typed is lost.
    /// </summary>
    private async Task SaveDescriptionAsync()
    {
        if (_savingDescription || _tdxService == null || _selectedTicket is not { } ticket) return;
        var update = TicketDetailFields.DescriptionChange(DecodeHtml(ticket.Description ?? ""), DescriptionEditor.Text);
        if (update == null)
        {
            SetDescriptionEditing(false);
            return;
        }

        _savingDescription = true;
        EditDescriptionButton.IsEnabled = false;
        DescriptionProgress.Visibility = Visibility.Visible;
        DescriptionProgress.IsActive = true;
        try
        {
            ShowActionMessage($"Saving the description of #{ticket.Id}…", isLoading: true);
            var updated = await _tdxService.UpdateTicketAsync(ticket.Id, update);
            if (updated == null)
            {
                ShowActionMessage("TeamDynamix did not accept the description. See the log for the response.", isError: true);
                return;
            }
            if (_selectedTicket?.Id == ticket.Id) SetDescriptionEditing(false);
            ReplaceCachedTicket(updated);
            ShowActionMessage($"Saved the description of #{ticket.Id}");
        }
        finally
        {
            _savingDescription = false;
            EditDescriptionButton.IsEnabled = true;
            DescriptionProgress.IsActive = false;
            DescriptionProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCopyDescriptionClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedTicket == null) return;
        CopyToClipboard(DetailDescriptionText.Text, "Description copied");
    }
}
