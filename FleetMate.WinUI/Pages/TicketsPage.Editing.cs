using System.ComponentModel;
using FleetMate.WinUI.Pages.Tickets;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace FleetMate.WinUI.Pages;

/// <summary>
/// The detail pane's editing input: the Reassign and Parent flyouts, the
/// description editor, the composer and replies. The work is the detail view
/// model's; this moves focus and maps keys, as the Mac's sheets do.
/// </summary>
public sealed partial class TicketsPage
{
    private CancellationTokenSource? _reassignSearch;

    /// <summary>Wire the detail pane's editing to the services and the list.</summary>
    private void AttachEditing()
    {
        Detail.Attach(ViewModel.IsDemo ? null : ViewModel.Tdx, () => ViewModel.Me);
        Detail.Edited += ViewModel.Replace;
        Detail.Reported += (message, error) => ShowAction(message, error ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        Detail.PropertyChanged += Detail_PropertyChanged;
    }

    private void Detail_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TicketDetailViewModel.ReplyingTo):
                var thread = Detail.ReplyingTo;
                ReplyComposer.Visibility = Show(thread != null);
                if (thread == null) return;
                ReplyHeader.Text = $"Replying to {thread.Author}, {thread.When}";
                ReplyBox.Focus(FocusState.Programmatic);
                break;
            case nameof(TicketDetailViewModel.IsComposeOpen) when Detail.IsComposeOpen:
                CommentBox.Focus(FocusState.Programmatic);
                CommentBox.SelectionStart = CommentBox.Text.Length;
                break;
            case nameof(TicketDetailViewModel.IsEditingDescription) when Detail.IsEditingDescription:
                DescriptionEditor.Focus(FocusState.Programmatic);
                break;
        }
    }

    /// <summary>The read-only description shows unless it is being edited.</summary>
    public Visibility DescriptionReadVisibility(bool editing) => editing ? Visibility.Collapsed : Visibility.Visible;

    private static bool CtrlDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>Ctrl+Enter saves held picks, as Cmd+Return does on the Mac; a text box's own Ctrl+Enter comes first.</summary>
    private async void SaveAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox || !Detail.CanSave) return;
        args.Handled = true;
        await Detail.SaveAsync();
    }

    // MARK: - Reassign

    private void ReassignFlyout_Opening(object sender, object e)
    {
        _reassignSearch?.Cancel();
        ReassignSearchBox.Text = "";
        ReassignResults.ItemsSource = null;
        ReassignStatusText.Text = $"Type at least {TicketDetailViewModel.MinimumSearch} letters of a name, email or group.";
        ReassignSearchBox.Focus(FocusState.Programmatic);
    }

    /// <summary>One lookup per pause in typing, not one per keystroke.</summary>
    private async void ReassignSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _reassignSearch?.Cancel();
        var cts = _reassignSearch = new CancellationTokenSource();
        var query = ReassignSearchBox.Text.Trim();
        if (query.Length < TicketDetailViewModel.MinimumSearch)
        {
            ReassignResults.ItemsSource = null;
            ReassignStatusText.Text = $"Type at least {TicketDetailViewModel.MinimumSearch} letters of a name, email or group.";
            return;
        }
        try
        {
            await Task.Delay(250, cts.Token);
            ReassignStatusText.Text = "Searching…";
            var rows = await Detail.SearchResponsiblesAsync(query, cts.Token);
            if (cts.IsCancellationRequested) return;
            ReassignResults.ItemsSource = rows;
            if (rows.Count > 0) ReassignResults.SelectedIndex = 0;
            ReassignStatusText.Text = rows.Count == 0
                ? $"No one who can own tickets matches “{query}”."
                : "Up and Down move, Enter picks.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            ReassignResults.ItemsSource = null;
            ReassignStatusText.Text = $"Couldn't search TeamDynamix: {ex.Message}";
        }
    }

    /// <summary>The search box keeps focus: Up and Down move the highlight, Enter picks it.</summary>
    private void ReassignSearch_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var count = ReassignResults.Items.Count;
        switch (e.Key)
        {
            case VirtualKey.Down when count > 0:
                ReassignResults.SelectedIndex = Math.Min(ReassignResults.SelectedIndex + 1, count - 1);
                ReassignResults.ScrollIntoView(ReassignResults.SelectedItem);
                e.Handled = true;
                break;
            case VirtualKey.Up when count > 0:
                ReassignResults.SelectedIndex = Math.Max(ReassignResults.SelectedIndex - 1, 0);
                ReassignResults.ScrollIntoView(ReassignResults.SelectedItem);
                e.Handled = true;
                break;
            case VirtualKey.Enter when ReassignResults.SelectedItem is ReassignCandidate row:
                e.Handled = true;
                PickReassign(row);
                break;
        }
    }

    private void ReassignResults_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ReassignCandidate row) PickReassign(row);
    }

    private void PickReassign(ReassignCandidate row)
    {
        ReassignFlyout.Hide();
        Detail.PickResponsible(row);
        ReassignButton.Focus(FocusState.Programmatic);
    }

    // MARK: - Parent

    private void ParentFlyout_Opening(object sender, object e)
    {
        var ticket = Detail.Ticket;
        ParentIdBox.Text = ticket?.ParentId is int id && id > 0 ? id.ToString() : "";
        ParentTitleBox.Text = ticket?.Title ?? "";
        ParentHintText.Text = ticket?.ParentTitle is { Length: > 0 } title ? $"Now: {title}" : "";
        ParentIdBox.Focus(FocusState.Programmatic);
        ParentIdBox.SelectAll();
    }

    private async void SetParent_Click(object sender, RoutedEventArgs e) => await SetParentAsync();

    private async void ParentIdBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await SetParentAsync();
    }

    private async Task SetParentAsync()
    {
        var hint = await Detail.SetParentAsync(ParentIdBox.Text);
        if (hint != null) ParentHintText.Text = hint; else ParentFlyout.Hide();
    }

    private async void CreateParent_Click(object sender, RoutedEventArgs e) => await CreateParentAsync();

    private async void ParentTitleBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await CreateParentAsync();
    }

    private async Task CreateParentAsync()
    {
        var hint = await Detail.CreateParentAsync(ParentTitleBox.Text, ViewModel.Insert);
        if (hint != null) ParentHintText.Text = hint; else ParentFlyout.Hide();
    }

    // MARK: - Description, comments and replies

    private async void DescriptionEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && CtrlDown())
        {
            e.Handled = true;
            await Detail.SaveDescriptionCommand.ExecuteAsync(null);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Detail.CancelDescriptionCommand.Execute(null);
        }
    }

    private async void CommentBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || !CtrlDown()) return;
        e.Handled = true;
        await Detail.PostCommentAsync();
    }

    private void ReplyInThread_Click(object sender, RoutedEventArgs e)
    {
        // A reply to a reply joins its comment's thread.
        if (sender is FrameworkElement { Tag: TicketFeedItem item }) Detail.OpenReply(item.Thread ?? item);
    }

    private void QuoteEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TicketFeedItem item }) Detail.Quote(item);
    }

    private async void PostReply_Click(object sender, RoutedEventArgs e) => await Detail.PostReplyAsync();

    private void CancelReply_Click(object sender, RoutedEventArgs e) => Detail.CancelReply();

    private async void ReplyBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && CtrlDown())
        {
            e.Handled = true;
            await Detail.PostReplyAsync();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Detail.CancelReply();
        }
    }

    /// <summary>Deleting a reply cannot be undone, so it asks first.</summary>
    private async void DeleteReply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TicketFeedItem { Thread: { } thread } reply }) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete Reply",
            Content = new TextBlock { Text = "Delete your reply? This can't be undone.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Detail.DeleteReplyAsync(thread, reply);
    }
}
