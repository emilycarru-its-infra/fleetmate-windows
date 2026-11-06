using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FleetMate.Core.Models.Tickets;

namespace FleetMate.GUI.Views.Tickets;

// The detail pane's header and fields, as on macOS: the actions row that drops
// its labels instead of wrapping, the Requestor picker and the Description
// editor. Every change goes through the same sparse PATCH as Status.
public partial class TicketsPage
{
    // ── Header actions ────────────────────────────────────────────

    private bool _detailActionsIconOnly;
    private double _detailActionsFullWidth;

    private void OnDetailHeaderSizeChanged(object sender, SizeChangedEventArgs e) => FitDetailActions();

    /// <summary>
    /// Keep the ticket actions on one row. With labels they need the width
    /// recorded the last time they were shown; when the row is narrower than
    /// that they show icons only (their names stay in the tooltip and for
    /// screen readers), and the labels come back once there is room.
    /// </summary>
    private void FitDetailActions()
    {
        var available = DetailHeaderRow.ActualWidth - DetailIdentityPanel.ActualWidth
            - DetailIdentityPanel.Margin.Left - DetailIdentityPanel.Margin.Right;
        if (available <= 0) return;
        if (!_detailActionsIconOnly) _detailActionsFullWidth = DetailActionsWidth();
        var iconOnly = _detailActionsFullWidth > available;
        if (iconOnly != _detailActionsIconOnly) ShowDetailActionLabels(!iconOnly);
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

    private async Task ChangeRequestorAsync(TdxPerson person)
    {
        RequestorPopup.IsOpen = false;
        if (_selectedTicket is not { } ticket || person.Uid is not { } uid) return;
        await UpdateTicketFieldAsync(ticket.Id, TicketDetailFields.Requestor, uid);
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
