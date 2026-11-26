using FleetMate.Core.Development;
using FleetMate.Core.Links;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Terminal;
using FleetMate.WinUI.Pages.Development.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace FleetMate.WinUI.Pages.Development.Commits;

/// <summary>
/// Development › Commits: one card per repository with its recent commits
/// (three, then "Show all N"), filtered by source, repository and the toolbar
/// search, and the selected commit's detail beside it.
/// </summary>
public sealed partial class CommitsSegmentView : UserControl, IDevelopmentSegmentView
{
    private readonly ListDetailLayout _layout;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private DevelopmentPage? _page;
    private DevelopmentSourceFilter _source = DevelopmentSourceFilter.All;
    private string? _repository;
    private string _search = "";
    private string _summary = "Commits";
    private bool _shown;
    private bool _rendering;
    private bool _busy;

    public CommitsSegmentView()
    {
        InitializeComponent();
        _layout = new ListDetailLayout(Root, ListColumn, DetailColumn, ListPane, DetailPane, BackButton);
    }

    public DevelopmentSegment Segment => DevelopmentSegment.Commits;

    public bool IsBusy => _busy;
    public event EventHandler? BusyChanged;

    public void Attach(DevelopmentPage page)
    {
        _page = page;
        var services = App.Services;
        services.CacheChanged += key => { if (key == "Commits" && _shown) Render(); else if (key == "Commits") UpdateBusy(); };
        services.DevelopmentSync.StatusChanged += (_, _) => { if (_shown) UpdateCount(); };
        services.DevOpsSignedIn += OnDevOpsSignedIn;
    }

    public void SetShown(bool shown)
    {
        _shown = shown;
        if (!shown) return;
        App.Services.DevelopmentSync.AttachView(this, Segment);
        Render();
    }

    public void ApplySearch(string text)
    {
        if (_search == text) return;
        _search = text;
        if (_shown) Render();
    }

    public Task RefreshAsync() => App.Services.DevelopmentSync.RefreshNowAsync(DevelopmentRefreshArea.Commits);

    // MARK: - Render

    private void Render()
    {
        var services = App.Services;
        UpdateBusy();
        if (services.DevelopmentCommits is not { } all)
        {
            CommitsSource.Source = null;
            CommitsList.ItemsSource = null;
            EmptyText.Text = services.DevelopmentSync.IsLoading(DevelopmentRefreshArea.Commits) ? "Loading commits…" : "Commits load when this view opens.";
            EmptyText.Visibility = Visibility.Visible;
            UpdateCount();
            return;
        }

        _repository = RepoFilter.SetEntries(RepositoryFilterHierarchy.Count(
            all.Where(repo => CommitsAndPipelinesFilter.MatchesSource(repo.Source, _source)),
            repo => new RepositoryFilterScope(repo.Source, repo.Container, repo.Repository),
            repo => repo.Commits.Count), _repository);

        var repos = CommitsAndPipelinesFilter.Commits(all, _source, _search, _repository);
        var selectedId = (CommitsList.SelectedItem as CommitRowItem)?.Commit.Id ?? Detail.Commit?.Id;
        var groups = repos.Select(repo => new RepositoryCommitsGroup(repo, _expanded.Contains(repo.Id))).ToList();

        _rendering = true;
        try
        {
            CommitsSource.Source = groups;
            CommitsList.ItemsSource = CommitsSource.View;
            if (selectedId != null)
                CommitsList.SelectedItem = groups.SelectMany(g => g.OfType<CommitRowItem>()).FirstOrDefault(row => row.Commit.Id == selectedId);
        }
        finally { _rendering = false; }

        _summary = DevelopmentText.CommitsSummary(repos);
        EmptyText.Visibility = DevelopmentUi.Show(repos.Count == 0);
        EmptyText.Text = all.Count == 0 ? "No commits in the last 14 days." : "Nothing matches these filters.";
        UpdateCount();
    }

    private void UpdateCount() =>
        CountText.Text = _summary + App.Services.DevelopmentSync.StatusSuffix(DevelopmentRefreshArea.Commits);

    private void UpdateBusy()
    {
        var services = App.Services;
        var busy = services.DevelopmentCommits == null && services.DevelopmentSync.IsLoading(DevelopmentRefreshArea.Commits);
        if (busy == _busy) return;
        _busy = busy;
        BusyChanged?.Invoke(this, EventArgs.Empty);
    }

    // MARK: - Filters

    private void Source_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<DevelopmentSourceFilter>(tag, out var source)) _source = source;
        _repository = null;
        SyncSourceChips();
        Render();
    }

    private void SyncSourceChips()
    {
        SourceAll.IsChecked = _source == DevelopmentSourceFilter.All;
        SourceDevOps.IsChecked = _source == DevelopmentSourceFilter.DevOps;
        SourceGitHub.IsChecked = _source == DevelopmentSourceFilter.GitHub;
    }

    private void RepoFilter_SelectionChanged(object? sender, EventArgs e)
    {
        if (RepoFilter.Selection == _repository) return;
        _repository = RepoFilter.Selection;
        Render();
    }

    /// <summary>Show Commits filtered to one repository — the Commits graphs' repository bar.</summary>
    public void ShowRepository(string repository)
    {
        _source = DevelopmentSourceFilter.All;
        SyncSourceChips();
        _repository = repository;
        _page?.ShowSegment(Segment);
        Render();
    }

    // MARK: - Rows

    private void CommitsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ShowAllItem showAll) return;
        var id = showAll.Group.Repository.Id;
        if (!_expanded.Remove(id)) _expanded.Add(id);
        showAll.Group.ToggleShowAll();
    }

    private async void CommitsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering) return;
        if (CommitsList.SelectedItem is ShowAllItem)
        {
            // Show all is an action, not a selection: keep the commit that was picked.
            CommitsList.SelectedItem = e.RemovedItems.OfType<CommitRowItem>().FirstOrDefault();
            return;
        }
        if (CommitsList.SelectedItem is CommitRowItem row) await ShowCommitAsync(row.Repository, row.Commit);
    }

    private async Task ShowCommitAsync(RepositoryCommits repository, PullRequestCommit commit)
    {
        Placeholder.Visibility = Visibility.Collapsed;
        Detail.Visibility = Visibility.Visible;
        _layout.ShowDetail();
        DevelopmentContext.Select("commit", new[]
        {
            new ContextSelection("commit", commit.Id, new Dictionary<string, string?>
            {
                ["subject"] = commit.Subject,
                ["repository"] = repository.DisplayName,
                ["author"] = commit.AuthorName,
                ["url"] = commit.Url,
                ["source"] = repository.Source.ToString(),
            }),
        });
        await Detail.ShowAsync(repository, commit);
    }

    /// <summary>Right-click, Shift+F10 or the context key on a commit row: Open in Browser, Copy SHA.</summary>
    private void CommitsList_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (RowFrom(args.OriginalSource, out var container) is not { } row) return;
        args.Handled = true;
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "Open in Browser", Icon = new FontIcon { Glyph = "" } };
        open.Click += (_, _) => DevelopmentUi.OpenUrl(row.Commit.Url);
        var copy = new MenuFlyoutItem { Text = "Copy SHA", Icon = new FontIcon { Glyph = "" } };
        copy.Click += (_, _) => DevelopmentUi.Copy(row.Commit.Id);
        menu.Items.Add(open);
        menu.Items.Add(copy);
        if (args.TryGetPosition(container, out var point)) menu.ShowAt(container, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(container);
    }

    private CommitRowItem? RowFrom(object source, out ListViewItem container)
    {
        for (var element = source as DependencyObject; element != null; element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
            if (element is ListViewItem item && CommitsList.ItemFromContainer(item) is CommitRowItem row)
            {
                container = item;
                return row;
            }
        container = null!;
        return null;
    }

    // MARK: - Links and sign-in

    /// <summary>Signed in to Azure DevOps: a load that ran before left Azure DevOps out, so load again.</summary>
    private async void OnDevOpsSignedIn()
    {
        if (_shown && App.Services.DevelopmentCommits != null) await RefreshAsync();
    }

    /// <summary>A <c>fleetmate:</c> commit link: clear the filters and open the commit, by sha when it isn't loaded.</summary>
    public async Task OpenLinkAsync(FleetMateLink link)
    {
        if (link is not FleetMateLink.Commit commitLink) return;
        _source = DevelopmentSourceFilter.All;
        _repository = null;
        SyncSourceChips();
        var services = App.Services;
        if (services.DevelopmentCommits is null) await RefreshAsync();
        Render();

        var (repository, commit) = DevelopmentLinkTargets.Commit(services.DevelopmentCommits, commitLink);
        var row = (CommitsSource.Source as IEnumerable<RepositoryCommitsGroup>)?
            .SelectMany(g => g.OfType<CommitRowItem>())
            .FirstOrDefault(r => ReferenceEquals(r.Commit, commit));
        if (row != null)
        {
            CommitsList.SelectedItem = row;
            CommitsList.ScrollIntoView(row);
            return;
        }
        // The commit pane holds an Azure DevOps commit for sign-in itself.
        _rendering = true;
        CommitsList.SelectedItem = null;
        _rendering = false;
        await ShowCommitAsync(repository, commit);
    }
}
