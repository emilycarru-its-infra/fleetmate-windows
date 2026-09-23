using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.GUI.Views.Shared;
using Serilog;

namespace FleetMate.GUI.Views.Projects.Code;

/// <summary>
/// Code: every open pull request on the operator's projects across Azure DevOps
/// and GitHub, plus the GitHub inbox, with the selected item shown inline.
///
/// Self-contained on purpose — it owns its data loading and reads its caches
/// from <see cref="App"/> — so it can move to its own top-level tab later
/// without touching the Projects page beyond removing the pill.
/// </summary>
public partial class CodeView : UserControl
{
    private const int RepoChipLimit = 12;

    private CodeSourceFilter _source = CodeSourceFilter.All;
    private CodeScope _scope = CodeScope.Everything;
    private string? _repository;
    private bool _loading;
    private bool _inboxHooked;
    private GitHubNotification? _selectedNotification;

    public CodeView()
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

        if (app.CodePullRequests is { } cached) RenderPullRequests(cached);
        else await LoadPullRequestsAsync();
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
                    return await devops.GetCodePullRequestsAsync();
                }));
            }

            // GitHub always runs: the gh CLI token needs no config, and a
            // signed-out GitHub comes back as a soft error, not an exception.
            var gh = config.GitHubProviderOrDefault();
            var owners = new[] { gh.Organization, gh.Owner }.Where(o => !string.IsNullOrWhiteSpace(o)).Cast<string>();
            tasks.Add(Task.Run(async () =>
            {
                using var github = new GitHubPullRequestService(gh);
                return await github.GetCodePullRequestsAsync(owners);
            }));

            var queue = new PullRequestQueue();
            foreach (var result in await Task.WhenAll(tasks)) queue.Merge(result);

            app.CodePullRequests = queue;
            RenderPullRequests(queue);
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
            .Where(pr => CodeFilter.MatchesSource(pr, _source) && CodeFilter.MatchesScope(pr, _scope))
            .ToList();

        RenderRepoChips(scoped);

        var visible = CodeFilter.Apply(queue.PullRequests, _source, _scope, _repository, SearchBox.Text);
        var rows = visible.Select(pr => new CodePullRequestRowViewModel { PullRequest = pr }).ToList();

        var selectedId = (PullRequestList.SelectedItem as CodePullRequestRowViewModel)?.PullRequest.Id;

        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CodePullRequestRowViewModel.RepositoryKey)));
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
        var counts = CodeFilter.RepositoryCounts(scoped);

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
        if (AppInstance?.CodePullRequests is { } queue) RenderPullRequests(queue);
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
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<CodeSourceFilter>(tag, out var source))
            _source = source;

        SourceAll.IsChecked = _source == CodeSourceFilter.All;
        SourceDevOps.IsChecked = _source == CodeSourceFilter.DevOps;
        SourceGitHub.IsChecked = _source == CodeSourceFilter.GitHub;
        Rerender();
    }

    private void OnScopeClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<CodeScope>(tag, out var scope))
            _scope = scope;

        ScopeEverything.IsChecked = _scope == CodeScope.Everything;
        ScopeMine.IsChecked = _scope == CodeScope.Mine;
        Rerender();
    }

    private async void OnPullRequestSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PullRequestList.SelectedItem is not CodePullRequestRowViewModel row) return;
        await ShowPullRequestAsync(row.PullRequest);
    }

    private async Task ShowPullRequestAsync(UnifiedPullRequest pr)
    {
        DetailPlaceholder.Visibility = Visibility.Collapsed;
        NonPullRequestPanel.Visibility = Visibility.Collapsed;
        DetailView.Visibility = Visibility.Visible;
        await DetailView.ShowAsync(pr);
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
        InboxBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        InboxBadgeText.Text = unread > 99 ? "99+" : unread.ToString();

        var selectedId = _selectedNotification?.Id;
        var rows = inbox.Notifications.Select(n => new CodeNotificationRowViewModel { Notification = n }).ToList();
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
        if (InboxList.SelectedItem is not CodeNotificationRowViewModel row || AppInstance is not { } app) return;
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
        if ((sender as FrameworkElement)?.Tag is not CodeNotificationRowViewModel row || AppInstance is not { } app) return;
        var result = await app.Inbox.MarkReadAsync(row.Notification);
        if (!result.Success) InboxStatus.Text = $"Mark read failed — {result.Error}";
    }

    private async void OnUnsubscribeClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CodeNotificationRowViewModel row || AppInstance is not { } app) return;
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
