using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FleetMate.Core.Config;
using FleetMate.GUI.Views.Development;

namespace FleetMate.GUI.Views.Shared;

public partial class MainWindow
{
    private Popup? _inboxPopup;
    private StackPanel? _inboxContent;
    private bool _inboxSubscribed;

    private void OnGitHubInboxLoaded(object sender, RoutedEventArgs e)
    {
        if (AppEdition.Current.IsTicketsOnly)
        {
            GitHubInboxButton.Visibility = Visibility.Collapsed;
            return;
        }
        if (!_inboxSubscribed && Application.Current is App app)
        {
            _inboxSubscribed = true;
            app.Inbox.Changed += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                GitHubInboxIcon.Glyph = app.Inbox.UnreadCount > 0 ? "\uE8F2" : "\uE715";
                GitHubInboxButton.ToolTip = app.Inbox.LastError != null ? "GitHub Inbox — refresh failed" : "GitHub Inbox";
                if (_inboxPopup?.IsOpen == true) RenderGitHubInbox();
            });
        }
    }

    private void OnGitHubInboxClicked(object sender, RoutedEventArgs e)
    {
        if (_inboxPopup?.IsOpen == true) { _inboxPopup.IsOpen = false; return; }
        if (_inboxPopup == null)
        {
            _inboxContent = new StackPanel { Margin = new Thickness(12), Width = 420 };
            var border = new Border { Child = _inboxContent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
            border.SetResourceReference(Border.BackgroundProperty, "SystemControlBackgroundChromeMediumLowBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "SystemControlForegroundBaseLowBrush");
            _inboxPopup = new Popup { PlacementTarget = GitHubInboxButton, Placement = PlacementMode.Bottom,
                StaysOpen = false, AllowsTransparency = true, Child = border };
        }
        RenderGitHubInbox();
        _inboxPopup.IsOpen = true;
    }

    private void RenderGitHubInbox()
    {
        if (_inboxContent == null || Application.Current is not App app) return;
        _inboxContent.Children.Clear();
        var heading = new DockPanel();
        var refresh = new Button { Content = "Refresh", IsEnabled = !app.Inbox.IsRefreshing };
        DockPanel.SetDock(refresh, Dock.Right);
        refresh.Click += async (_, _) => { await app.Inbox.RefreshAsync(); RenderGitHubInbox(); };
        heading.Children.Add(refresh);
        heading.Children.Add(new TextBlock { Text = "GitHub Inbox", FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, FontSize = 14 });
        _inboxContent.Children.Add(heading);
        if (app.Inbox.LastError is { } error)
            _inboxContent.Children.Add(new TextBlock { Text = $"Refresh failed: {error}\nShowing saved notifications.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) });
        var list = new StackPanel();
        foreach (var notification in app.Inbox.Notifications.Where(item => item.Unread))
        {
            var row = DevelopmentNotificationRowViewModel.Create(notification, app.DevelopmentPullRequests?.PullRequests ?? []);
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = row.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            label.Children.Add(new TextBlock { Text = row.Byline, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            var button = new Button { Content = label, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(6) };
            button.Click += (_, _) =>
            {
                _inboxPopup!.IsOpen = false;
                NavigateToTab("Development");
                if (GetOrCreatePage("Development") is DevelopmentPage page) page.View.OpenInboxNotification(notification);
            };
            list.Children.Add(button);
        }
        if (list.Children.Count == 0)
            list.Children.Add(new TextBlock { Text = app.Inbox.IsRefreshing ? "Refreshing…" : "No unread notifications.", Margin = new Thickness(0, 10, 0, 6) });
        _inboxContent.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var all = new Button { Content = "View Inbox", Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        all.Click += (_, _) =>
        {
            _inboxPopup!.IsOpen = false;
            NavigateToTab("Development");
            if (GetOrCreatePage("Development") is DevelopmentPage page) page.View.ShowInbox();
        };
        _inboxContent.Children.Add(all);
    }
}
