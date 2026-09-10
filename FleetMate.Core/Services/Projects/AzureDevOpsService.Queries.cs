using System.Net.Http.Json;
using FleetMate.Core.Models.Projects;
using Serilog;

namespace FleetMate.Core.Services.Projects;

// Stored Queries (Shared Queries)
//
// The Projects tab's List view renders every Shared Query with its results
// expanded, the way the Azure DevOps query grid does. Three pieces make that
// possible: enumerating the Shared Queries folder tree, running a stored
// query by id (flat and tree results differ structurally), and the work item
// comments the lightbox shows. Mirrors the macOS AzureDevOpsService+Queries.

public partial class AzureDevOpsService
{
    /// <summary>
    /// Rows per query the UI will materialize. Queries can legally return
    /// thousands of items; past this cap the run is marked truncated.
    /// </summary>
    public const int StoredQueryRowCap = 500;

    /// <summary>
    /// One file's content from a git repository via the items API. The Manage
    /// roster fetches computers.csv this way so it never depends on a local
    /// checkout being current.
    /// </summary>
    public async Task<string?> GetRepositoryItemContentAsync(string project, string repository, string path)
    {
        if (!await SetAuthorizationAsync()) return null;

        try
        {
            var url = $"{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repository)}/items"
                + $"?path={Uri.EscapeDataString(path)}&includeContent=true&api-version=7.1";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");
            var response = await _client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("[azdo] Repository item {Project}/{Repo}{Path} → {Status}",
                    project, repository, path, response.StatusCode);
                return null;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("content", out var content) ? content.GetString() : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[azdo] Failed to fetch {Project}/{Repo}{Path}", project, repository, path);
            return null;
        }
    }

    /// <summary>
    /// All leaf queries under the project's "Shared Queries" folder, including
    /// those nested one folder deep, in display order.
    /// </summary>
    public async Task<List<AdoSharedQuery>> GetSharedQueriesAsync()
    {
        if (!await SetAuthorizationAsync()) return new List<AdoSharedQuery>();

        try
        {
            var url = $"{_config.Project}/_apis/wit/queries/Shared%20Queries?$depth=2&$expand=all&api-version=7.0";
            var root = await _client.GetFromJsonAsync<AdoQuery>(url, _jsonOptions);
            if (root == null) return new List<AdoSharedQuery>();

            var leaves = new List<AdoSharedQuery>();
            void Walk(AdoQuery node, string folderPath)
            {
                foreach (var child in node.Children ?? new List<AdoQuery>())
                {
                    if (child.IsLeafQuery)
                    {
                        leaves.Add(new AdoSharedQuery
                        {
                            Id = child.Id,
                            Name = child.Name ?? "Untitled query",
                            FolderPath = folderPath,
                            QueryType = child.ResolvedQueryType
                        });
                    }
                    else
                    {
                        var nested = folderPath.Length == 0
                            ? child.Name ?? ""
                            : $"{folderPath}/{child.Name}";
                        Walk(child, nested);
                    }
                }
            }
            Walk(root, "");
            Log.Debug("AzDO shared queries → {Count}", leaves.Count);
            return leaves;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to enumerate Shared Queries");
            return new List<AdoSharedQuery>();
        }
    }

    /// <summary>
    /// Run a stored query by id and materialize its rows in display order.
    /// Flat queries return workItems; tree and one-hop queries return
    /// workItemRelations edges that are rebuilt into a pre-order flattened
    /// hierarchy with depths, matching the Azure DevOps results grid.
    /// </summary>
    public async Task<StoredQueryRun?> RunStoredQueryAsync(string queryId)
    {
        if (!await SetAuthorizationAsync()) return null;

        try
        {
            var url = $"{_config.Project}/_apis/wit/wiql/{queryId}?api-version=7.0";
            var result = await _client.GetFromJsonAsync<WorkItemQueryResult>(url, _jsonOptions);
            if (result == null) return null;

            var queryType = string.IsNullOrEmpty(result.QueryType) ? "flat" : result.QueryType;

            if (result.WorkItemRelations is { Count: > 0 } links)
                return await MaterializeTreeAsync(queryId, queryType, links);

            var refs = result.WorkItems ?? new List<WorkItemReference>();
            var truncated = refs.Count > StoredQueryRowCap;
            var ids = refs.Take(StoredQueryRowCap).Select(r => r.Id).ToList();
            var items = await GetWorkItemsByIdsAsync(ids);
            var byId = items.ToDictionary(i => i.Id);
            var rows = ids
                .Where(byId.ContainsKey)
                .Select(id => new StoredQueryRow { Item = byId[id], Depth = 0, HasChildren = false })
                .ToList();
            return new StoredQueryRun { QueryId = queryId, QueryType = queryType, Rows = rows, Truncated = truncated };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Stored query {QueryId} failed", queryId);
            return null;
        }
    }

    /// <summary>
    /// Rebuild tree/one-hop edges into ordered rows. Edges arrive in display
    /// order: a nil source marks a root, any other edge hangs target under
    /// source. The same target can legally appear under several sources in
    /// one-hop queries; only the first placement is kept.
    /// </summary>
    private async Task<StoredQueryRun> MaterializeTreeAsync(string queryId, string queryType, List<WorkItemLink> links)
    {
        var order = new List<int>();
        var children = new Dictionary<int, List<int>>();
        var childIds = new HashSet<int>();

        foreach (var link in links)
        {
            if (link.Target?.Id is not { } targetId || targetId == 0) continue;
            if (link.Source?.Id is { } sourceId && sourceId != 0)
            {
                if (childIds.Add(targetId))
                {
                    if (!children.TryGetValue(sourceId, out var list))
                        children[sourceId] = list = new List<int>();
                    list.Add(targetId);
                }
            }
            else if (!order.Contains(targetId))
            {
                order.Add(targetId);
            }
        }

        var flattened = new List<(int Id, int Depth, bool HasChildren)>();
        var truncated = false;
        void Visit(int id, int depth)
        {
            if (flattened.Count >= StoredQueryRowCap) { truncated = true; return; }
            var kids = children.TryGetValue(id, out var list) ? list : new List<int>();
            flattened.Add((id, depth, kids.Count > 0));
            foreach (var child in kids) Visit(child, depth + 1);
        }
        foreach (var rootId in order) Visit(rootId, 0);

        var items = await GetWorkItemsByIdsAsync(flattened.Select(f => f.Id).ToList());
        var byId = items.ToDictionary(i => i.Id);
        var rows = flattened
            .Where(f => byId.ContainsKey(f.Id))
            .Select(f => new StoredQueryRow { Item = byId[f.Id], Depth = f.Depth, HasChildren = f.HasChildren })
            .ToList();
        return new StoredQueryRun { QueryId = queryId, QueryType = queryType, Rows = rows, Truncated = truncated };
    }

    /// <summary>Web URL for a stored query's results page.</summary>
    public string StoredQueryWebUrl(string queryId) =>
        $"{_config.BaseUrl}/{Uri.EscapeDataString(_config.Project)}/_queries/query/{queryId}/";

    /// <summary>Discussion comments on one work item, oldest first.</summary>
    public async Task<List<WorkItemComment>> GetWorkItemCommentsAsync(int workItemId)
    {
        if (!await SetAuthorizationAsync()) return new List<WorkItemComment>();

        try
        {
            var url = $"{_config.Project}/_apis/wit/workItems/{workItemId}/comments?api-version=7.0-preview.3";
            var result = await _client.GetFromJsonAsync<WorkItemCommentsResponse>(url, _jsonOptions);
            return result?.Comments?
                .OrderBy(c => c.CreatedDate ?? DateTime.MinValue)
                .ToList() ?? new List<WorkItemComment>();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load comments for work item {Id}", workItemId);
            return new List<WorkItemComment>();
        }
    }
}
