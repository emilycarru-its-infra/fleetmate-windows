using FleetMate.Core.Models.Projects;

namespace FleetMate.Core.Services.Projects;

public sealed record PipelineRepositoryScope(string Owner, string Repository)
{
    public string Key => $"{Owner}/{Repository}";
}
public sealed record PipelineRefreshTarget(PullRequestSource Source, string Container, string? Repository = null,
    long? RunId = null, IReadOnlyList<long>? BuildIds = null);

/// <summary>Small targeted batches, with rotating discovery so quiet repositories are not starved by busy runs.</summary>
public sealed class PipelineRefreshPlan
{
    private int _githubActive;
    private int _githubRepos;
    private int _azureActive;
    private int _azureProjects;
    private int _azureDiscoveryRound;
    private readonly Dictionary<string, int> _azureBuildCursors = new();

    public void PrioritizeSelection() => _azureDiscoveryRound = 0;

    public List<PipelineRefreshTarget> GitHub(IReadOnlyList<PipelineRun> runs, IReadOnlyList<PipelineRepositoryScope> repositories,
        PipelineRun? selected, string? selectedRepository)
    {
        var targets = new List<PipelineRefreshTarget>();
        if (selected?.Source == PullRequestSource.GitHub && !string.IsNullOrEmpty(selected.Repository))
            targets.Add(new(selected.Source, selected.Container, selected.Repository, selected.RunId));
        else if (repositories.FirstOrDefault(repo => repo.Key == selectedRepository) is { } scope)
            targets.Add(new(PullRequestSource.GitHub, scope.Owner, scope.Repository));
        var active = runs.Where(run => run.Source == PullRequestSource.GitHub && run.Status.IsActive() && !string.IsNullOrEmpty(run.Repository))
            .OrderBy(PipelineRunCache.Key).ToArray();
        foreach (var run in Rotate(active, ref _githubActive, (repositories.Count > 0 ? 3 : 4) - targets.Count))
        {
            if (targets.Any(target => target.Container == run.Container && target.RunId == run.RunId)) continue;
            if (targets.Count >= (repositories.Count > 0 ? 3 : 4)) break;
            targets.Add(new(run.Source, run.Container, run.Repository, run.RunId));
        }
        foreach (var repo in Rotate(repositories, ref _githubRepos, 4 - targets.Count))
        {
            if (targets.Count >= 4) break;
            if (!targets.Any(target => target.Container == repo.Owner && target.Repository == repo.Repository && target.RunId == null))
                targets.Add(new(PullRequestSource.GitHub, repo.Owner, repo.Repository));
        }
        return targets;
    }

    public List<PipelineRefreshTarget> AzureDevOps(IReadOnlyList<PipelineRun> runs, IReadOnlyList<string> projects,
        PipelineRun? selected, string? selectedRepository)
    {
        var candidates = runs.Where(run => run.Source == PullRequestSource.AzureDevOps && run.Status.IsActive())
            .Concat(selected?.Source == PullRequestSource.AzureDevOps ? new[] { selected! } : Array.Empty<PipelineRun>())
            .GroupBy(run => run.Container).OrderBy(group => group.Key).ToArray();
        var priority = selected?.Source == PullRequestSource.AzureDevOps ? selected.Container : null;
        var groups = candidates.Where(group => group.Key == priority)
            .Concat(Rotate(candidates.Where(group => group.Key != priority).ToArray(), ref _azureActive, priority != null ? 1 : 2)).Take(2);
        var result = groups.Select(group =>
        {
            var selectedId = selected?.Source == PullRequestSource.AzureDevOps && selected.Container == group.Key ? selected.RunId : (long?)null;
            var ids = group.Select(run => run.RunId).Distinct().Order().Where(id => id != selectedId).ToArray();
            var cursor = _azureBuildCursors.GetValueOrDefault(group.Key);
            var batch = Rotate(ids, ref cursor, selectedId != null ? 49 : 50).ToList();
            _azureBuildCursors[group.Key] = cursor;
            if (selectedId is { } id) batch.Insert(0, id);
            return new PipelineRefreshTarget(PullRequestSource.AzureDevOps, group.Key, BuildIds: batch);
        }).ToList();
        var selectedProject = selected?.Source == PullRequestSource.AzureDevOps ? selected.Container
            : projects.FirstOrDefault(project => selectedRepository == project || selectedRepository?.StartsWith(project + "/", StringComparison.Ordinal) == true);
        var discovery = selectedProject != null && _azureDiscoveryRound++ % 2 == 0 ? selectedProject
            : Rotate(projects, ref _azureProjects, 1).FirstOrDefault();
        if (discovery != null) result.Add(new(PullRequestSource.AzureDevOps, discovery));
        return result;
    }

    public static List<PipelineRefreshTarget> GitHubHistory(IEnumerable<PipelineRepositoryScope> repositories) => repositories
        .Distinct().Take(500).Select(repo => new PipelineRefreshTarget(PullRequestSource.GitHub, repo.Owner, repo.Repository)).ToList();

    public static List<PipelineRefreshTarget> AzureDevOpsHistory(IEnumerable<string> projects) => projects
        .Distinct().Take(200).Select(project => new PipelineRefreshTarget(PullRequestSource.AzureDevOps, project)).ToList();

    private static IEnumerable<T> Rotate<T>(IReadOnlyList<T> items, ref int cursor, int count)
    {
        if (items.Count == 0) return Array.Empty<T>();
        var start = cursor % items.Count;
        var take = Math.Min(count, items.Count);
        cursor = (start + take) % items.Count;
        return Enumerable.Range(0, take).Select(index => items[(start + index) % items.Count]).ToArray();
    }
}

public static class PipelineRunCache
{
    public const int MaximumRuns = 500;
    public static string Key(PipelineRun run) => $"{run.Source}:{run.Container}:{run.RunId}";

    /// <summary>Merge successful responses only; a failed scope supplies no replacements and keeps its old rows.</summary>
    public static List<PipelineRun> Merge(IEnumerable<PipelineRun> previous, IEnumerable<PipelineRun> updated,
        DateTime now, PipelineRun? selected = null)
    {
        var selectedKey = selected == null ? null : Key(selected);
        return previous.Concat(updated).GroupBy(Key).Select(group => group.Last())
            .Where(run => run.Status.IsActive() || run.SortDate >= now.AddDays(-7) || Key(run) == selectedKey)
            .OrderByDescending(run => Key(run) == selectedKey).ThenByDescending(run => run.Status.IsActive()).ThenByDescending(run => run.SortDate)
            .Take(MaximumRuns).OrderByDescending(run => run.SortDate).ToList();
    }

    public static bool SameContent(IReadOnlyList<PipelineRun>? left, IReadOnlyList<PipelineRun> right)
    {
        if (left == null || left.Count != right.Count) return false;
        return left.Zip(right).All(pair => Key(pair.First) == Key(pair.Second)
            && (pair.First.Status, pair.First.Repository, pair.First.PipelineId, pair.First.PipelineName, pair.First.RunNumber, pair.First.StartedAt, pair.First.FinishedAt,
                pair.First.Branch, pair.First.CommitSha, pair.First.TriggeredBy, pair.First.WebUrl)
            == (pair.Second.Status, pair.Second.Repository, pair.Second.PipelineId, pair.Second.PipelineName, pair.Second.RunNumber, pair.Second.StartedAt, pair.Second.FinishedAt,
                pair.Second.Branch, pair.Second.CommitSha, pair.Second.TriggeredBy, pair.Second.WebUrl));
    }
}
