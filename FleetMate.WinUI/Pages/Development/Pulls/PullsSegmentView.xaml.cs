using FleetMate.Core.Shared;
using FleetMate.WinUI.Shell;
using FleetMate.Core.Config;
using FleetMate.Core.Development;
using FleetMate.Core.Links;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Serilog;
using Windows.System;
using Windows.UI.Core;

namespace FleetMate.WinUI.Pages.Development.Pulls;

/// <summary>
/// Development › Pulls: every open pull request on the operator's projects
/// across Azure DevOps and GitHub, filtered by scope, source, repository and the
/// toolbar search, sorted by last modified and optionally grouped by repository
/// (groups collapse and are remembered), with the selected one in the detail
/// pane. The rules are FleetMate.Core's <see cref="DevelopmentFilter"/>, shared
/// with FleetMate.GUI.
/// </summary>
public sealed partial class PullsSegmentView : UserControl, IDevelopmentSegmentView
{
    private DevelopmentPage? _page;
    private DevelopmentSourceFilter _source = DevelopmentSourceFilter.All;
    private DevelopmentScope _scope = DevelopmentScope.Everything;
    private string? _repository = UserPreferences.PullsRepository;
    private bool _oldestFirst = UserPreferences.PullsOldestFirst;
    private bool _grouped = UserPreferences.PullsGroupedByRepository;
    private string _search = "";
    private bool _shown;
    private bool _rendering;
    private List<PullRequestRow> _rows = new();
    private List<string> _groupKeys = new();
    private string _summary = "Pull requests";
    private GitHubNotification? _notification;

    /// <summary>A pull request link that failed before Azure DevOps sign-in finished, opened again once it does.</summary>
    private FleetMateLink? _linkAwaitingSignIn;

    public PullsSegmentView()
    {
        InitializeComponent();
        AgentHandOff.AttachDrag(List, item => item is PullRequestRow row ? AgentContexts.PullRequest(row.PullRequest) : null);
        PullGroupCollapse.Load(UserPreferences.PullsCollapsedGroups);
        UpdateSortTip();
        Detail.StateChanged += async (_, _) => await RefreshAsync();
    }

    public DevelopmentSegment Segment => DevelopmentSegment.Pulls;

    public bool IsBusy => App.Services.DevelopmentPullRequests == null
                          && App.Services.DevelopmentSync.IsLoading(DevelopmentRefreshArea.PullRequests);

    public event EventHandler? BusyChanged;

    public void Attach(DevelopmentPage page)
    {
        _page = page;
        var services = App.Services;
        services.CacheChanged += key =>
        {
            if (key != "PullRequests") return;
            BusyChanged?.Invoke(this, EventArgs.Empty);
            if (_shown) Render();
        };
        services.DevelopmentSync.StatusChanged += (_, _) =>
        {
            BusyChanged?.Invoke(this, EventArgs.Empty);
            if (_shown) UpdateCount();
        };
        services.DevOpsSignedIn += OnDevOpsSignedIn;
    }

    public void SetShown(bool shown)
    {
        _shown = shown;
        var sync = App.Services.DevelopmentSync;
        if (!shown)
        {
            sync.DetachView(this);
            return;
        }
        sync.AttachView(this, DevelopmentSegment.Pulls);
        Render();
        if (_rows.Count > 0 && List.SelectedItem == null && Detail.PullRequest == null && _notification == null) ShowPane(Placeholder);
    }

    public void ApplySearch(string text)
    {
        if (_search == text) return;
        _search = text;
        if (_shown) Render();
    }

    /// <summary>Refresh means fresh: GitHub data is rebuilt in full, not incrementally.</summary>
    public Task RefreshAsync()
    {
        GitHubSync.RequestFullResync();
        return App.Services.DevelopmentSync.RefreshNowAsync(DevelopmentRefreshArea.PullRequests);
    }

    // MARK: - Render

    private void Render()
    {
        if (App.Services.DevelopmentPullRequests is not { } queue)
        {
            List.ItemsSource = null;
            EmptyText.Visibility = Visibility.Collapsed;
            UpdateCount();
            return;
        }

        var scoped = queue.PullRequests
            .Where(pr => DevelopmentFilter.MatchesSource(pr, _source) && DevelopmentFilter.MatchesScope(pr, _scope))
            .ToList();
        _repository = RepoFilter.SetEntries(
            RepositoryFilterHierarchy.Count(scoped, pr => new RepositoryFilterScope(pr.Source, pr.Container, pr.Repository)),
            _repository);

        var visible = DevelopmentFilter.Apply(queue.PullRequests, _source, _scope, _repository, _search, _oldestFirst);
        var selectedId = (List.SelectedItem as PullRequestRow)?.PullRequest.Id ?? Detail.PullRequest?.Id;

        _rendering = true;
        try
        {
            if (_grouped)
            {
                var groups = DevelopmentFilter.GroupByRepository(visible, _oldestFirst)
                    .Select(g => new PullRequestGroup(g.Repository,
                        g.Rows.Select(pr => new PullRequestRow(pr, showRepository: false)).ToList(),
                        PullGroupCollapse.Collapsed.Contains(g.Repository)))
                    .ToList();
                _groupKeys = groups.Select(g => g.Key).ToList();
                _rows = groups.SelectMany(g => g).ToList();
                GroupedPulls.Source = groups;
                List.ItemsSource = GroupedPulls.View;
                _summary = DevelopmentText.PullsSummary(groups.Sum(g => g.Total), groups.Count, queue.Errors);
            }
            else
            {
                _groupKeys = new();
                _rows = visible.Select(pr => new PullRequestRow(pr, showRepository: true)).ToList();
                List.ItemsSource = _rows;
                _summary = DevelopmentText.PullsSummary(_rows.Count, _rows.Select(r => r.RepositoryKey).Distinct().Count(), queue.Errors);
            }
            if (selectedId != null) List.SelectedItem = _rows.FirstOrDefault(r => r.PullRequest.Id == selectedId);
        }
        finally
        {
            _rendering = false;
        }

        UpdateCount();
        var empty = visible.Count == 0;
        EmptyText.Visibility = DevelopmentUi.Show(empty);
        EmptyText.Text = queue.IsEmpty ? "No open pull requests." : "Nothing matches these filters.";
    }

    private void UpdateCount() =>
        CountText.Text = _summary + App.Services.DevelopmentSync.StatusSuffix(DevelopmentRefreshArea.PullRequests);

    // MARK: - Filters

    private void Scope_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<DevelopmentScope>(tag, out var scope)) _scope = scope;
        SyncChips();
        Render();
    }

    private void Source_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<DevelopmentSourceFilter>(tag, out var source)) _source = source;
        // A repository picked under the old source means nothing under the new one.
        PickRepository(null);
        SyncChips();
        Render();
    }

    private void SyncChips()
    {
        ScopeEverything.IsChecked = _scope == DevelopmentScope.Everything;
        ScopeMine.IsChecked = _scope == DevelopmentScope.Mine;
        SourceAll.IsChecked = _source == DevelopmentSourceFilter.All;
        SourceDevOps.IsChecked = _source == DevelopmentSourceFilter.DevOps;
        SourceGitHub.IsChecked = _source == DevelopmentSourceFilter.GitHub;
    }

    private void RepoFilter_SelectionChanged(object? sender, EventArgs e)
    {
        if (RepoFilter.Selection == _repository) return;
        PickRepository(RepoFilter.Selection);
        Render();
    }

    /// <summary>A repository the person chose, remembered for the next launch.</summary>
    private void PickRepository(string? repository)
    {
        _repository = repository;
        UserPreferences.SetPullsRepository(repository);
    }

    // MARK: - Sort and group

    private void SortMenu_Opening(object sender, object e)
    {
        SortNewest.IsChecked = !_oldestFirst;
        SortOldest.IsChecked = _oldestFirst;
        GroupToggle.IsChecked = _grouped;
        GroupSeparator.Visibility = ExpandAllItem.Visibility = CollapseAllItem.Visibility = DevelopmentUi.Show(_grouped);
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        _oldestFirst = (sender as FrameworkElement)?.Tag as string == "Oldest";
        UserPreferences.SetPullsOldestFirst(_oldestFirst);
        UpdateSortTip();
        Render();
    }

    private void Group_Click(object sender, RoutedEventArgs e)
    {
        _grouped = !_grouped;
        UserPreferences.SetPullsGroupedByRepository(_grouped);
        UpdateSortTip();
        Render();
    }

    private void UpdateSortTip() =>
        ToolTipService.SetToolTip(SortButton, $"Sorted by {(_oldestFirst ? "least" : "most")} recently modified"
                                              + (_grouped ? ", grouped by repository" : ""));

    private void ExpandAll_Click(object sender, RoutedEventArgs e) =>
        ApplyGroups(PullGroupCollapse.SetAll(PullGroupCollapse.Collapsed, false, _groupKeys));

    private void CollapseAll_Click(object sender, RoutedEventArgs e) =>
        ApplyGroups(PullGroupCollapse.SetAll(PullGroupCollapse.Collapsed, true, _groupKeys));

    /// <summary>A header click collapses or expands that group; Ctrl-click every group.</summary>
    private void GroupHeader_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        var all = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        ApplyGroups(PullGroupCollapse.Toggle(PullGroupCollapse.Collapsed, key, all, _groupKeys));
    }

    private void GroupHeader_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        args.Handled = true;
        var menu = new MenuFlyout();
        var toggle = new MenuFlyoutItem { Text = PullGroupCollapse.ToggleLabel(PullGroupCollapse.Collapsed.Contains(key)) };
        toggle.Click += (_, _) => ApplyGroups(PullGroupCollapse.Toggle(PullGroupCollapse.Collapsed, key, all: false, _groupKeys));
        menu.Items.Add(toggle);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Expand All Groups", () => ExpandAll_Click(this, new RoutedEventArgs())));
        menu.Items.Add(Item("Collapse All Groups", () => CollapseAll_Click(this, new RoutedEventArgs())));
        ShowMenu(menu, sender, args);
    }

    private void ApplyGroups(HashSet<string> collapsed)
    {
        PullGroupCollapse.Set(collapsed);
        UserPreferences.SetPullsCollapsedGroups(PullGroupCollapse.Format(collapsed));
        Render();
    }

    // MARK: - Selection and detail

    private async void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering || List.SelectedItem is not PullRequestRow row) return;
        if (Detail.PullRequest?.Id == row.PullRequest.Id && Detail.Visibility == Visibility.Visible) return;
        await ShowDetailAsync(row.PullRequest);
    }

    private async Task ShowDetailAsync(UnifiedPullRequest pr)
    {
        _notification = null;
        DevelopmentContext.PullRequest(pr);
        ShowPane(Detail);
        await Detail.ShowAsync(pr);
    }

    /// <summary>One thing in the detail pane at a time.</summary>
    private void ShowPane(FrameworkElement visible)
    {
        foreach (var pane in new FrameworkElement[] { Placeholder, WaitHost, NotificationPanel, Detail })
            pane.Visibility = DevelopmentUi.Show(ReferenceEquals(pane, visible));
    }

    /// <summary>Every filter back to everything, so the item asked for can't be hidden.</summary>
    private void ClearFilters()
    {
        _source = DevelopmentSourceFilter.All;
        _scope = DevelopmentScope.Everything;
        _repository = null;
        SyncChips();
    }

    /// <summary>Pulls filtered to one repository — a repository bar's click.</summary>
    public void ShowRepository(string repository)
    {
        _source = DevelopmentSourceFilter.All;
        PickRepository(repository);
        SyncChips();
        _page?.ShowSegment(DevelopmentSegment.Pulls);
        Render();
    }

    /// <summary>Pulls filtered to one source, any repository — the pull request figures' click.</summary>
    public void ShowSource(DevelopmentSourceFilter source)
    {
        _source = source;
        PickRepository(null);
        SyncChips();
        _page?.ShowSegment(DevelopmentSegment.Pulls);
        Render();
    }

    /// <summary>Open one pull request, filters cleared so its row is in the list.</summary>
    public void ShowPullRequest(UnifiedPullRequest pr) => _ = ShowPullRequestAsync(pr);

    private async Task ShowPullRequestAsync(UnifiedPullRequest pr)
    {
        ClearFilters();
        _page?.ShowSegment(DevelopmentSegment.Pulls);
        Render();
        if (_rows.FirstOrDefault(r => r.PullRequest.Id == pr.Id) is { } match)
        {
            List.SelectedItem = match;
            List.ScrollIntoView(match);
        }
        else
        {
            await ShowDetailAsync(pr);
        }
    }

    /// <summary>Select the first row and wait for its detail: the QA capture's detail view.</summary>
    public async Task SelectFirstAsync()
    {
        if (_rows.FirstOrDefault() is not { } row) return;
        _rendering = true;
        List.SelectedItem = row;
        _rendering = false;
        await ShowDetailAsync(row.PullRequest);
    }

    // MARK: - Inbox threads

    /// <summary>
    /// A thread picked in the Inbox popover. A pull request opens in the detail
    /// pane (and reading it marks it read, as on github.com); anything else says
    /// what it is and offers the browser.
    /// </summary>
    public async Task ShowNotificationAsync(NotificationRow row)
    {
        var notification = row.Notification;
        _notification = notification;
        _page?.ShowSegment(DevelopmentSegment.Pulls);

        if (notification.IsPullRequest && notification.SubjectNumber is { } number)
        {
            try
            {
                var pr = App.Services.DemoMode
                    ? App.Services.DevelopmentPullRequests?.PullRequests.FirstOrDefault(p => p.Number == number)
                    : await FetchGitHubPullRequestAsync(notification.Owner, notification.RepositoryName, number);
                if (_notification?.Id != notification.Id) return;
                if (pr != null)
                {
                    List.SelectedItem = _rows.FirstOrDefault(r => r.PullRequest.Id == pr.Id);
                    await ShowDetailAsync(pr);
                    if (notification.Unread && !App.Services.DemoMode) await App.Services.Inbox.MarkReadAsync(notification);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[development] Could not open {Repo}#{Number} in-app", notification.Repository, number);
            }
        }

        // Issues, releases, check suites and discussions have no in-app viewer yet.
        List.SelectedItem = null;
        ShowPane(NotificationPanel);
        NotificationTitle.Text = notification.SubjectTitle;
        NotificationByline.Text = row.Byline;
    }

    private async void OpenNotification_Click(object sender, RoutedEventArgs e)
    {
        if (_notification is not { } notification) return;
        DevelopmentUi.OpenUrl(notification.WebUrl);
        if (notification.Unread && !App.Services.DemoMode) await App.Services.Inbox.MarkReadAsync(notification);
    }

    private static async Task<UnifiedPullRequest?> FetchGitHubPullRequestAsync(string owner, string repo, int number)
    {
        using var github = new GitHubPullRequestService(App.Services.Config.GitHubProviderOrDefault());
        return await github.GetPullRequestAsync(owner, repo, number);
    }

    // MARK: - Links

    /// <summary>A <c>fleetmate:</c> pull request link: clear the filters and select it, fetching it when it isn't loaded.</summary>
    public async Task OpenLinkAsync(FleetMateLink link)
    {
        if (link is not FleetMateLink.PullRequest prLink) return;
        _linkAwaitingSignIn = null;
        ClearFilters();
        var services = App.Services;
        var startedSignedIn = services.IsDevOpsSignedIn;
        if (services.DevelopmentPullRequests is null) await RefreshAsync();
        Render();

        if (_rows.FirstOrDefault(r => Matches(r.PullRequest, prLink)) is { } row)
        {
            List.SelectedItem = row;
            List.ScrollIntoView(row);
            return;
        }

        UnifiedPullRequest? pr = null;
        try { pr = await FetchAsync(prLink); }
        catch (Exception ex) { Log.Information(ex, "[development] Fetching {Link} failed", link.ToLink()); }
        if (pr != null)
        {
            await ShowDetailAsync(pr);
            return;
        }

        var retry = link.NeedsAzureDevOps() ? services.DevOpsRetry(startedSignedIn) : SignInRetry.ShowError;
        switch (retry)
        {
            case SignInRetry.RetryNow:
                await OpenLinkAsync(link);
                break;
            case SignInRetry.WaitForSignIn:
                _linkAwaitingSignIn = link;
                WaitHost.Content = new DevOpsSignInWaitPanel("the linked pull request");
                ShowPane(WaitHost);
                break;
            default:
                _page?.ShowLinkNotFound(link);
                break;
        }
    }

    /// <summary>Signed in to Azure DevOps: reload what loaded without it, then open a held link.</summary>
    private async void OnDevOpsSignedIn()
    {
        if (_shown && App.Services.DevelopmentPullRequests != null) await RefreshAsync();
        if (_linkAwaitingSignIn is not { } link) return;
        _linkAwaitingSignIn = null;
        try { await OpenLinkAsync(link); }
        catch (Exception ex) { Log.Warning(ex, "Could not open {Link}", link.ToLink()); }
    }

    private static bool Matches(UnifiedPullRequest pr, FleetMateLink.PullRequest link) => link.Source switch
    {
        FleetMateLink.AzureDevOps a => pr.Source == PullRequestSource.AzureDevOps && pr.Number == link.Number
            && Same(pr.Container, a.Project) && Same(pr.Repository, a.Repo),
        FleetMateLink.GitHub g => pr.Source == PullRequestSource.GitHub && pr.Number == link.Number
            && Same(pr.Container, g.Owner) && Same(pr.Repository, g.Repo),
        _ => false,
    };

    private static async Task<UnifiedPullRequest?> FetchAsync(FleetMateLink.PullRequest link)
    {
        if (App.Services.DemoMode) return null;
        switch (link.Source)
        {
            case FleetMateLink.AzureDevOps a when App.Services.DevOpsService is { } devOps:
                return await devOps.GetPullRequestAsync(a.Project, a.Repo, link.Number);
            case FleetMateLink.GitHub g:
                return await FetchGitHubPullRequestAsync(g.Owner, g.Repo, link.Number);
            default:
                return null;
        }
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // MARK: - Row menu

    private void List_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if ((args.OriginalSource as FrameworkElement)?.DataContext is not PullRequestRow row) return;
        args.Handled = true;
        var pr = row.PullRequest;
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Open in Browser", () => DevelopmentUi.OpenUrl(pr.WebUrl), ""));
        menu.Items.Add(Item("Copy Link", () => DevelopmentUi.Copy(pr.WebUrl), ""));
        menu.Items.Add(Item(row.CopyIdLabel, () => DevelopmentUi.Copy(pr.Number.ToString()), ""));
        AgentHandOff.Prepend(menu, () => [AgentContexts.PullRequest(pr)], args.OriginalSource as FrameworkElement);
        ShowMenu(menu, args.OriginalSource as UIElement ?? sender, args);
    }

    private static MenuFlyoutItem Item(string text, Action action, string? glyph = null)
    {
        var item = new MenuFlyoutItem { Text = text };
        if (glyph != null) item.Icon = new FontIcon { Glyph = glyph };
        item.Click += (_, _) => action();
        return item;
    }

    private static void ShowMenu(MenuFlyout menu, UIElement target, ContextRequestedEventArgs args)
    {
        menu.ShowAt(target, args.TryGetPosition(target, out var point)
            ? new FlyoutShowOptions { Position = point }
            : new FlyoutShowOptions());
    }
}
