using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FleetMate.Core.Models.Inventory;
using Serilog;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// The toolbar Recent Activity popover, which replaced the Dashboard's feed:
/// on every tab, filtered to that tab, each row opening its deep link. The
/// app-level error banner sits at its top.
/// </summary>
public partial class MainWindow
{
    private List<SnipeActivity> _snipeActivity = new();
    private DateTime _snipeActivityLoadedAt = DateTime.MinValue;

    private async void OnRecentActivityClicked(object sender, RoutedEventArgs e)
    {
        if (RecentActivityPopup.IsOpen)
        {
            RecentActivityPopup.IsOpen = false;
            return;
        }

        UpdateAppError();
        RenderRecentActivity();
        RecentActivityPopup.IsOpen = true;

        // The Snipe-IT activity log is the one source not already cached; read
        // it when Inventory's feed is opened, at most every five minutes.
        if (CurrentTab == "Inventory" && Application.Current is App { SnipeService: { } snipe }
            && DateTime.UtcNow - _snipeActivityLoadedAt > TimeSpan.FromMinutes(5))
        {
            try
            {
                _snipeActivity = await snipe.GetActivityAsync(limit: 50);
                _snipeActivityLoadedAt = DateTime.UtcNow;
                if (RecentActivityPopup.IsOpen) RenderRecentActivity();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[activity] Snipe-IT activity log unavailable");
            }
        }
    }

    private void RenderRecentActivity()
    {
        if (Application.Current is not App app) return;

        var tab = CurrentTab;
        RecentActivityHeader.Text = $"Recent Activity · {tab}";
        RecentActivityHideMine.Visibility = tab == "Development" ? Visibility.Visible : Visibility.Collapsed;

        var items = RecentActivityFeed.Build(tab, new RecentActivityFeed.Sources
        {
            Tickets = app.CachedTickets,
            WorkItems = app.CachedWorkItems,
            Devices = app.CachedDevices,
            Assets = app.CachedAssets,
            SnipeActivity = _snipeActivity,
            PullRequests = app.DevelopmentPullRequests,
        }, DateTime.UtcNow, RecentActivityHideMine.IsChecked == true);

        RecentActivityList.Children.Clear();

        if (items.Count == 0)
        {
            RecentActivityList.Children.Add(new TextBlock
            {
                Text = tab is "Manage" or "Identity" ? "No activity feed for this tab." : "Nothing recent.",
                Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
                Margin = new Thickness(0, 8, 0, 8),
            });
            return;
        }

        foreach (var item in items.Take(200)) RecentActivityList.Children.Add(BuildActivityRow(item));
    }

    private FrameworkElement BuildActivityRow(ActivityItem item)
    {
        var medium = (Brush)FindResource("SystemControlForegroundBaseMediumBrush");

        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), Cursor = Cursors.Hand, Background = Brushes.Transparent };

        var icon = new ModernWpf.Controls.FontIcon
        {
            Glyph = item.Icon, FontSize = 14, Margin = new Thickness(0, 2, 10, 0),
            VerticalAlignment = VerticalAlignment.Top, Foreground = medium,
        };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);

        var right = new StackPanel { Margin = new Thickness(8, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        if (!string.IsNullOrEmpty(item.Detail))
        {
            right.Children.Add(new Border
            {
                Background = (Brush)FindResource("SubtleFillBrush"), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 1, 6, 1), HorizontalAlignment = HorizontalAlignment.Right,
                Child = new TextBlock { Text = item.Detail, FontSize = 10, MaxWidth = 120, TextTrimming = TextTrimming.CharacterEllipsis },
            });
        }
        right.Children.Add(new TextBlock { Text = item.Time, FontSize = 10, Foreground = medium, HorizontalAlignment = HorizontalAlignment.Right });
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);

        var main = new StackPanel();
        main.Children.Add(new TextBlock { Text = item.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = item.Name });
        if (!string.IsNullOrEmpty(item.Context))
            main.Children.Add(new TextBlock { Text = item.Context, FontSize = 10, Foreground = medium, TextTrimming = TextTrimming.CharacterEllipsis });
        row.Children.Add(main);

        row.MouseLeftButtonUp += (_, _) => OpenActivity(item);
        return row;
    }

    /// <summary>Set the matching pending deep link, then switch to its tab — the page opens the item when shown.</summary>
    private void OpenActivity(ActivityItem item)
    {
        if (Application.Current is not App app) return;
        RecentActivityPopup.IsOpen = false;

        if (item.DeviceId != null) app.PendingNavigateDeviceId = item.DeviceId;
        if (item.TicketId != null) app.PendingNavigateTicketId = item.TicketId;
        if (item.AssetId != null) app.PendingNavigateAssetId = item.AssetId;
        if (item.WorkItemId != null) app.PendingNavigateWorkItemId = item.WorkItemId;

        if (item.Tab == CurrentTab && item.PullRequest == null)
        {
            // Already on the tab: reload the page into the frame so it runs
            // its Loaded handler again and picks up the pending link.
            var current = GetOrCreatePage(item.Tab);
            ContentFrame.Content = null;
            ContentFrame.Navigate(current);
        }
        else
        {
            NavigateToTab(item.Tab);
        }

        // The page is cached, so it can be handed the PR before the frame has
        // finished navigating to it.
        if (item.PullRequest != null && GetOrCreatePage("Development") is FleetMate.GUI.Views.Development.DevelopmentPage page)
            page.View.ShowPullRequest(item.PullRequest);
    }

    private void OnRecentActivityHideMineChanged(object sender, RoutedEventArgs e)
    {
        if (RecentActivityPopup.IsOpen) RenderRecentActivity();
    }

    private void UpdateAppError()
    {
        var error = (Application.Current as App)?.AppError;
        AppErrorDot.Visibility = error != null ? Visibility.Visible : Visibility.Collapsed;
        AppErrorBanner.Visibility = error != null ? Visibility.Visible : Visibility.Collapsed;
        AppErrorText.Text = error ?? "";
    }

    private void OnDismissAppErrorClicked(object sender, RoutedEventArgs e) =>
        (Application.Current as App)?.ClearAppError();
}
