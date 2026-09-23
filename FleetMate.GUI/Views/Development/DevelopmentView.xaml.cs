using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.GUI.Views.Shared;
using Serilog;

namespace FleetMate.GUI.Views.Development;

/// <summary>
/// Development: every open pull request on the operator's projects across Azure DevOps
/// and GitHub, plus the GitHub inbox, with the selected item shown inline.
///
/// Self-contained — it owns its data loading and reads its caches
/// from <see cref="App"/> — so it survives page rebuilds on tab switches
/// without the tab page doing more than host it.
/// </summary>
public partial class DevelopmentView : UserControl
{
    private const int RepoChipLimit = 12;

    private DevelopmentSourceFilter _source = DevelopmentSourceFilter.All;
    private DevelopmentScope _scope = DevelopmentScope.Everything;
    private string? _repository;
    private bool _loading;
    private bool _inboxHooked;
    private GitHubNotification? _selectedNotification;

    public DevelopmentView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DetailView.StateChanged += async (_, _) => await RefreshAsync();
    }

    private static App? AppInstance => Application.Current as App;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (AppInstance is not { } app) return;

        if (!_inboxHooked)
        {
            _inboxHooked = true;
            app.Inbox.Changed += (_, _) => Dispatcher.Invoke(RenderInbox);
        }

        RenderInbox();

        if (app.DevelopmentPullRequests is { } cached)
        {
            RenderPullRequests(cached);
            RenderActivity();
        }
        else
        {
            await LoadPullRequestsAsync();
        }
    }

    /// <summary>Refetch both lists — the header refresh button and post-action refresh.</summary>
    public async Task RefreshAsync()
    {
        if (AppInstance is { } app) _ = app.Inbox.RefreshAsync();
        await LoadPullRequestsAsync();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();

    // MARK: - Pull requests

    private async Task LoadPullRequestsAsync()
    {
        if (_loading || AppInstance is not { } app) return;
        _loading = true;
        LoadingRing.Visibility = Visibility.Visible;

        try
        {
            var config = app.Config;
            var tasks = new List<Task<PullRequestQueue>>();

            if (!string.IsNullOrWhiteSpace(config.AzureDevOps?.Organization))
            {
                tasks.Add(Task.Run(async () =>
                {
                    using var devops = new AzureDevOpsService(config.AzureDevOps!);
                    return await devops.GetDevelopmentPullRequestsAsync();
                }));
            }

            // GitHub always runs: the gh CLI token needs no config, and a
            // signed-out GitHub comes back as a soft error, not an exception.
            var gh = config.GitHubProviderOrDefault();
            var owners = new[] { gh.Organization, gh.Owner }.Where(o => !string.IsNullOrWhiteSpace(o)).Cast<string>();
            tasks.Add(Task.Run(async () =>
            {
                using var github = new GitHubPullRequestService(gh);
                return await github.GetDevelopmentPullRequestsAsync(owners);
            }));

            var queue = new PullRequestQueue();
            foreach (var result in await Task.WhenAll(tasks)) queue.Merge(result);

            app.DevelopmentPullRequests = queue;
            RenderPullRequests(queue);
            RenderActivity();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[code] Failed to load pull requests");
            PullRequestCount.Text = $"Could not load pull requests — {ex.Message}";
        }
        finally
        {
            _loading = false;
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderPullRequests(PullRequestQueue queue)
    {
        var scoped = queue.PullRequests
            .Where(pr => DevelopmentFilter.MatchesSource(pr, _source) && DevelopmentFilter.MatchesScope(pr, _scope))
            .ToList();

        RenderRepoChips(scoped);

        var visible = DevelopmentFilter.Apply(queue.PullRequests, _source, _scope, _repository, SearchBox.Text);
        var rows = visible.Select(pr => new DevelopmentPullRequestRowViewModel { PullRequest = pr }).ToList();

        var selectedId = (PullRequestList.SelectedItem as DevelopmentPullRequestRowViewModel)?.PullRequest.Id;

        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DevelopmentPullRequestRowViewModel.RepositoryKey)));
        PullRequestList.ItemsSource = view;

        if (selectedId != null)
            PullRequestList.SelectedItem = rows.FirstOrDefault(r => r.PullRequest.Id == selectedId);

        // Provider failures are stated in the count line; a short list that
        // looks complete is worse than one that says why.
        var errors = queue.Errors
            .Where(e => !PullRequestQueueView.IsExpectedSignedOut(e))
            .Select(e => $"{e.Source.ShortName()} unavailable");
        var errorText = string.Join(" · ", errors);

        PullRequestCount.Text = $"{rows.Count} open across {rows.Select(r => r.RepositoryKey).Distinct().Count()} repositories"
                                + (errorText.Length > 0 ? $" · {errorText}" : "");

        if (PullRequestsSegment.IsChecked == true)
        {
            EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = queue.IsEmpty ? "No open pull requests." : "Nothing matches these filters.";
        }
    }

    private void RenderRepoChips(List<UnifiedPullRequest> scoped)
    {
        var counts = DevelopmentFilter.RepositoryCounts(scoped);

        // A repository that vanished (refresh, scope change) must not keep filtering.
        if (_repository != null && counts.All(c => c.Repository != _repository)) _repository = null;

        RepoChipsPanel.Children.Clear();
        if (counts.Count < 2) return;

        foreach (var (repo, count) in counts.Take(RepoChipLimit))
        {
            var chip = new ToggleButton
            {
                Content = $"{repo.Split('/').Last()} {count}",
                ToolTip = repo,
                Tag = repo,
                IsChecked = repo == _repository,
                Padding = new Thickness(8, 1, 8, 1),
                FontSize = 10,
                Margin = new Thickness(0, 0, 4, 4),
            };
            chip.Click += OnRepoChipClicked;
            RepoChipsPanel.Children.Add(chip);
        }
    }

    private void Rerender()
    {
        if (AppInstance?.DevelopmentPullRequests is { } queue) RenderPullRequests(queue);
    }

    private void OnRepoChipClicked(object sender, RoutedEventArgs e)
    {
        var repo = (sender as ToggleButton)?.Tag as string;
        _repository = _repository == repo ? null : repo;
        Rerender();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Rerender();

    private void OnSourceClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<DevelopmentSourceFilter>(tag, out var source))
            _source = source;

        SourceAll.IsChecked = _source == DevelopmentSourceFilter.All;
        SourceDevOps.IsChecked = _source == DevelopmentSourceFilter.DevOps;
        SourceGitHub.IsChecked = _source == DevelopmentSourceFilter.GitHub;
        Rerender();
    }

    private void OnScopeClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<DevelopmentScope>(tag, out var scope))
            _scope = scope;

        ScopeEverything.IsChecked = _scope == DevelopmentScope.Everything;
        ScopeMine.IsChecked = _scope == DevelopmentScope.Mine;
        Rerender();
    }

    private async void OnPullRequestSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PullRequestList.SelectedItem is not DevelopmentPullRequestRowViewModel row) return;
        await ShowPullRequestAsync(row.PullRequest);
    }

    private async Task ShowPullRequestAsync(UnifiedPullRequest pr)
    {
        DetailPlaceholder.Visibility = Visibility.Collapsed;
        NonPullRequestPanel.Visibility = Visibility.Collapsed;
        DetailView.Visibility = Visibility.Visible;
        await DetailView.ShowAsync(pr);
    }

    // MARK: - Activity

    /// <summary>Show or collapse the activity sidebar; the page toolbar owns the toggle.</summary>
    public void ShowActivity(bool visible)
    {
        ActivityPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ActivitySplitter.Visibility = ActivityPanel.Visibility;
        ActivitySplitterColumn.Width = new GridLength(visible ? 6 : 0);
        ActivityColumn.Width = visible ? new GridLength(320) : new GridLength(0);
    }

    private void RenderActivity()
    {
        if (AppInstance?.DevelopmentPullRequests is not { } queue) return;

        var rows = DevelopmentFilter.Activity(queue.PullRequests, queue.ViewerNames, HideMineCheck.IsChecked == true);
        ActivityList.ItemsSource = rows;
        ActivityEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnHideMineChanged(object sender, RoutedEventArgs e)
    {
        if (IsInitialized) RenderActivity();
    }

    /// <summary>A comment opens its pull request in the centre, switching back to Pulls.</summary>
    private async void OnActivitySelected(object sender, SelectionChangedEventArgs e)
    {
        if (ActivityList.SelectedItem is not DevelopmentActivityRowViewModel row) return;

        PullRequestsSegment.IsChecked = true;
        var match = (PullRequestList.ItemsSource as System.Collections.IEnumerable)?
            .OfType<DevelopmentPullRequestRowViewModel>()
            .FirstOrDefault(r => r.PullRequest.Id == row.PullRequest.Id);

        if (match != null)
        {
            PullRequestList.SelectedItem = match;
            PullRequestList.ScrollIntoView(match);
        }
        else
        {
            // Filtered out of the list; show it anyway.
            await ShowPullRequestAsync(row.PullRequest);
        }
    }

    // MARK: - Segments

    private void OnSegmentChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        var inbox = InboxSegment.IsChecked == true;

        PullRequestFilters.Visibility = inbox ? Visibility.Collapsed : Visibility.Visible;
        PullRequestList.Visibility = inbox ? Visibility.Collapsed : Visibility.Visible;
        InboxHeader.Visibility = inbox ? Visibility.Visible : Visibility.Collapsed;
        InboxList.Visibility = inbox ? Visibility.Visible : Visibility.Collapsed;

        if (inbox) RenderInbox();
        else Rerender();
    }

    // MARK: - Inbox

    private void RenderInbox()
    {
        if (AppInstance is not { } app) return;
        var inbox = app.Inbox;

        var unread = inbox.UnreadCount;
        InboxSegment.Content = unread > 0 ? $"Inbox {unread}" : "Inbox";

        var selectedId = _selectedNotification?.Id;
        var rows = inbox.Notifications.Select(n => new DevelopmentNotificationRowViewModel { Notification = n }).ToList();
        InboxList.ItemsSource = rows;
        if (selectedId != null) InboxList.SelectedItem = rows.FirstOrDefault(r => r.Notification.Id == selectedId);

        InboxStatus.Text = inbox.LastError is { } error && inbox.Notifications.Count == 0
            ? $"GitHub inbox unavailable — {error}"
            : $"{unread} unread of {rows.Count}"
              + (inbox.LastRefreshed is { } at ? $" · updated {at:HH:mm}" : "")
              + (inbox.LastError != null ? " · last refresh failed" : "");

        if (InboxSegment.IsChecked == true)
        {
            EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = inbox.LastError != null ? "Could not reach GitHub notifications." : "Inbox zero.";
        }
    }

    private async void OnNotificationSelected(object sender, SelectionChangedEventArgs e)
    {
        if (InboxList.SelectedItem is not DevelopmentNotificationRowViewModel row || AppInstance is not { } app) return;
        var notification = row.Notification;
        if (notification.Id == _selectedNotification?.Id && DetailView.Visibility == Visibility.Visible) return;
        _selectedNotification = notification;

        if (notification.IsPullRequest && notification.SubjectNumber is { } number)
        {
            var gh = app.Config.GitHubProviderOrDefault();
            try
            {
                using var github = new GitHubPullRequestService(gh);
                var pr = await github.GetPullRequestAsync(notification.Owner, notification.RepositoryName, number);
                if (_selectedNotification?.Id != notification.Id) return;

                if (pr != null)
                {
                    await ShowPullRequestAsync(pr);
                    // Opening it is reading it — same as clicking through on github.com.
                    if (notification.Unread) await app.Inbox.MarkReadAsync(notification);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[code] Could not open {Repo}#{Number} in-app", notification.Repository, number);
            }
        }

        // Issues, releases, check suites and discussions have no in-app viewer
        // yet; say what it is and offer the browser.
        DetailPlaceholder.Visibility = Visibility.Collapsed;
        DetailView.Visibility = Visibility.Collapsed;
        NonPullRequestPanel.Visibility = Visibility.Visible;
        NonPullRequestTitle.Text = notification.SubjectTitle;
        NonPullRequestByline.Text = row.Byline;
    }

    private async void OnOpenNotificationInBrowser(object sender, RoutedEventArgs e)
    {
        if (_selectedNotification is not { } notification) return;

        try { Process.Start(new ProcessStartInfo(notification.WebUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "[code] Could not open {Url}", notification.WebUrl); }

        if (notification.Unread && AppInstance is { } app) await app.Inbox.MarkReadAsync(notification);
    }

    private async void OnMarkReadClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DevelopmentNotificationRowViewModel row || AppInstance is not { } app) return;
        var result = await app.Inbox.MarkReadAsync(row.Notification);
        if (!result.Success) InboxStatus.Text = $"Mark read failed — {result.Error}";
    }

    private async void OnUnsubscribeClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DevelopmentNotificationRowViewModel row || AppInstance is not { } app) return;
        var result = await app.Inbox.UnsubscribeAsync(row.Notification);
        if (!result.Success) InboxStatus.Text = $"Unsubscribe failed — {result.Error}";
    }

    private async void OnMarkAllReadClicked(object sender, RoutedEventArgs e)
    {
        if (AppInstance is not { } app) return;
        var result = await app.Inbox.MarkAllReadAsync();
        if (!result.Success) InboxStatus.Text = $"Mark all read failed — {result.Error}";
    }
}
