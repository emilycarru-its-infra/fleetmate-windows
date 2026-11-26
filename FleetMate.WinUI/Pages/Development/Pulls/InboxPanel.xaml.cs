using FleetMate.Core.Development;
using FleetMate.Core.Models.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace FleetMate.WinUI.Pages.Development.Pulls;

/// <summary>
/// The Inbox popover: GitHub notifications, Unread or All, with Mark Read,
/// Done and Unsubscribe on each thread and Mark All Read. Picking a thread opens
/// it in Pulls' detail pane. The inbox itself is FleetMate.Core's
/// <see cref="DevelopmentInbox"/>, polled from launch.
/// </summary>
public sealed partial class InboxPanel : UserControl
{
    private DevelopmentPage? _page;
    private bool _showRead;
    private string? _selectedId;

    public InboxPanel() => InitializeComponent();

    public void Attach(DevelopmentPage page) => _page = page;

    private static DevelopmentInbox Inbox => App.Services.Inbox;

    public void Render()
    {
        var inbox = Inbox;
        MarkAllReadButton.IsEnabled = inbox.UnreadCount > 0;
        var pullRequests = App.Services.DevelopmentPullRequests?.PullRequests ?? Enumerable.Empty<UnifiedPullRequest>();
        var rows = DevelopmentFilter.Inbox(inbox.Notifications, _showRead).Select(n => new NotificationRow(n, pullRequests)).ToList();
        List.ItemsSource = rows;
        if (_selectedId != null) List.SelectedItem = rows.FirstOrDefault(r => r.Notification.Id == _selectedId);
        StatusText.Text = DevelopmentText.InboxStatus(inbox);
        StatusText.Visibility = DevelopmentUi.Show(StatusText.Text.Length > 0);
        EmptyText.Visibility = DevelopmentUi.Show(rows.Count == 0);
        EmptyText.Text = DevelopmentText.InboxEmpty(inbox, _showRead);
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        _showRead = (sender as FrameworkElement)?.Tag as string == "All";
        UnreadChip.IsChecked = !_showRead;
        AllChip.IsChecked = _showRead;
        Render();
    }

    private async void List_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not NotificationRow row || _page == null) return;
        _selectedId = row.Notification.Id;
        _page.CloseInbox();
        await _page.Pulls.ShowNotificationAsync(row);
    }

    private async void MarkRead_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is NotificationRow row) await MarkReadAsync(row);
    }

    private async void Done_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is NotificationRow row) await DoneAsync(row);
    }

    private async void Unsubscribe_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is NotificationRow row) await UnsubscribeAsync(row);
    }

    private async void MarkAllRead_Click(object sender, RoutedEventArgs e)
    {
        if (App.Services.DemoMode) return;
        var result = await Inbox.MarkAllReadAsync();
        if (!result.Success) ShowFailure($"Mark all read failed — {result.Error}");
    }

    private async Task MarkReadAsync(NotificationRow row)
    {
        if (App.Services.DemoMode) return;
        var result = await Inbox.MarkReadAsync(row.Notification);
        if (!result.Success) ShowFailure($"Mark read failed — {result.Error}");
    }

    private async Task DoneAsync(NotificationRow row)
    {
        if (App.Services.DemoMode) return;
        var result = await Inbox.MarkDoneAsync(row.Notification);
        if (!result.Success) ShowFailure($"Done failed — {result.Error}");
    }

    private async Task UnsubscribeAsync(NotificationRow row)
    {
        if (App.Services.DemoMode) return;
        var result = await Inbox.UnsubscribeAsync(row.Notification);
        if (!result.Success) ShowFailure($"Unsubscribe failed — {result.Error}");
    }

    private void ShowFailure(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    /// <summary>The thread's menu: the row's three actions, then the browser and the link.</summary>
    private void List_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if ((args.OriginalSource as FrameworkElement)?.DataContext is not NotificationRow row) return;
        args.Handled = true;
        var menu = new MenuFlyout();
        if (row.Unread) menu.Items.Add(Item("Mark as Read", () => _ = MarkReadAsync(row)));
        menu.Items.Add(Item("Done", () => _ = DoneAsync(row)));
        menu.Items.Add(Item("Unsubscribe", () => _ = UnsubscribeAsync(row)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Open in Browser", () => DevelopmentUi.OpenUrl(row.Notification.WebUrl)));
        menu.Items.Add(Item("Copy Link", () => DevelopmentUi.Copy(row.Notification.WebUrl)));
        var target = args.OriginalSource as UIElement ?? sender;
        menu.ShowAt(target, args.TryGetPosition(target, out var point) ? new FlyoutShowOptions { Position = point } : new FlyoutShowOptions());
    }

    private static MenuFlyoutItem Item(string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => action();
        return item;
    }
}
