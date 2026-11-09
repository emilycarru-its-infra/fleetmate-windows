using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FleetMate.Core.Config;
using FleetMate.Core.Services.Teams;
using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Teams;

/// <summary>Search remains the primary action; the indexed channel directory makes the initial scope visible.</summary>
public partial class TeamsPage : Page, ITabSearch
{
    private readonly FleetMateConfig _config;
    private readonly TeamsSearchService _service;
    private IReadOnlyList<TeamsChannel> _channels = Array.Empty<TeamsChannel>();
    private CancellationTokenSource? _channelsRequest;
    private CancellationTokenSource? _searchRequest;
    private CancellationTokenSource? _threadRequest;
    private Scope _scope = new(null, null, "All channels");
    private TeamsSearchHit? _selectedHit;
    private bool _channelsLoaded;
    private bool _defaultsApplied;
    private bool _buildingTree;
    private bool _sidebarShown = true;
    private double _sidebarWidth = 240;
    private bool _hasSearched;
    private bool _channelError;

    private sealed record Scope(string? Team, string? Channel, string Title);
    private sealed record ResultRow(TeamsSearchHit Hit)
    {
        public string Title => TeamsText.OneLine(string.IsNullOrWhiteSpace(Hit.Subject) ? Hit.Snippet : Hit.Subject);
        public string Location => TeamsText.OneLine($"{Hit.TeamName} › {Hit.ChannelName}");
        public string Snippet => TeamsText.TerminalSafe(Hit.Snippet);
        public string Activity => $"{Hit.ReplyCount} replies · {TeamsText.LocalTime(Hit.LastActivityAt)}";
    }

    public TeamsPage()
    {
        InitializeComponent();
        _config = (Application.Current as App)?.Config ?? new FleetMateConfig();
        _service = TeamsSearchService.FromConfig(_config);
        Loaded += async (_, _) =>
        {
            if (!_channelsLoaded) await LoadChannelsAsync();
            UpdateLayoutMode();
        };
        SizeChanged += (_, _) => UpdateLayoutMode();
    }

    public TabSearchScope? SearchScope => new(QueryBox, "Search Teams channels", () => _ = SearchAsync());
    public event EventHandler? SearchScopeChanged { add { } remove { } }

    private async Task LoadChannelsAsync()
    {
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
            _channelsLoaded = true;
            _channelError = false;
            BuildChannelTree();
            if (_channels.Count == 0) ChannelSummary.Text = "No channels are available in the search index yet. Refresh to check again.";
            if (!_hasSearched) StatusText.Text = "Search message text, then read the matching threads here.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (request.IsCancellationRequested) return;
            _channelError = true;
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
            var all = new TreeViewItem { Header = "All channels", Tag = new Scope(null, null, "All channels") };
            ChannelTree.Items.Add(all);
            TreeViewItem initial = all;
            var pinned = TeamsChannelScope.Pinned(_channels, _config.TeamsDefaultChannels);
            if (pinned.Count > 0)
            {
                var group = new TreeViewItem { Header = "Pinned channels", IsExpanded = true };
                foreach (var channel in pinned) group.Items.Add(ChannelItem(channel, true));
                ChannelTree.Items.Add(group);
            }
            foreach (var team in _channels.GroupBy(c => c.TeamId)
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
            initial.IsSelected = true;
            initial.Dispatcher.BeginInvoke(new Action(initial.BringIntoView), System.Windows.Threading.DispatcherPriority.Loaded);
            if (initial.Tag is Scope scope) _scope = scope;
            ShowScope();
            _defaultsApplied = true;
        }
        finally { _buildingTree = false; }
    }

    private static TreeViewItem ChannelItem(TeamsChannel channel, bool includeTeam) => new()
    {
        Header = TeamsText.OneLine(includeTeam ? $"{channel.TeamName} › {channel.ChannelName}" : channel.ChannelName),
        Tag = new Scope(channel.TeamId, channel.ChannelId, TeamsText.OneLine($"{channel.TeamName} › {channel.ChannelName}")),
        ToolTip = $"{channel.Threads} indexed threads · latest activity {TeamsText.LocalTime(channel.LastActivityAt)}",
    };

    private async void OnChannelChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_buildingTree || e.NewValue is not TreeViewItem { Tag: Scope scope }) return;
        _scope = scope;
        ShowScope();
        if (_hasSearched && !string.IsNullOrWhiteSpace(QueryBox.Text)) await SearchAsync();
    }

    private void ShowScope()
    {
        ScopeText.Text = _scope.Title;
        var channels = _channels.Where(c => _scope.Channel != null ? c.ChannelId == _scope.Channel : _scope.Team == null || c.TeamId == _scope.Team).ToList();
        if (channels.Count == 0) return;
        var latest = channels.Select(c => TeamsText.Parse(c.LastActivityAt)).Where(d => d.HasValue).Max();
        ChannelSummary.Text = $"{channels.Count} indexed channels · {channels.Sum(c => c.Threads)} threads"
            + (latest.HasValue ? $" · latest activity {latest.Value.ToLocalTime():g}" : "")
            + ". Search to find a conversation in this scope.";
    }

    private async Task SearchAsync()
    {
        if (!_service.IsConfigured || string.IsNullOrWhiteSpace(QueryBox.Text)) return;
        _searchRequest?.Cancel();
        _threadRequest?.Cancel();
        var request = _searchRequest = new CancellationTokenSource();
        var days = PeriodBox.SelectedIndex switch { 1 => 7, 2 => 30, _ => 0 };
        var since = days == 0 ? null : DateTime.Today.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var query = new TeamsSearchQuery(QueryBox.Text.Trim(), _scope.Team, _scope.Channel,
            string.IsNullOrWhiteSpace(PersonBox.Text) ? null : PersonBox.Text.Trim(), since, Limit: 100);
        _hasSearched = true;
        _channelError = false;
        _selectedHit = null;
        ResultsList.ItemsSource = null;
        UpdateLayoutMode();
        StatusText.Text = "Searching…";
        RetryButton.Visibility = Visibility.Collapsed;
        Welcome.Visibility = Visibility.Collapsed;
        ResultsList.Visibility = Visibility.Visible;
        try
        {
            var response = (await _service.SearchAsync(query, request.Token)).Value;
            if (request.IsCancellationRequested) return;
            ResultsList.ItemsSource = response.Results.Select(hit => new ResultRow(hit)).ToList();
            StatusText.Text = response.Total == 0 ? "No matching threads. Try different words or a wider scope."
                : $"{response.Total} matching threads" + (response.Total > response.Results.Count ? $" · showing the first {response.Results.Count}" : "")
                  + (response.AsOf == null ? "" : $" · index as of {TeamsText.LocalTime(response.AsOf)}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (request.IsCancellationRequested) return;
            _channelError = false;
            StatusText.Text = "Search failed. " + TeamsText.TerminalSafe(ex.Message);
            RetryButton.Visibility = Visibility.Visible;
        }
    }

    private async void OnResultChanged(object sender, SelectionChangedEventArgs e)
    {
        _threadRequest?.Cancel();
        _selectedHit = (ResultsList.SelectedItem as ResultRow)?.Hit;
        UpdateLayoutMode();
        if (_selectedHit is not { } hit) return;
        var request = _threadRequest = new CancellationTokenSource();
        ThreadText.Text = "Loading thread…";
        OpenButton.IsEnabled = CopyButton.IsEnabled = SafeLink() != null;
        try
        {
            var thread = (await _service.GetThreadAsync(hit.ThreadId, hit.ChannelId, request.Token)).Value;
            if (request.IsCancellationRequested) return;
            var text = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(thread.Subject)) text.AppendLine(TeamsText.TerminalSafe(thread.Subject)).AppendLine();
            foreach (var message in thread.Messages)
            {
                text.AppendLine($"{TeamsText.OneLine(message.AuthorDisplayName ?? "Unknown author")} · {TeamsText.LocalTime(message.CreatedAt)}");
                text.AppendLine(TeamsText.TerminalSafe(message.Text));
                foreach (var attachment in message.Attachments) text.AppendLine("Attachment: " + TeamsText.OneLine(attachment.Name));
                text.AppendLine();
            }
            ThreadText.Text = text.ToString();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!request.IsCancellationRequested) ThreadText.Text = "Couldn't load thread. " + TeamsText.TerminalSafe(ex.Message);
        }
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
    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (SafeLink() is { } uri) Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
    private void OnCopy(object sender, RoutedEventArgs e) { if (SafeLink() is { } uri) Clipboard.SetText(uri.AbsoluteUri); }
    private void OnCloseThread(object sender, RoutedEventArgs e) { ResultsList.SelectedItem = null; }
    private void OnToggleChannels(object sender, RoutedEventArgs e) { _sidebarShown = !_sidebarShown; UpdateLayoutMode(); }
    private async void OnRefreshChannels(object sender, RoutedEventArgs e) => await LoadChannelsAsync();
    private async void OnRetry(object sender, RoutedEventArgs e) { if (_channelError) await LoadChannelsAsync(); else await SearchAsync(); }
    private async void OnSearch(object sender, RoutedEventArgs e) => await SearchAsync();
    private async void OnPersonKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(); } }
    private async void OnFilterChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded && _hasSearched) await SearchAsync(); }
}
