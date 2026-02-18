using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace FleetMate.GUI.Views;

public partial class DashboardPage : Page
{
    private readonly App? _app;

    public DashboardPage()
    {
        InitializeComponent();

        if (Application.Current is App app)
            _app = app;

        Loaded += (_, _) => RefreshStats();
    }

    private void RefreshStats()
    {
        if (_app == null) return;

        // Counts from cache (populated by App.PreloadAllDataAsync)
        var deviceCount = _app.CachedDevices.Count;
        DevicesCountText.Text = deviceCount > 0 ? deviceCount.ToString() : "--";

        // Non-compliant count
        if (deviceCount > 0)
        {
            var nonCompliant = _app.CachedDevices
                .Count(d => d.ComplianceState?.Equals("noncompliant", StringComparison.OrdinalIgnoreCase) == true);
            NonCompliantCountText.Text = nonCompliant.ToString();
            NonCompliantCountText.Foreground = nonCompliant > 0
                ? System.Windows.Media.Brushes.OrangeRed
                : System.Windows.Media.Brushes.Green;
        }
        else
        {
            NonCompliantCountText.Text = "--";
        }

        AssetsCountText.Text = _app.CachedAssets.Count > 0 ? _app.CachedAssets.Count.ToString() : "--";

        var openTickets = _app.CachedTickets
            .Where(t => t.StatusName?.ToLowerInvariant() is not ("closed" or "cancelled" or "canceled"))
            .ToList();
        TicketsCountText.Text = _app.CachedTickets.Count > 0 ? openTickets.Count.ToString() : "--";

        // Configuration Status
        UpdateConfigStatus(
            ConfigGraphIcon, ConfigGraphStatus,
            _app.GraphService != null);
        UpdateConfigStatus(
            ConfigDevOpsIcon, ConfigDevOpsStatus,
            _app.Config.AzureDevOps != null && !string.IsNullOrEmpty(_app.Config.AzureDevOps.Organization));
        UpdateConfigStatus(
            ConfigTdxIcon, ConfigTdxStatus,
            _app.TdxService != null);
        UpdateConfigStatus(
            ConfigSnipeIcon, ConfigSnipeStatus,
            _app.SnipeService != null);

        // Recent activity: last 10 ticket modifications from cache
        var activity = new ObservableCollection<string>();
        if (_app.CachedTickets.Count > 0)
        {
            foreach (var t in _app.CachedTickets
                .Where(t => t.ModifiedDate.HasValue)
                .OrderByDescending(t => t.ModifiedDate!.Value)
                .Take(10))
            {
                var ago = FormatRelative(t.ModifiedDate!.Value);
                var status = t.StatusName ?? "";
                activity.Add($"#{t.Id}  {t.Title?.Truncate(60)}  ·  {status}  ·  {ago}");
            }
        }
        ActivityFeed.ItemsSource = activity;
    }

    private static void UpdateConfigStatus(TextBlock icon, TextBlock status, bool configured)
    {
        icon.Text = configured ? "✅" : "❌";
        status.Text = configured ? "Configured" : "Not Configured";
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (_app == null) return;

        // Invalidate caches and reload
        _app.InvalidateAllCaches();
        _ = _app.ReloadAllDataAsync().ContinueWith(_ =>
        {
            Dispatcher.Invoke(RefreshStats);
        });
    }

    private void OnDismissErrorClicked(object sender, RoutedEventArgs e)
    {
        ErrorBanner.Visibility = Visibility.Collapsed;
    }

    public void ShowError(string message)
    {
        ErrorBannerText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private static string FormatRelative(DateTime dt)
    {
        var span = DateTime.Now - dt;
        if (span.TotalMinutes < 2)  return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24)   return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7)     return $"{(int)span.TotalDays}d ago";
        return dt.ToString("MMM d");
    }
}

internal static class StringExtensions
{
    public static string Truncate(this string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
