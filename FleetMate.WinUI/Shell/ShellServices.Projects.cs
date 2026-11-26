using FleetMate.Core.Config;
using FleetMate.Core.Links;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Projects.Tasks;
using Serilog;

namespace FleetMate.WinUI.Shell;

/// <summary>
/// The Projects tab's data, following FleetMate.GUI's BoardsPage and
/// App.Widgets: the task providers (Azure DevOps, GitHub, Gitea) and the
/// tasks they list, every Shared Query run, the signed-in user's open work
/// items with their initiatives and the current sprint, their GitHub issues,
/// and the GitHub Project board. It reuses the Azure DevOps service and
/// sign-in Development set up. Each load raises <see cref="CacheChanged"/>
/// with its key, on the UI thread.
///
/// <code>
/// App.Services.CacheChanged += key => { if (key == ShellServices.ProjectTasksKey) Render(); };
/// await App.Services.LoadProjectTasksAsync();
/// </code>
/// </summary>
public sealed partial class ShellServices
{
    public const string ProjectTasksKey = "ProjectTasks";
    public const string QueriesKey = "Queries";
    public const string MyWorkItemsKey = "MyWorkItems";
    public const string IssuesKey = "Issues";
    public const string GitHubProjectKey = "GitHubProject";

    private static readonly TimeSpan QueriesFreshness = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MyWorkItemsRetry = TimeSpan.FromMinutes(2);

    private TaskProviderRegistry? _taskRegistry;
    private Task? _registryReady;
    private DateTime _queriesLoadedAt = DateTime.MinValue;
    private DateTime? _myWorkItemsAt;

    /// <summary>Everything the providers listed, up to 500, newest first; empty until the first load.</summary>
    public List<UnifiedTask> ProjectTasks { get; private set; } = new();

    /// <summary>Whether a provider list has loaded at least once.</summary>
    public bool ProjectTasksLoaded { get; private set; }

    /// <summary>The bucket filter's choices: every provider's buckets, after "(All Buckets)".</summary>
    public List<string> ProjectBuckets { get; private set; } = new() { TaskBoard.AllBuckets };

    /// <summary>Every Shared Query with its rows, in Azure DevOps order.</summary>
    public List<QueryRunDisplay> QueryRuns { get; private set; } = new();

    /// <summary>Null until the queries have been asked for; the message the view shows instead of rows.</summary>
    public string? QueriesMessage { get; private set; }

    /// <summary>The signed-in user's open work items across the organization; null until loaded.</summary>
    public List<WorkItem>? MyWorkItems { get; private set; }

    public List<WorkItem> InitiativeAncestors { get; private set; } = new();

    public List<Sprint> Sprints { get; private set; } = new();

    /// <summary>The user's open GitHub issues; null until fetched.</summary>
    public List<GitHubIssueSummary>? GitHubIssues { get; private set; }

    /// <summary>The @Me ids Mine shows, and the GitHub login for GitHub items; null until loaded.</summary>
    public HashSet<int>? MyWorkItemIds { get; private set; }

    /// <summary>@Me work items the board's list does not hold (it keeps the most recent).</summary>
    public List<UnifiedTask> MineExtra { get; } = new();

    public string? GitHubViewerLogin { get; private set; }

    /// <summary>The GitHub Project the Board view's GitHub source shows.</summary>
    public string? GitHubProjectId { get; private set; }

    public GitHubProjectField? GitHubProjectStatus { get; private set; }

    public List<GitHubProjectItem> GitHubProjectItems { get; private set; } = new();

    /// <summary>Why the GitHub Project board is empty, when it is.</summary>
    public string? GitHubProjectMessage { get; private set; }

    public ITaskProvider? TaskProvider(string provider) => _taskRegistry?.GetProvider(provider);

    /// <summary>The Projects strip's figures as the strip draws them.</summary>
    public ProjectsFigures ProjectsFigures => new(
        MyWorkItems ?? new List<WorkItem>(),
        IsCacheLoading(MyWorkItemsKey),
        InitiativeAncestors,
        Sprints,
        GitHubIssues,
        IsCacheLoading(IssuesKey),
        ProjectsTileFilter.Shared.Selected,
        DemoMode ? ProjectsDemoData.Organization : Config.AzureDevOps?.BaseUrl);

    /// <summary>Whether this setup can make a work item, an issue, a project.</summary>
    public bool CanCreateWorkItem => DemoMode || !string.IsNullOrEmpty(Config.AzureDevOps?.Organization);

    public bool CanCreateGitHubItem
    {
        get
        {
            if (DemoMode) return true;
            var gh = Config.GitHubProviderOrDefault();
            return !string.IsNullOrEmpty(gh.Owner ?? gh.Organization);
        }
    }

    /// <summary>Queries need Azure DevOps; without it the Queries view lists work items instead, as in FleetMate.GUI.</summary>
    public bool UsesStoredQueries => DemoMode || !string.IsNullOrEmpty(Config.AzureDevOps?.Organization);

    // MARK: - Demo mode

    /// <summary>Demo mode's Projects fixture (registered in App): <see cref="ProjectsDemoData"/> in every cache.</summary>
    public void SeedProjectsDemo()
    {
        var now = DateTime.UtcNow;
        ProjectTasks = ProjectsDemoData.Tasks(now);
        ProjectTasksLoaded = true;
        ProjectBuckets = new List<string> { TaskBoard.AllBuckets }
            .Concat(ProjectTasks.Select(t => t.Bucket).OfType<string>().Where(b => b.Length > 0).Distinct().Order())
            .ToList();
        QueryRuns = ProjectsDemoData.QueryRuns(now);
        MyWorkItems = ProjectsDemoData.MyWorkItems(now);
        MyWorkItemIds = MyWorkItems.Select(w => w.Id).ToHashSet();
        GitHubViewerLogin = ProjectsDemoData.Viewer;
        Sprints = ProjectsDemoData.Sprints(now);
        GitHubIssues = ProjectsDemoData.Issues(now);
        GitHubProjectId = "demo-project";
        GitHubProjectStatus = ProjectsDemoData.StatusField();
        GitHubProjectItems = ProjectsDemoData.ProjectItems(now);
    }

    // MARK: - Tasks

    private Task EnsureTaskRegistryAsync() => _registryReady ??= BuildTaskRegistryAsync();

    private async Task BuildTaskRegistryAsync()
    {
        var registry = new TaskProviderRegistry();
        registry.RegisterProvider(new AzureDevOpsTaskProvider(Config));
        registry.RegisterProvider(new GitHubProjectsTaskProvider(Config.Tasks?.Providers?.GitHub ?? new GitHubProviderConfig()));
        registry.RegisterProvider(new GiteaTaskProvider(Config));
        foreach (var provider in registry.GetProviders().Where(p => p.IsEnabled))
        {
            try { await provider.AuthenticateAsync(); }
            catch (Exception ex) { Log.Warning(ex, "[projects] {Provider} sign-in failed", provider.ProviderId); }
        }
        _taskRegistry = registry;

        var buckets = new List<string> { TaskBoard.AllBuckets };
        foreach (var provider in registry.GetProviders().Where(p => p.IsEnabled))
        {
            try { buckets.AddRange((await provider.ListBucketsAsync()).Select(b => b.Name)); }
            catch (Exception ex) { Log.Debug(ex, "[projects] {Provider} buckets unavailable", provider.ProviderId); }
        }
        ProjectBuckets = buckets.Distinct().ToList();
    }

    /// <summary>List tasks from every enabled provider, or only <paramref name="provider"/>.</summary>
    public async Task LoadProjectTasksAsync(string? provider = null)
    {
        if (DemoMode) { NotifyCacheChanged(ProjectTasksKey); return; }
        using var scope = TrackCacheLoad(ProjectTasksKey);
        await EnsureTaskRegistryAsync();
        if (_taskRegistry is not { } registry) return;
        // Closed tasks are fetched too: the State columns and Show Closed need them. The macOS load size.
        var filter = new TaskFilter { IncludeClosed = true, Limit = 500 };
        ProjectTasks = string.IsNullOrEmpty(provider)
            ? await registry.ListAllTasksAsync(filter)
            : await registry.ListTasksAsync(provider, filter);
        ProjectTasksLoaded = true;
    }

    /// <summary>Re-read one work item and patch it in place (macOS refreshLocalTask).</summary>
    public async Task<UnifiedTask?> RefreshProjectTaskAsync(UnifiedTask task)
    {
        if (DemoMode || DevOpsService is not { } devOps || !int.TryParse(task.Id, out var id)) return null;
        var item = await devOps.GetWorkItemAsync(id);
        if (item == null) return null;
        var fresh = AzureDevOpsTaskProvider.MapToUnifiedTask(item);
        TaskBoard.Replace(ProjectTasks, task, fresh);
        TaskBoard.Replace(MineExtra, task, fresh);
        NotifyCacheChanged(ProjectTasksKey);
        return fresh;
    }

    /// <summary>Apply a field update from a board drop; the card patches from what Azure DevOps returns.</summary>
    public async Task<bool> UpdateProjectTaskAsync(UnifiedTask task, UpdateWorkItemRequest request)
    {
        if (!int.TryParse(task.Id, out var id) || task.Provider != "azdevops") return false;
        if (DemoMode) return false;
        if (DevOpsService is not { } devOps) return false;
        var updated = await devOps.UpdateWorkItemAsync(id, request);
        if (updated == null) return false;
        TaskBoard.ApplyUpdate(task, updated);
        NotifyCacheChanged(ProjectTasksKey);
        return true;
    }

    // MARK: - Mine

    /// <summary>
    /// The @Me set, the GitHub login, and any @Me work items the loaded list
    /// does not contain (it holds the most recent; mine may be older).
    /// </summary>
    public async Task EnsureMineAsync(bool force = false)
    {
        if (DemoMode || IsCacheLoading("Mine") || (MyWorkItemIds != null && !force)) return;
        using var scope = TrackCacheLoad("Mine");
        try
        {
            if (DevOpsService is { } devOps)
            {
                var ids = await devOps.GetMyOpenWorkItemIdsAsync();
                var loaded = ProjectTasks.Where(t => t.Provider == "azdevops").Select(t => t.Id).ToHashSet();
                var missing = ids.Where(id => !loaded.Contains(id.ToString())).Take(200).ToList();
                MineExtra.Clear();
                if (missing.Count > 0)
                    MineExtra.AddRange((await devOps.GetWorkItemsByIdsAsync(missing)).Select(AzureDevOpsTaskProvider.MapToUnifiedTask));
                MyWorkItemIds = ids;
            }
            else MyWorkItemIds = new();

            if (GitHubViewerLogin == null)
            {
                try
                {
                    using var github = new GitHubPullRequestService(Config.GitHubProviderOrDefault());
                    GitHubViewerLogin = (await github.GetViewerAsync()).Login ?? "";
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[projects] GitHub viewer unavailable for Mine");
                    GitHubViewerLogin = "";
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[projects] Could not load Mine");
            MyWorkItemIds ??= new();
        }
    }

    // MARK: - Stored queries

    /// <summary>
    /// Enumerate every Shared Query and run each one, a few at a time. Kept
    /// for five minutes across tab switches, or until <paramref name="force"/>.
    /// </summary>
    public async Task LoadQueriesAsync(bool force = false)
    {
        if (DemoMode || DevOpsService is not { } devOps || IsCacheLoading(QueriesKey)) return;
        if (!force && QueryRuns.Count > 0 && DateTime.UtcNow - _queriesLoadedAt < QueriesFreshness) return;
        using var scope = TrackCacheLoad(QueriesKey);
        if (QueryRuns.Count == 0) QueriesMessage = "Loading shared queries…";
        try
        {
            var queries = await devOps.GetSharedQueriesAsync();
            if (queries.Count == 0)
            {
                QueriesMessage = "No shared queries found. Sign in to Azure DevOps and refresh.";
                return;
            }
            var runs = new List<QueryRunDisplay>();
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(queries.Select(async query =>
            {
                await gate.WaitAsync();
                try
                {
                    if (await devOps.RunStoredQueryAsync(query.Id) is { } run)
                        lock (runs) runs.Add(Core.Models.Projects.QueryRuns.From(query, run));
                }
                catch (Exception ex) { Log.Warning(ex, "[projects] Query {Query} failed", query.Name); }
                finally { gate.Release(); }
            }));
            // Runs finish in any order; keep the Azure DevOps order, which Query Order shows.
            var position = queries.Select((q, i) => (q.Id, i)).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().i);
            QueryRuns = runs.OrderBy(r => position.GetValueOrDefault(r.Query.Id, int.MaxValue)).ToList();
            _queriesLoadedAt = DateTime.UtcNow;
            QueriesMessage = null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[projects] Shared queries failed");
            if (QueryRuns.Count == 0) QueriesMessage = $"Could not load the shared queries: {ex.Message}";
        }
    }

    /// <summary>The web page of a stored query, for its header's Open button.</summary>
    public string? StoredQueryUrl(AdoSharedQuery query) =>
        DemoMode ? $"{ProjectsDemoData.Organization}/{ProjectsDemoData.Project}/_queries/query/{query.Id}"
        : DevOpsService?.StoredQueryWebUrl(query.Id);

    /// <summary>The Queries fallback without stored queries: the most recent work items.</summary>
    public List<WorkItem> RecentWorkItems { get; private set; } = new();

    public async Task LoadRecentWorkItemsAsync()
    {
        if (DemoMode) { RecentWorkItems = ProjectsDemoData.WorkItems(DateTime.UtcNow); return; }
        if (DevOpsService is not { } devOps) return;
        using var scope = TrackCacheLoad("WorkItems");
        RecentWorkItems = await devOps.GetWorkItemsAsync(limit: 200);
    }

    // MARK: - Strip figures

    /// <summary>Fetch <see cref="MyWorkItems"/> and the sprints unless loading or fresh.</summary>
    public async Task LoadMyWorkItemsAsync(bool force = false)
    {
        if (DemoMode || DevOpsService is not { } devOps || IsCacheLoading(MyWorkItemsKey)) return;
        if (!force && _myWorkItemsAt is { } at
            && DateTime.Now - at < (MyWorkItems is { Count: > 0 } ? TimeSpan.FromMinutes(Config.CacheMinutes) : MyWorkItemsRetry))
            return;
        using var scope = TrackCacheLoad(MyWorkItemsKey);
        try
        {
            var items = await Task.Run(() => devOps.GetMyOpenWorkItemsAsync());
            List<WorkItem> ancestors;
            try { ancestors = await devOps.GetInitiativeAncestorsAsync(items); }
            catch (Exception) { ancestors = new(); Log.Warning("Initiative ancestry unavailable"); }
            MyWorkItems = items;
            InitiativeAncestors = ancestors;
            try { Sprints = await devOps.GetSprintsAsync(); }
            catch (Exception ex) { Log.Debug(ex, "[projects] Sprints unavailable"); }
            Log.Information("Loaded {Count} of my open work items", items.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load my open work items");
        }
        finally
        {
            _myWorkItemsAt = DateTime.Now;
        }
    }

    /// <summary>The user's open GitHub issues for the strip, fetched once.</summary>
    public async Task EnsureGitHubIssuesAsync()
    {
        if (DemoMode || GitHubIssues != null || IsCacheLoading(IssuesKey)) return;
        using var scope = TrackCacheLoad(IssuesKey);
        try
        {
            using var service = new GitHubPullRequestService(Config.GitHubProviderOrDefault());
            GitHubIssues = await service.GetMyIssuesAsync();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[widgets] GitHub issues unavailable");
            GitHubIssues = new();
        }
    }

    // MARK: - GitHub Project board

    public async Task LoadGitHubProjectAsync()
    {
        if (DemoMode || IsCacheLoading(GitHubProjectKey)) return;
        var gh = Config.Tasks?.Providers?.GitHub;
        if (gh is not { Enabled: true })
        {
            GitHubProjectMessage = "GitHub is not configured";
            NotifyCacheChanged(GitHubProjectKey);
            return;
        }
        using var scope = TrackCacheLoad(GitHubProjectKey);
        try
        {
            var service = new GitHubProjectsService(gh);
            if (!await service.AuthenticateAsync())
            {
                GitHubProjectMessage = "GitHub sign-in failed";
                return;
            }
            var scopeKind = gh.ProjectScope?.ToLowerInvariant() switch
            {
                "user" => ProjectScope.User,
                "repository" or "repo" => ProjectScope.Repository,
                _ => ProjectScope.Organization,
            };
            var owner = gh.Organization ?? gh.Owner ?? "";
            var projectId = gh.ProjectNumber is int number
                ? (await service.GetProjectAsync(scopeKind, owner, number, gh.Repo))?.Id
                : (await service.ListProjectsAsync(scopeKind, owner, gh.Repo, limit: 1)).FirstOrDefault()?.Id;
            if (projectId == null)
            {
                GitHubProjectMessage = "No project found";
                return;
            }
            GitHubProjectId = projectId;
            GitHubProjectStatus = await service.GetStatusFieldAsync(projectId);
            GitHubProjectItems = await service.ListProjectItemsAsync(projectId, 100);
            GitHubProjectMessage = null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[projects] GitHub Project failed to load");
            GitHubProjectMessage = $"Could not load the project: {ex.Message}";
        }
    }

    // MARK: - Links

    /// <summary>A work item or GitHub issue link waiting for the Projects tab.</summary>
    public FleetMateLink? PendingProjectsLink { get; set; }

    /// <summary>Raised after a Projects link is queued, for a Projects tab that is already showing.</summary>
    public event Action? ProjectsLinkRequested;

    public void RequestProjectsLink(FleetMateLink link)
    {
        PendingProjectsLink = link;
        ProjectsLinkRequested?.Invoke();
    }
}
