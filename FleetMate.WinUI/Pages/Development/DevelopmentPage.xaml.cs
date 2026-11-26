using FleetMate.Core.Development;
using FleetMate.Core.Links;
using FleetMate.WinUI.Pages.Development.Graphs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Serilog;

namespace FleetMate.WinUI.Pages.Development;

/// <summary>
/// The Development tab: the sub-tabs (Repos, Pulls, Commits, Pipelines,
/// Skills), the Inbox popover, Refresh, and the Graphs strip above the open
/// sub-tab. Each sub-tab is its own control (<see cref="IDevelopmentSegmentView"/>);
/// this page switches between them, routes the toolbar search, Refresh and
/// <c>fleetmate:</c> links to them, and owns nothing else.
/// </summary>
public sealed partial class DevelopmentPage : Page
{
    private readonly Dictionary<DevelopmentSegment, IDevelopmentSegmentView> _views;
    private IDevelopmentSegmentView _current;
    private string _search = "";
    private bool _hooked;

    public DevelopmentPage()
    {
        InitializeComponent();
        _views = new IDevelopmentSegmentView[] { ReposView, PullsView, CommitsView, PipelinesView, SkillsView }
            .ToDictionary(v => v.Segment);
        _current = ReposView;
        foreach (var view in _views.Values)
        {
            view.Attach(this);
            view.BusyChanged += (_, _) => UpdateBusy();
        }
        Inbox.Attach(this);
        BuildSegmentMenu();
        MainWindow.SearchChanged += (_, text) =>
        {
            _search = text;
            _current.ApplySearch(text);
        };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>The open sub-tab.</summary>
    public DevelopmentSegment CurrentSegment => _current.Segment;

    /// <summary>Raised when another sub-tab opens, after it is shown.</summary>
    public event EventHandler? SegmentChanged;

    public Pulls.PullsSegmentView Pulls => PullsView;
    public Commits.CommitsSegmentView Commits => CommitsView;
    public Pipelines.PipelinesSegmentView Pipelines => PipelinesView;
    public Repos.ReposSegmentView Repos => ReposView;
    public Skills.SkillsSegmentView Skills => SkillsView;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        DevelopmentContext.Segment(CurrentSegment.ToString());
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_hooked)
        {
            _hooked = true;
            var services = App.Services;
            services.Inbox.Changed += OnInboxChanged;
            services.DevelopmentLinkRequested += OnDevelopmentLinkRequested;
            services.CacheChanged += OnCacheChanged;
            services.DevelopmentSync.StatusChanged += OnSyncStatusChanged;
        }
        _current.SetShown(true);
        GraphsButton.IsChecked = DevelopmentGraphs.IsShown;
        RenderGraphs();
        UpdateInboxButton();
        UpdateBusy();
        _ = TakePendingLinkAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Another tab is showing: stop the visible-segment refresh cadence.
        _current.SetShown(false);
        App.Services.DevelopmentSync.DetachView(_current);
    }

    // MARK: - Segments

    /// <summary>Open <paramref name="segment"/>; links, widgets and the inbox call this before selecting something there.</summary>
    public void ShowSegment(DevelopmentSegment segment)
    {
        var item = segment switch
        {
            DevelopmentSegment.Repos => ReposItem,
            DevelopmentSegment.Pulls => PullsItem,
            DevelopmentSegment.Commits => CommitsItem,
            DevelopmentSegment.Pipelines => PipelinesItem,
            _ => SkillsItem,
        };
        if (SegmentBar.SelectedItem != item) SegmentBar.SelectedItem = item;
        else Switch(segment);
    }

    private void SegmentBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string tag && Enum.TryParse<DevelopmentSegment>(tag, out var segment)) Switch(segment);
    }

    private void Switch(DevelopmentSegment segment)
    {
        if (_views is null) return;
        var next = _views[segment];
        if (!ReferenceEquals(next, _current))
        {
            _current.SetShown(false);
            ((UIElement)_current).Visibility = Visibility.Collapsed;
            _current = next;
        }
        ((UIElement)_current).Visibility = Visibility.Visible;
        if (IsLoaded) _current.SetShown(true);
        _current.ApplySearch(_search);
        SegmentMenu.Content = segment.ToString();
        DevelopmentContext.Segment(segment.ToString());
        RenderGraphs();
        UpdateBusy();
        SegmentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BuildSegmentMenu()
    {
        SegmentMenu.Content = CurrentSegment.ToString();
        foreach (var item in SegmentBar.Items)
        {
            var entry = new MenuFlyoutItem { Text = item.Text, Tag = item.Tag };
            entry.Click += (_, _) => SegmentBar.SelectedItem = item;
            SegmentMenuFlyout.Items.Add(entry);
        }
    }

    // MARK: - Refresh

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void RefreshAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            await _current.RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[development] Refresh of {Segment} failed", CurrentSegment);
        }
    }

    private void OnSyncStatusChanged(object? sender, EventArgs e) => UpdateBusy();

    private void UpdateBusy() => LoadingRing.IsActive = _current?.IsBusy == true;

    // MARK: - Graphs

    private void GraphsButton_Click(object sender, RoutedEventArgs e)
    {
        DevelopmentGraphs.IsShown = GraphsButton.IsChecked == true;
        RenderGraphs();
    }

    private void OnCacheChanged(string key)
    {
        UpdateBusy();
        if (DevelopmentGraphs.Follows(CurrentSegment, key)) RenderGraphs();
    }

    /// <summary>Rebuild the strip for the open sub-tab. Hidden graphs cost nothing.</summary>
    public void RenderGraphs()
    {
        var shown = DevelopmentGraphs.IsShown;
        var content = shown ? DevelopmentGraphs.Build(this, CurrentSegment) : null;
        GraphsHost.Content = content;
        GraphsScroller.Visibility = DevelopmentUi.Show(content != null);
        // The graphs never take more than 40% of the page, so the lists keep the screen.
        GraphsScroller.MaxHeight = Math.Max(180, ActualHeight * 0.4);
    }

    // MARK: - Inbox

    private void OnInboxChanged(object? sender, EventArgs e)
    {
        UpdateInboxButton();
        Inbox.Render();
    }

    private bool _inboxOpen;

    private void UpdateInboxButton() =>
        InboxButton.Visibility = DevelopmentUi.Show(DevelopmentFilter.ShowInboxButton(App.Services.Inbox.UnreadCount, _inboxOpen));

    private void InboxFlyout_Opening(object sender, object e)
    {
        _inboxOpen = true;
        Inbox.Render();
    }

    private void InboxFlyout_Closed(object sender, object e)
    {
        _inboxOpen = false;
        UpdateInboxButton();
    }

    /// <summary>Open the Inbox popover — the Unread in Inbox figure's click.</summary>
    public void ShowInbox()
    {
        InboxButton.Visibility = Visibility.Visible;
        InboxFlyout.ShowAt(InboxButton);
    }

    /// <summary>Close the popover once a notification opened in the detail pane.</summary>
    public void CloseInbox() => InboxFlyout.Hide();

    // MARK: - Links

    private async void OnDevelopmentLinkRequested()
    {
        if (IsLoaded) await TakePendingLinkAsync();
    }

    private async Task TakePendingLinkAsync()
    {
        if (App.Services.PendingDevelopmentLink is not { } link) return;
        App.Services.PendingDevelopmentLink = null;
        try
        {
            await OpenLinkAsync(link);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Link}", link.ToLink());
        }
    }

    /// <summary>A pull request, commit or pipeline link: its sub-tab opens, filters clear, and the item is selected.</summary>
    public async Task OpenLinkAsync(FleetMateLink link)
    {
        switch (link)
        {
            case FleetMateLink.PullRequest:
                ShowSegment(DevelopmentSegment.Pulls);
                await PullsView.OpenLinkAsync(link);
                break;
            case FleetMateLink.Commit:
                ShowSegment(DevelopmentSegment.Commits);
                await CommitsView.OpenLinkAsync(link);
                break;
            case FleetMateLink.AzureDevOpsRun or FleetMateLink.AzureDevOpsPipeline or FleetMateLink.GitHubRun:
                ShowSegment(DevelopmentSegment.Pipelines);
                await PipelinesView.OpenLinkAsync(link);
                break;
        }
    }

    /// <summary>A link that could not be found: say so in the window's link banner.</summary>
    public void ShowLinkNotFound(FleetMateLink link)
    {
        if (App.Current.Window is { } window)
            window.ShowLinkBanner($"FleetMate couldn't find {link.ToLink()}. It may have been deleted, or you may not have access.");
    }
}
