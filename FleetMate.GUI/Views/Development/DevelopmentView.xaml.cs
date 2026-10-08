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
        RunView.RunChanged += async (_, _) => await LoadRunsAsync();
        StartTimers();
        HookLinks();
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
        }
        else
        {
            await LoadPullRequestsAsync();
        }
    }

    /// <summary>
    /// Refetch everything already loaded — the page toolbar's Refresh, the
    /// 15-minute cycle, and after a PR action.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (AppInstance is not { } app) return;
        _ = app.Inbox.RefreshAsync();

        var work = new List<Task> { LoadPullRequestsAsync() };
        if (app.DevelopmentCommits != null) work.Add(LoadCommitsAsync());
        if (app.DevelopmentRuns != null) work.Add(LoadRunsAsync());
        await Task.WhenAll(work);
    }

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
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[development] Failed to load pull requests");
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

        _repository = RepoFilterMenu.Fill(PullsRepoCombo,
            RepoFilterMenu.Counts(scoped, DevelopmentFilter.RepositoryKey), _repository);

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

    private void Rerender()
    {
        if (AppInstance?.DevelopmentPullRequests is { } queue) RenderPullRequests(queue);
    }

    /// <summary>Show Pulls filtered to one repository — the Development widget's bar click.</summary>
    public void ShowRepository(string repository)
    {
        _source = DevelopmentSourceFilter.All;
        SourceAll.IsChecked = true;
        SourceDevOps.IsChecked = SourceGitHub.IsChecked = false;
        _repository = repository;
        PullRequestsSegment.IsChecked = true;
        Rerender();
    }

    private void OnPullsRepoChanged(object sender, SelectionChangedEventArgs e)
    {
        var repo = RepoFilterMenu.Picked(PullsRepoCombo, out var changed);
        if (!changed || repo == _repository) return;
        _repository = repo;
        Rerender();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Rerender();

    private void OnSourceClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<DevelopmentSourceFilter>(tag, out var source))
            _source = source;

        // A repository picked under the old source means nothing under the new one.
        _repository = null;

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
        ShowDetail(DetailView);
        await DetailView.ShowAsync(pr);
    }

    /// <summary>One thing in the centre pane at a time.</summary>
    private void ShowDetail(FrameworkElement visible)
    {
        foreach (var pane in new FrameworkElement[] { DetailPlaceholder, NonPullRequestPanel, DetailView, CommitView, RunView, SkillDetail })
            pane.Visibility = pane == visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Open one pull request in Pulls — a Recent Activity comment row's deep
    /// link. Filters are cleared so the row is in the list to select.
    /// </summary>
    public async void ShowPullRequest(UnifiedPullRequest pr)
    {
        _source = DevelopmentSourceFilter.All;
        _scope = DevelopmentScope.Everything;
        _repository = null;
        SourceAll.IsChecked = ScopeEverything.IsChecked = true;
        SourceDevOps.IsChecked = SourceGitHub.IsChecked = ScopeMine.IsChecked = false;
        SearchBox.Text = "";
        PullRequestsSegment.IsChecked = true;
        Rerender();

        var match = (PullRequestList.ItemsSource as System.Collections.IEnumerable)?
            .OfType<DevelopmentPullRequestRowViewModel>()
            .FirstOrDefault(r => r.PullRequest.Id == pr.Id);

        if (match != null)
        {
            PullRequestList.SelectedItem = match;
            PullRequestList.ScrollIntoView(match);
        }
        else
        {
            await ShowPullRequestAsync(pr);
        }
    }

    // MARK: - Segments

    private async void OnSegmentChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;

        var pulls = PullRequestsSegment.IsChecked == true;
        var inbox = InboxSegment.IsChecked == true;
        var commits = CommitsSegment.IsChecked == true;
        var pipelines = PipelinesSegment.IsChecked == true;
        var skills = SkillsSegment.IsChecked == true;

        static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        PullRequestFilters.Visibility = Show(pulls);
        PullRequestList.Visibility = Show(pulls);
        InboxHeader.Visibility = Show(inbox);
        InboxList.Visibility = Show(inbox);
        CommitsFilters.Visibility = Show(commits);
        CommitsScroller.Visibility = Show(commits);
        PipelinesFilters.Visibility = Show(pipelines);
        PipelinesList.Visibility = Show(pipelines);
        SkillsFilters.Visibility = Show(skills);
        SkillsList.Visibility = Show(skills);
        EmptyText.Visibility = Visibility.Collapsed;

        if (inbox) RenderInbox();
        else if (pulls) Rerender();
        else if (commits)
        {
            if (AppInstance?.DevelopmentCommits is null) await LoadCommitsAsync();
            else RenderCommits();
        }
        else if (pipelines)
        {
            if (AppInstance?.DevelopmentRuns is null) await LoadRunsAsync();
            else RenderRuns();
        }
        else if (skills) RenderSkills();
    }

    // MARK: - Inbox

    private void RenderInbox()
    {
        if (AppInstance is not { } app) return;
        var inbox = app.Inbox;

        var unread = inbox.UnreadCount;

        // Shown only while there is something unread — and kept while it is
        // the open segment, so marking everything read does not yank the page.
        InboxSegment.Visibility = unread > 0 || InboxSegment.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MarkAllReadButton.IsEnabled = unread > 0;

        var selectedId = _selectedNotification?.Id;
        var rows = DevelopmentFilter.Inbox(inbox.Notifications, _showReadNotifications)
            .Select(n => new DevelopmentNotificationRowViewModel { Notification = n }).ToList();
        InboxList.ItemsSource = rows;
        if (selectedId != null) InboxList.SelectedItem = rows.FirstOrDefault(r => r.Notification.Id == selectedId);

        InboxStatus.Text = inbox.LastError is { } error && inbox.Notifications.Count == 0
            ? $"GitHub inbox unavailable — {error}"
            : (inbox.LastRefreshed is { } at ? $"Checked {at:HH:mm}" : "")
              + (inbox.LastError != null ? " · last refresh failed" : "");

        if (InboxSegment.IsChecked == true)
        {
            EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = inbox.LastError != null ? "Could not reach GitHub notifications."
                : _showReadNotifications ? "No notifications." : "Inbox zero.";
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
                Log.Warning(ex, "[development] Could not open {Repo}#{Number} in-app", notification.Repository, number);
            }
        }

        // Issues, releases, check suites and discussions have no in-app viewer
        // yet; say what it is and offer the browser.
        ShowDetail(NonPullRequestPanel);
        NonPullRequestTitle.Text = notification.SubjectTitle;
        NonPullRequestByline.Text = row.Byline;
    }

    private async void OnOpenNotificationInBrowser(object sender, RoutedEventArgs e)
    {
        if (_selectedNotification is not { } notification) return;

        try { Process.Start(new ProcessStartInfo(notification.WebUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "[development] Could not open {Url}", notification.WebUrl); }

        if (notification.Unread && AppInstance is { } app) await app.Inbox.MarkReadAsync(notification);
    }

    private async void OnMarkReadClicked(object sender, RoutedEventArgs e)
    {
        if (RowOf<DevelopmentNotificationRowViewModel>(sender) is not { } row || AppInstance is not { } app) return;
        var result = await app.Inbox.MarkReadAsync(row.Notification);
        if (!result.Success) InboxStatus.Text = $"Mark read failed — {result.Error}";
    }

    private async void OnUnsubscribeClicked(object sender, RoutedEventArgs e)
    {
        if (RowOf<DevelopmentNotificationRowViewModel>(sender) is not { } row || AppInstance is not { } app) return;
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
