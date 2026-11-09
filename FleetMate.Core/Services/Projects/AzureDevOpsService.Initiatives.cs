using System.Net.Http.Json;
using FleetMate.Core.Models.Projects;

namespace FleetMate.Core.Services.Projects;

public partial class AzureDevOpsService
{
    /// <summary>Resolve shared parents using bounded metadata batches, stopping at an Initiative.</summary>
    public async Task<List<WorkItem>> GetInitiativeAncestorsAsync(IReadOnlyList<WorkItem> items, CancellationToken cancellationToken = default)
    {
        if (items.Count == 0) return new();
        if (!await SetAuthorizationAsync()) throw new InvalidOperationException("Sign-in unavailable.");
        var index = items.GroupBy(w => w.Id).ToDictionary(g => g.Key, g => g.Last());
        var requested = items.Select(w => w.Id).ToHashSet();
        var frontier = items.ToList();
        var ancestors = new List<WorkItem>();
        var queried = 0;
        for (var depth = 0; depth < WorkItemStats.InitiativeMaxDepth; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = frontier.Where(w => !w.WorkItemType.Equals("Initiative", StringComparison.OrdinalIgnoreCase))
                .Select(w => w.HierarchyParentId).OfType<int>().Distinct().Except(requested).Order()
                .Take(Math.Max(0, 2000 - queried)).ToList();
            if (ids.Count == 0) break;
            queried += ids.Count;
            requested.UnionWith(ids);
            frontier = new();
            foreach (var chunk in ids.Chunk(200))
            {
                using var response = await _client.PostAsJsonAsync("_apis/wit/workitemsbatch?api-version=7.0", new
                {
                    ids = chunk,
                    fields = new[] { "System.Id", "System.Title", "System.WorkItemType", "System.Parent" },
                    errorPolicy = "Omit",
                }, _jsonOptions, cancellationToken);
                response.EnsureSuccessStatusCode();
                var batch = await response.Content.ReadFromJsonAsync<WorkItemBatchResponse>(_jsonOptions, cancellationToken);
                foreach (var item in batch?.Value ?? new())
                {
                    if (!index.TryAdd(item.Id, item)) continue;
                    ancestors.Add(item);
                    frontier.Add(item);
                }
            }
        }
        return ancestors;
    }
}
