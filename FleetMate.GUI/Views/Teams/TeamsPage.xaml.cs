using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FleetMate.Core.Config;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Teams;
using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Teams;

/// <summary>Search remains the primary action; the indexed channel directory makes the initial scope visible.</summary>
public partial class TeamsPage : Page, ITabSearch, IDisposable
{
    private readonly FleetMateConfig _config;
    private readonly TeamsSearchService _service;
    private bool _disposed;
    private IReadOnlyList<TeamsChannel> _channels = Array.Empty<TeamsChannel>();
    private HashSet<string> _authorizedChannels = new(StringComparer.Ordinal);
    private readonly HashSet<(string Channel, string Thread)> _unavailableThreads = new();
    private CancellationTokenSource? _channelsRequest;
    private CancellationTokenSource? _searchRequest;
    private CancellationTokenSource? _threadRequest;
    private Scope _scope = new(null, null, "All channels");
    private TeamsSearchHit? _selectedHit;
    private TeamsThread? _thread;
    private TeamsSavedThreads? _savedStore;
    private string? _savedError;
    private HashSet<string> _following = new(StringComparer.Ordinal);
    private IReadOnlyList<TeamsSavedThread> _savedItems = Array.Empty<TeamsSavedThread>();
    private bool _channelsLoaded;
    private bool _defaultsApplied;
    private bool _buildingTree;
    private bool _sidebarShown = true;
    private double _sidebarWidth = 240;
    private bool _hasSearched;
    private TeamsBrowseState _browse = new();
    private bool _channelError;

    private IReadOnlyList<TeamsSavedThread> VisibleSaved => !_channelsLoaded ? Array.Empty<TeamsSavedThread>()
        : _savedItems.Where(item => _authorizedChannels.Contains(item.Hit.ChannelId) && !_unavailableThreads.Contains((item.Hit.ChannelId, item.Hit.ThreadId))).ToList();

    private sealed record Scope(string? Team, string? Channel, string Title, string? Collection = null, bool Following = false);
    private sealed record ResultRow(TeamsSearchHit Hit)
    {
        public string Title => TeamsText.OneLine(string.IsNullOrWhiteSpace(Hit.Subject) ? Hit.Snippet : Hit.Subject);
        public string Location => TeamsText.OneLine($"{Hit.TeamName} › {Hit.ChannelName}");
        public string Snippet => TeamsText.TerminalSafe(Hit.Snippet);
        public string Activity => $"{Hit.ReplyCount} replies · {TeamsText.LocalTime(Hit.LastActivityAt)}";
    }

    public TeamsPage() : this((Application.Current as App)?.Config ?? new FleetMateConfig()) { }

    internal TeamsPage(FleetMateConfig config, TeamsSearchService? service = null, TeamsSavedThreads? savedStore = null)
    {
        InitializeComponent();
        _config = config;
        _service = service ?? TeamsSearchService.ForWorkspace(_config, EntraTokenSource.Shared?.WindowsUpn() ?? EntraTokenSource.ResolveWindowsUpn());
        if (savedStore != null)
        {
            _savedStore = savedStore;
            _savedItems = savedStore.Load();
            _following = savedStore.FollowedChannels().ToHashSet(StringComparer.Ordinal);
        }
        BuildChannelTree();
        Loaded += async (_, _) =>
        {
            var savedTask = LoadSavedThreadsAsync();
            if (!_channelsLoaded) await LoadChannelsAsync();
            await savedTask;
            UpdateLayoutMode();
        };
        SizeChanged += (_, _) => UpdateLayoutMode();
    }

    public TabSearchScope? SearchScope => new(QueryBox, _scope.Collection != null ? "Filter saved threads" : _scope.Following ? "Search Following" : "Search Teams channels", () => _ = SearchAsync());
    public event EventHandler? SearchScopeChanged;

    internal async Task LoadChannelsAsync()
    {
        if (_disposed) return;
        if (!_service.IsConfigured)
        {
            StatusText.Text = "Teams search needs a connection. Ask your administrator to configure the Teams search service.";
            return;
        }
        _channelsRequest?.Cancel();
        var request = _channelsRequest = new CancellationTokenSource();
        StatusText.Text = "Loading channels…";
        RetryButton.Visibility = Visibility.Collapsed;
        try
        {
            var payload = await _service.GetChannelsAsync(request.Token);
            if (request.IsCancellationRequested) return;
            _channels = payload.Value.Channels;
            _authorizedChannels = _channels.Select(channel => channel.ChannelId).ToHashSet(StringComparer.Ordinal);
            _channelsLoaded = true;
            _channelError = false;
            if ((_scope.Channel != null && !_authorizedChannels.Contains(_scope.Channel))
                || (_scope.Channel == null && _scope.Team != null && !_channels.Any(channel => channel.TeamId == _scope.Team)))
            {
                ClearRemoteContent();
                _scope = new Scope(null, null, "All channels");
            }
            BuildChannelTree();
            if (_scope.Collection != null) ShowSavedThreads();
            else if (_scope.Team != null || _scope.Channel != null || _scope.Following || !string.IsNullOrWhiteSpace(QueryBox.Text)) await SearchAsync();
            else ShowBrowseGuidance(false);
            if (_channels.Count == 0) ChannelSummary.Text = "No channels are available for this account in the search index.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (request.IsCancellationRequested) return;
            _channelError = true;
            if (IsAccessDenied(ex)) InvalidateChannelAccess();
            StatusText.Text = "Couldn't load channels. " + TeamsText.TerminalSafe(ex.Message);
            RetryButton.Visibility = Visibility.Visible;
        }
    }

    private void BuildChannelTree()
    {
        _buildingTree = true;
        try
        {
            ChannelTree.Items.Clear();
            var visibleSaved = VisibleSaved;
            if (!string.IsNullOrEmpty(_scope.Collection) && !visibleSaved.Any(item => item.Collection == _scope.Collection))
            {
                _scope = new Scope(null, null, "Saved threads", "");
                SearchScopeChanged?.Invoke(this, EventArgs.Empty);
            }
            var saved = new TreeViewItem { Header = $"Saved threads ({visibleSaved.Count})", Tag = new Scope(null, null, "Saved threads", ""), IsExpanded = true };
            foreach (var collection in visibleSaved.Select(item => item.Collection).Distinct().OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
                saved.Items.Add(new TreeViewItem { Header = collection, Tag = new Scope(null, null, collection, collection) });
            ChannelTree.Items.Add(saved);
            var filteredChannels = TeamsChannelScope.Filter(_channels, ChannelFilterBox.Text);
            ChannelFilterSummary.Text = filteredChannels.Count == _channels.Count ? $"{_channels.Count} channels" : $"{filteredChannels.Count} of {_channels.Count} channels";
            var all = new TreeViewItem { Header = "All channels", Tag = new Scope(null, null, "All channels") };
            ChannelTree.Items.Add(all);
            var following = new TreeViewItem { Header = $"Following ({_following.Count(id => _authorizedChannels.Contains(id))})", Tag = new Scope(null, null, "Following", Following: true), IsExpanded = true };
            var known = _channels.GroupBy(channel => channel.ChannelId).ToDictionary(group => group.Key, group => group.First());
            foreach (var id in _following.OrderBy(id => known.TryGetValue(id, out var channel) ? channel.TeamName + channel.ChannelName : id, StringComparer.CurrentCultureIgnoreCase))
            {
                if (known.TryGetValue(id, out var channel))
                {
                    if (TeamsChannelScope.Filter(new[] { channel }, ChannelFilterBox.Text).Count > 0) following.Items.Add(ChannelItem(channel, true));
                }
            }
            ChannelTree.Items.Add(following);
            TreeViewItem initial = all;
            var pinned = TeamsChannelScope.Pinned(filteredChannels, _config.TeamsDefaultChannels);
            if (pinned.Count > 0)
            {
                var group = new TreeViewItem { Header = "Pinned channels", IsExpanded = true };
                foreach (var channel in pinned) group.Items.Add(ChannelItem(channel, true));
                ChannelTree.Items.Add(group);
            }
            foreach (var team in filteredChannels.GroupBy(c => c.TeamId)
                         .OrderBy(g => g.Key == _config.TeamsDefaultTeam?.Trim() ? 0 : 1)
                         .ThenBy(g => g.First().TeamName, StringComparer.CurrentCultureIgnoreCase))
            {
                var name = TeamsText.OneLine(team.First().TeamName);
                var item = new TreeViewItem { Header = name, Tag = new Scope(team.Key, null, name), IsExpanded = true };
                foreach (var channel in team.OrderBy(c => c.ChannelName, StringComparer.CurrentCultureIgnoreCase))
                    item.Items.Add(ChannelItem(channel, false));
                ChannelTree.Items.Add(item);
                if (team.Key == _scope.Team && _scope.Channel == null
                    || !_defaultsApplied && team.Key == _config.TeamsDefaultTeam?.Trim()) initial = item;
                if (_scope.Channel != null)
                    foreach (TreeViewItem child in item.Items)
                        if (child.Tag is Scope { Channel: var id } && id == _scope.Channel) initial = child;
            }
            if (_scope.Following) initial = following;
            if (_scope.Collection != null)
            {
                initial = saved;
                foreach (TreeViewItem collection in saved.Items)
                    if (collection.Tag is Scope collectionScope && collectionScope.Collection == _scope.Collection) initial = collection;
            }
            // Filtering the channel directory must not change the active conversation scope.
            var matchesScope = initial.Tag is Scope candidate && candidate.Team == _scope.Team
                && candidate.Channel == _scope.Channel && candidate.Collection == _scope.Collection && candidate.Following == _scope.Following;
            if (matchesScope || !_defaultsApplied) initial.IsSelected = true;
            initial.Dispatcher.BeginInvoke(new Action(initial.BringIntoView), System.Windows.Threading.DispatcherPriority.Loaded);
            if (!_defaultsApplied && initial.Tag is Scope scope) _scope = scope;
            ShowScope();
            if (_channelsLoaded) _defaultsApplied = true;
        }
        finally { _buildingTree = false; }
    }

    private TreeViewItem ChannelItem(TeamsChannel channel, bool includeTeam)
    {
        var item = new TreeViewItem
        {
            Header = TeamsText.OneLine(includeTeam ? $"{channel.TeamName} › {channel.ChannelName}" : channel.ChannelName),
            Tag = new Scope(channel.TeamId, channel.ChannelId, TeamsText.OneLine($"{channel.TeamName} › {channel.ChannelName}")),
            ToolTip = $"{channel.Threads} indexed threads · latest activity {TeamsText.LocalTime(channel.LastActivityAt)}",
        };
        AttachFollowMenu(item, channel.ChannelId);
        return item;
    }

    private void AttachFollowMenu(TreeViewItem item, string channel)
    {
        var menu = new ContextMenu();
        var action = new MenuItem();
        menu.Items.Add(action);
        menu.Opened += (_, _) =>
        {
            action.Header = _following.Contains(channel) ? "Unfollow channel" : "Follow channel";
            action.IsEnabled = _savedStore != null;
            action.ToolTip = _savedStore == null ? _savedError ?? "Sign in to follow channels for this account." : null;
        };
        action.Click += async (_, _) => await ToggleFollowingAsync(channel);
        item.ContextMenu = menu;
    }

    private async Task ToggleFollowingAsync(string channel)
    {
        if (_savedStore == null) { StatusText.Text = _savedError ?? "Sign in to follow channels for this account."; return; }
        try
        {
            var channels = new HashSet<string>(_following, StringComparer.Ordinal);
            var removed = !channels.Add(channel);
            if (removed) channels.Remove(channel);
            _following = _savedStore.SetFollowedChannels(channels.OrderBy(id => id, StringComparer.Ordinal)).ToHashSet(StringComparer.Ordinal);
            BuildChannelTree();
            UpdateFollowingAction();
            if (_scope.Following) await SearchAsync();
            else StatusText.Text = removed ? "Channel unfollowed." : "Channel followed.";
        }
        catch (Exception ex) { StatusText.Text = "Couldn't update followed channels. " + TeamsText.TerminalSafe(ex.Message); }
    }

    private void UpdateFollowingAction()
    {
        FollowButton.Visibility = _scope.Channel != null && _scope.Collection == null ? Visibility.Visible : Visibility.Collapsed;
        FollowButton.IsEnabled = _savedStore != null;
        FollowButton.Content = _scope.Channel != null && _following.Contains(_scope.Channel) ? "Unfollow channel" : "Follow channel";
        FollowButton.ToolTip = _savedStore == null ? _savedError ?? "Sign in to follow channels for this account." : null;
    }

    private async void OnFollowChannel(object sender, RoutedEventArgs e)
    {
        if (_scope.Channel != null) await ToggleFollowingAsync(_scope.Channel);
    }

    private async void OnChannelChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_disposed || _buildingTree || e.NewValue is not TreeViewItem { Tag: Scope scope }) return;
        _scope = scope;
        PeriodBox.IsEnabled = PersonBox.IsEnabled = _scope.Collection == null;
        SearchScopeChanged?.Invoke(this, EventArgs.Empty);
        ShowScope();
        if (_scope.Collection != null) ShowSavedThreads();
        else await SearchAsync();
    }

    private void ShowScope()
    {
        ScopeText.Text = _scope.Title;
        UpdateFollowingAction();
        if (_scope.Collection != null || _scope.Following) return;
        var channels = _channels.Where(c => _scope.Channel != null ? c.ChannelId == _scope.Channel : _scope.Team == null || c.TeamId == _scope.Team).ToList();
        if (channels.Count == 0) return;
        var latest = channels.Select(c => TeamsText.Parse(c.LastActivityAt)).Where(d => d.HasValue).Max();
        ChannelSummary.Text = $"{channels.Count} indexed channels · {channels.Sum(c => c.Threads)} threads"
            + (latest.HasValue ? $" · latest activity {latest.Value.ToLocalTime():g}" : "")
            + ". Search to find a conversation in this scope.";
    }

    private async Task SearchAsync(bool append = false, bool indexRefreshed = false)
    {
        if (_disposed) return;
        if (_scope.Collection != null) { ShowSavedThreads(); return; }
        var browsing = string.IsNullOrWhiteSpace(QueryBox.Text);
        var paged = browsing || _scope.Following;
        if (_scope.Following && _following.Count == 0)
        {
            ShowBrowseGuidance(true);
            return;
        }
        if (!_service.IsConfigured) return;
        if (browsing && _scope.Team == null && _scope.Channel == null && !_scope.Following)
        {
            ShowBrowseGuidance(false);
            return;
        }
        if (append && (!paged || _browse.NextOffset == null)) return;
        var offset = append ? _browse.NextOffset!.Value : 0;
        if (!append) _browse = new TeamsBrowseState();
        _searchRequest?.Cancel();
        _threadRequest?.Cancel();
        var request = _searchRequest = new CancellationTokenSource();
        var days = PeriodBox.SelectedIndex switch { 1 => 7, 2 => 30, _ => 0 };
        var since = days == 0 ? null : DateTime.Today.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var query = new TeamsSearchQuery(QueryBox.Text.Trim(), _scope.Team, _scope.Channel,
            string.IsNullOrWhiteSpace(PersonBox.Text) ? null : PersonBox.Text.Trim(), since, Limit: 100);
        _hasSearched = true;
        _channelError = false;
        if (!append)
        {
            _selectedHit = null;
            ResultsList.ItemsSource = null;
            UpdateLayoutMode();
        }
        StatusText.Text = paged ? "Loading conversations…" : "Searching…";
        LoadMoreButton.IsEnabled = false;
        RetryButton.Visibility = Visibility.Collapsed;
        Welcome.Visibility = Visibility.Collapsed;
        ResultsList.Visibility = Visibility.Visible;
        try
        {
            if (paged)
            {
                var response = _scope.Following
                    ? (await _service.FeedAsync(new TeamsFeedQuery(_following.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                        string.IsNullOrWhiteSpace(QueryBox.Text) ? null : QueryBox.Text.Trim(), query.Author,
                        query.Since, query.Until, Limit: 50, Offset: offset), request.Token)).Value
                    : (await _service.BrowseAsync(new TeamsBrowseQuery(query.Team, query.Channel, query.Author,
                        query.Since, query.Until, Limit: 50, Offset: offset), request.Token)).Value;
                if (request.IsCancellationRequested) return;
                if (!_browse.Apply(response, append))
                {
                    await SearchAsync(indexRefreshed: true);
                    return;
                }
                var selected = _selectedHit;
                ResultsList.ItemsSource = _browse.Results.Select(hit => new ResultRow(hit)).ToList();
                if (append && selected != null)
                    ResultsList.SelectedItem = ResultsList.Items.OfType<ResultRow>().FirstOrDefault(row => TeamsSavedThreads.Same(row.Hit, selected));
                StatusText.Text = (indexRefreshed ? "Index refreshed; list restarted. " : "")
                    + (response.Total == 0 ? (browsing ? "No indexed conversations in this scope." : "No conversations match these search words.") : $"{_browse.Results.Count} of {response.Total} conversations · newest activity first");
                LoadMoreButton.Visibility = response.NextOffset is { } next && next > offset ? Visibility.Visible : Visibility.Collapsed;
                LoadMoreButton.IsEnabled = true;
            }
            else
            {
                var response = (await _service.SearchAsync(query, request.Token)).Value;
                if (request.IsCancellationRequested) return;
                ResultsList.ItemsSource = response.Results.Select(hit => new ResultRow(hit)).ToList();
                StatusText.Text = response.Total == 0 ? "No matching threads. Try different words or a wider scope."
                    : $"{response.Total} matching threads" + (response.Total > response.Results.Count ? $" · showing the first {response.Results.Count}" : "")
                      + (response.AsOf == null ? "" : $" · index as of {TeamsText.LocalTime(response.AsOf)}");
                LoadMoreButton.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (request.IsCancellationRequested) return;
            _channelError = false;
            if (IsAccessDenied(ex)) { InvalidateChannelAccess(); _channelError = true; }
            StatusText.Text = paged && ex is TeamsSearchException { Failure: TeamsSearchFailure.NotFound }
                ? (_scope.Following ? "Following isn't available on this service yet. Browse a channel or search Teams." : "Channel browsing isn't available on this service yet. Enter search words to find conversations.")
                : (paged ? "Couldn't load conversations. " : "Search failed. ") + TeamsText.TerminalSafe(ex.Message);
            RetryButton.Visibility = Visibility.Visible;
        }
    }

    private void ShowBrowseGuidance(bool following)
    {
        _searchRequest?.Cancel();
        _threadRequest?.Cancel();
        ResultsList.ItemsSource = null;
        ResultsList.Visibility = Visibility.Collapsed;
        Welcome.Visibility = Visibility.Visible;
        LoadMoreButton.Visibility = RetryButton.Visibility = Visibility.Collapsed;
        WelcomeTitle.Text = following ? "Follow channels" : "Browse or search Teams";
        WelcomeDescription.Text = following
            ? "Choose a channel and select Follow channel. Following brings those channels together, newest activity first."
            : "Choose a team or channel to browse, enter search words, or open Saved threads.";
        ChannelSummary.Text = following ? "No channels followed yet." : ChannelSummary.Text;
        StatusText.Text = following ? "Follow a channel to start your feed." : "Choose a scope or enter search words.";
    }

    private async void OnResultChanged(object sender, SelectionChangedEventArgs e)
    {
        _threadRequest?.Cancel();
        _selectedHit = (ResultsList.SelectedItem as ResultRow)?.Hit;
        CollectionBox.Text = _selectedHit == null ? "" : _savedItems.FirstOrDefault(item => TeamsSavedThreads.Same(item.Hit, _selectedHit))?.Collection ?? "";
        UpdateLayoutMode();
        _thread = null;
        ThreadCards.Children.Clear();
        UpdateThreadActions();
        if (_selectedHit is not { } hit) return;
        var request = _threadRequest = new CancellationTokenSource();
        _thread = null;
        ShowThreadStatus("Loading thread…");
        UpdateThreadActions();
        try
        {
            var thread = (await _service.GetThreadAsync(hit.ThreadId, hit.ChannelId, request.Token)).Value;
            if (request.IsCancellationRequested) return;
            _thread = thread;
            _unavailableThreads.Remove((hit.ChannelId, hit.ThreadId));
            TeamsThreadCards.Show(ThreadCards, thread, TeamsLinkPolicy.Hosts(_config.TeamsLinkHosts), CopyText, OpenLink);
            UpdateThreadActions();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!request.IsCancellationRequested)
            {
                if (IsAccessDenied(ex))
                {
                    InvalidateChannelAccess();
                    _channelError = true;
                    StatusText.Text = "Couldn't load thread. " + TeamsText.TerminalSafe(ex.Message);
                    RetryButton.Visibility = Visibility.Visible;
                }
                else if (ex is TeamsSearchException { Failure: TeamsSearchFailure.NotFound })
                {
                    _unavailableThreads.Add((hit.ChannelId, hit.ThreadId));
                    InvalidateChannelAccess();
                    var recoveryScope = _scope;
                    await LoadChannelsAsync();
                    if (!_channelError && _scope == recoveryScope) StatusText.Text = "This thread is no longer available. Its saved reference has been kept but hidden.";
                }
                else ShowThreadStatus("Couldn't load thread. " + TeamsText.TerminalSafe(ex.Message));
            }
        }
    }

    private static bool IsAccessDenied(Exception error) => error is EntraTokenException || error is TeamsSearchException
    {
        Failure: TeamsSearchFailure.Unauthorized or TeamsSearchFailure.Forbidden or TeamsSearchFailure.PurposeNotGranted or TeamsSearchFailure.AccessNotVerified,
    };

    private void ClearRemoteContent()
    {
        _threadRequest?.Cancel();
        _selectedHit = null;
        _thread = null;
        ResultsList.ItemsSource = null;
        ThreadCards.Children.Clear();
        LoadMoreButton.Visibility = Visibility.Collapsed;
        _browse = new TeamsBrowseState();
        UpdateLayoutMode();
        UpdateThreadActions();
    }

    private void InvalidateChannelAccess()
    {
        ClearRemoteContent();
        _channels = Array.Empty<TeamsChannel>();
        _authorizedChannels.Clear();
        _channelsLoaded = false;
        if (_scope.Collection == null && !_scope.Following) _scope = new Scope(null, null, "All channels");
        BuildChannelTree();
    }

    private void ShowThreadStatus(string message)
    {
        ThreadCards.Children.Clear();
        ThreadCards.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
    }

    private async Task LoadSavedThreadsAsync()
    {
        if (_disposed || _savedStore != null || !_service.IsConfigured) return;
        try
        {
            var account = _service.Workspace?.Account ?? EntraTokenSource.Shared?.WindowsUpn() ?? EntraTokenSource.ResolveWindowsUpn();
            if (_service.Workspace == null && string.IsNullOrWhiteSpace(account) && EntraTokenSource.Shared is { } tokenSource)
                account = await tokenSource.ResolveAccountUpnAsync();
            if (string.IsNullOrWhiteSpace(account)) return;
            var store = new TeamsSavedThreads(_config.TeamsApiUrl!, account);
            _savedItems = store.Load();
            _following = store.FollowedChannels().ToHashSet(StringComparer.Ordinal);
            _savedStore = store;
            _savedError = null;
            BuildChannelTree();
            if (_scope.Collection != null) ShowSavedThreads();
            else if (_scope.Following) await SearchAsync();
            UpdateFollowingAction();
            UpdateThreadActions();
        }
        catch (Exception ex)
        {
            _savedError = "Couldn't read saved threads. " + TeamsText.TerminalSafe(ex.Message);
            StatusText.Text = _savedError;
            UpdateFollowingAction();
            UpdateThreadActions();
        }
    }

    private void ShowSavedThreads()
    {
        _searchRequest?.Cancel();
        _threadRequest?.Cancel();
        if (!_channelsLoaded)
        {
            ClearRemoteContent();
            Welcome.Visibility = Visibility.Collapsed;
            ResultsList.Visibility = Visibility.Visible;
            StatusText.Text = "Load channels to check access before showing saved threads.";
            _channelError = true;
            RetryButton.Visibility = Visibility.Visible;
            return;
        }
        var query = QueryBox.Text.Trim();
        var items = VisibleSaved.Where(item => string.IsNullOrEmpty(_scope.Collection) || item.Collection == _scope.Collection)
            .Where(item => query.Length == 0 || $"{item.Hit.Subject} {item.Hit.Snippet} {item.Hit.TeamName} {item.Hit.ChannelName}".Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Select(item => new ResultRow(item.Hit)).ToList();
        ResultsList.ItemsSource = items;
        ResultsList.Visibility = Visibility.Visible;
        Welcome.Visibility = Visibility.Collapsed;
        StatusText.Text = items.Count == 0 ? "No saved threads here yet. Save a conversation to find it again." : $"{items.Count} saved threads · select one to reload its latest messages";
        RetryButton.Visibility = Visibility.Collapsed;
        LoadMoreButton.Visibility = Visibility.Collapsed;
    }

    private void UpdateThreadActions()
    {
        OpenButton.IsEnabled = CopyButton.IsEnabled = SafeLink() != null;
        CopyThreadButton.IsEnabled = CopyMarkdownButton.IsEnabled = _thread != null;
        SaveButton.IsEnabled = _selectedHit != null && _savedStore != null;
        SaveButton.ToolTip = _savedStore == null ? _savedError ?? "Sign in to save threads for this account." : "Keep a local reference to this conversation.";
        var saved = _selectedHit != null && _savedItems.Any(item => TeamsSavedThreads.Same(item.Hit, _selectedHit));
        SaveButton.Content = saved ? "Remove bookmark" : "Save thread";
    }

    private void CopyText(string text)
    {
        try { Clipboard.SetText(text); StatusText.Text = "Copied."; }
        catch (Exception ex) { StatusText.Text = "Couldn't copy. " + TeamsText.TerminalSafe(ex.Message); }
    }

    private void OnCopyThread(object sender, RoutedEventArgs e)
    {
        if (_thread != null) CopyText(TeamsThreadCopy.Thread(_thread, false, TeamsLinkPolicy.Hosts(_config.TeamsLinkHosts)));
    }

    private void OnCopyMarkdown(object sender, RoutedEventArgs e)
    {
        if (_thread != null) CopyText(TeamsThreadCopy.Thread(_thread, true, TeamsLinkPolicy.Hosts(_config.TeamsLinkHosts)));
    }

    private void OnSaveThread(object sender, RoutedEventArgs e)
    {
        if (_savedStore == null || _selectedHit == null) return;
        try
        {
            var saved = _savedItems.Any(item => TeamsSavedThreads.Same(item.Hit, _selectedHit));
            _savedItems = saved ? _savedStore.Remove(_selectedHit) : _savedStore.Save(_selectedHit, CollectionBox.Text);
            BuildChannelTree();
            if (_scope.Collection != null) ShowSavedThreads();
            UpdateThreadActions();
            StatusText.Text = saved ? "Bookmark removed." : "Thread saved.";
        }
        catch (Exception ex) { StatusText.Text = "Couldn't save bookmark. " + TeamsText.TerminalSafe(ex.Message); }
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized && _scope.Collection != null) ShowSavedThreads();
    }

    private void OnResultContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var container = e.OriginalSource is DependencyObject source
            ? ItemsControl.ContainerFromElement(ResultsList, source) as ListBoxItem : null;
        var row = container?.DataContext as ResultRow ?? ResultsList.SelectedItem as ResultRow;
        e.Handled = true;
        if (row == null) return;
        var menu = new ContextMenu { PlacementTarget = container ?? (UIElement)ResultsList };
        var save = new MenuItem { Header = "Save thread", DataContext = row, IsEnabled = _savedStore != null,
            ToolTip = _savedStore == null ? _savedError ?? "Sign in to save threads for this account." : null };
        save.Click += OnResultSave;
        menu.Items.Add(save);
        var link = new MenuItem { Header = "Copy link", DataContext = row,
            IsEnabled = TeamsLinkPolicy.SafeUri(row.Hit.WebUrl, TeamsLinkPolicy.Hosts(_config.TeamsLinkHosts)) != null };
        link.Click += OnResultCopyLink;
        menu.Items.Add(link);
        menu.IsOpen = true;
    }

    private void OnResultSave(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not ResultRow row) return;
        if (_savedStore == null) { StatusText.Text = _savedError ?? "Sign in to save threads for this account."; return; }
        try
        {
            var collection = _savedItems.FirstOrDefault(item => TeamsSavedThreads.Same(item.Hit, row.Hit))?.Collection;
            _savedItems = _savedStore.Save(row.Hit, collection);
            BuildChannelTree();
            UpdateThreadActions();
            StatusText.Text = "Thread saved.";
        }
        catch (Exception ex) { StatusText.Text = "Couldn't save bookmark. " + TeamsText.TerminalSafe(ex.Message); }
    }

    private void OnResultCopyLink(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is ResultRow row
            && TeamsLinkPolicy.SafeUri(row.Hit.WebUrl, TeamsLinkPolicy.Hosts(_config.TeamsLinkHosts)) is { } link) CopyText(link.AbsoluteUri);
    }

    private void OnChannelFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized && _config != null) BuildChannelTree();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _channelsRequest?.Cancel();
        _searchRequest?.Cancel();
        _threadRequest?.Cancel();
        _service.Dispose();
        ClearRemoteContent();
        _channels = Array.Empty<TeamsChannel>();
        _authorizedChannels.Clear();
        _savedItems = Array.Empty<TeamsSavedThread>();
        _following.Clear();
        _savedStore = null;
        ChannelTree.Items.Clear();
        UpdateFollowingAction();
        UpdateThreadActions();
    }

    private void UpdateLayoutMode()
    {
        if (!IsInitialized) return;
        var sidebar = _sidebarShown;
        Sidebar.Visibility = SidebarDivider.Visibility = sidebar ? Visibility.Visible : Visibility.Collapsed;
        if (!sidebar && SidebarColumn.Width.Value > 0)
        {
            _sidebarWidth = Math.Max(SidebarColumn.ActualWidth, 180);
            SidebarColumn.Width = new GridLength(0);
        }
        SidebarColumn.MinWidth = sidebar ? 180 : 0;
        if (sidebar && SidebarColumn.Width.Value <= 0) SidebarColumn.Width = new GridLength(_sidebarWidth);
        SidebarDividerColumn.Width = new GridLength(sidebar ? 5 : 0);
        var detail = _selectedHit != null;
        var beside = detail && ActualWidth >= 1200;
        ThreadPanel.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;
        DetailDivider.Visibility = beside ? Visibility.Visible : Visibility.Collapsed;
        ResultsColumn.MinWidth = detail && !beside ? 0 : 180;
        ResultsColumn.Width = new GridLength(detail && !beside ? 0 : 1, detail && !beside ? GridUnitType.Pixel : GridUnitType.Star);
        DetailDividerColumn.Width = new GridLength(beside ? 5 : 0);
        DetailColumn.Width = new GridLength(detail ? 1 : 0, detail ? GridUnitType.Star : GridUnitType.Pixel);
    }

    private Uri? SafeLink() => TeamsLinkPolicy.SafeUri(_selectedHit?.WebUrl, TeamsLinkPolicy.Hosts(_config.TeamsLinkHosts));
    private void OpenLink(Uri uri)
    {
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = "Couldn't open link. " + TeamsText.TerminalSafe(ex.Message); }
    }
    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (SafeLink() is { } uri) OpenLink(uri);
    }
    private void OnCopy(object sender, RoutedEventArgs e) { if (SafeLink() is { } uri) CopyText(uri.AbsoluteUri); }
    private void OnCloseThread(object sender, RoutedEventArgs e) { ResultsList.SelectedItem = null; }
    private void OnToggleChannels(object sender, RoutedEventArgs e) { _sidebarShown = !_sidebarShown; UpdateLayoutMode(); }
    private async void OnRefreshChannels(object sender, RoutedEventArgs e) => await LoadChannelsAsync();
    private async void OnRetry(object sender, RoutedEventArgs e)
    {
        _service.InvalidateWorkspaceTokens();
        if (_channelError) await LoadChannelsAsync(); else await SearchAsync();
    }
    private async void OnLoadMore(object sender, RoutedEventArgs e) => await SearchAsync(append: true);
    private async void OnSearch(object sender, RoutedEventArgs e) => await SearchAsync();
    private async void OnPersonKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(); } }
    private async void OnFilterChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded && _hasSearched) await SearchAsync(); }
}
