using System.Windows.Threading;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Serilog;

namespace FleetMate.GUI.Views.Projects.Code;

/// <summary>
/// The GitHub inbox, held for the app's lifetime rather than by the Code view.
///
/// It polls from startup whether or not Code has been opened, because the whole
/// point is the unread badge on the Projects tab: a notification nobody has
/// gone looking for is exactly the one that gets missed.
/// </summary>
public sealed class CodeInbox
{
    /// <summary>
    /// Five minutes: well inside GitHub's advertised X-Poll-Interval (60s) and
    /// a rounding error against the REST quota.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly Func<GitHubNotificationService?> _serviceFactory;
    private DispatcherTimer? _timer;
    private bool _refreshing;

    public CodeInbox(Func<GitHubNotificationService?> serviceFactory)
    {
        _serviceFactory = serviceFactory;
    }

    public IReadOnlyList<GitHubNotification> Notifications { get; private set; } = Array.Empty<GitHubNotification>();
    public int UnreadCount => Notifications.Count(n => n.Unread);
    public string? LastError { get; private set; }
    public DateTime? LastRefreshed { get; private set; }

    /// <summary>Raised on the UI thread after every refresh or local change.</summary>
    public event EventHandler? Changed;

    public void Start()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        using var service = _serviceFactory();
        if (service == null) return;

        _refreshing = true;
        try
        {
            Notifications = await service.ListAsync();
            LastError = null;
            LastRefreshed = DateTime.Now;
        }
        catch (Exception ex)
        {
            // Signed out of GitHub is a normal state; keep the last list and
            // say why it is stale rather than blanking it.
            Log.Warning(ex, "[inbox] refresh failed");
            LastError = ex.Message;
        }
        finally
        {
            _refreshing = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<PullRequestActionResult> MarkReadAsync(GitHubNotification notification)
    {
        using var service = _serviceFactory();
        if (service == null) return PullRequestActionResult.Failed("GitHub is not configured");

        var result = await service.MarkReadAsync(notification.Id);
        if (result.Success)
        {
            notification.Unread = false;
            Resort();
        }
        return result;
    }

    public async Task<PullRequestActionResult> MarkAllReadAsync()
    {
        using var service = _serviceFactory();
        if (service == null) return PullRequestActionResult.Failed("GitHub is not configured");

        var result = await service.MarkAllReadAsync();
        if (result.Success)
        {
            // GitHub may apply a large mark-all asynchronously (202), so the
            // next list can still show some unread. Reflect the intent now.
            foreach (var n in Notifications) n.Unread = false;
            Resort();
        }
        return result;
    }

    public async Task<PullRequestActionResult> UnsubscribeAsync(GitHubNotification notification)
    {
        using var service = _serviceFactory();
        if (service == null) return PullRequestActionResult.Failed("GitHub is not configured");

        var result = await service.UnsubscribeAsync(notification.Id);
        if (!result.Success) return result;

        // Unsubscribing does not read the thread; do both so it leaves the
        // unread count, which is what the operator meant.
        if (notification.Unread) await MarkReadAsync(notification);
        Notifications = Notifications.Where(n => n.Id != notification.Id).ToList();
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private void Resort()
    {
        Notifications = GitHubNotificationService.Sort(Notifications);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
