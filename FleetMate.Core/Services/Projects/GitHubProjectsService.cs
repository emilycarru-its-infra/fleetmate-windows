using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Projects;
using Serilog;

namespace FleetMate.Core.Services.Projects;

/// <summary>
/// Service for interacting with GitHub Projects v2 via the GraphQL API.
/// Supports organization, user, and repository-level projects.
/// </summary>
public class GitHubProjectsService : IDisposable
{
    private readonly GitHubGraphQLClient _client;
    private readonly GitHubProviderConfig _config;

    public GitHubProjectsService(GitHubProviderConfig config)
    {
        _config = config;
        _client = new GitHubGraphQLClient(config);
    }

    public async Task<bool> AuthenticateAsync(CancellationToken ct = default)
        => await _client.AuthenticateAsync(ct);

    // ──────────────────────────── Projects ────────────────────────────

    /// <summary>
    /// Lists projects v2 for the configured scope (org, user, or repo).
    /// </summary>
    public async Task<List<GitHubProject>> ListProjectsAsync(
        ProjectScope scope, string owner, string? repo = null,
        bool includeClosed = false, int limit = 20, CancellationToken ct = default)
    {
        var query = scope switch
        {
            ProjectScope.Organization => @"
                query($login: String!, $first: Int!, $after: String) {
                    organization(login: $login) {
                        projectsV2(first: $first, after: $after, orderBy: {field: UPDATED_AT, direction: DESC}) {
                            nodes {
                                id number title shortDescription url closed public
                                createdAt updatedAt closedAt
                            }
                            pageInfo { hasNextPage endCursor }
                        }
                    }
                }",
            ProjectScope.User => @"
                query($login: String!, $first: Int!, $after: String) {
                    user(login: $login) {
                        projectsV2(first: $first, after: $after, orderBy: {field: UPDATED_AT, direction: DESC}) {
                            nodes {
                                id number title shortDescription url closed public
                                createdAt updatedAt closedAt
                            }
                            pageInfo { hasNextPage endCursor }
                        }
                    }
                }",
            ProjectScope.Repository => @"
                query($owner: String!, $name: String!, $first: Int!, $after: String) {
                    repository(owner: $owner, name: $name) {
                        projectsV2(first: $first, after: $after, orderBy: {field: UPDATED_AT, direction: DESC}) {
                            nodes {
                                id number title shortDescription url closed public
                                createdAt updatedAt closedAt
                            }
                            pageInfo { hasNextPage endCursor }
                        }
                    }
                }",
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };

        var vars = scope == ProjectScope.Repository
            ? new Dictionary<string, object> { ["owner"] = owner, ["name"] = repo!, ["first"] = limit }
            : new Dictionary<string, object> { ["login"] = owner, ["first"] = limit };

        var data = await _client.ExecuteRawAsync(query, vars, ct);

        var rootProp = scope switch
        {
            ProjectScope.Organization => "organization",
            ProjectScope.User => "user",
            ProjectScope.Repository => "repository",
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };

        var projects = ParseProjects(data.GetProperty(rootProp).GetProperty("projectsV2"));
        if (!includeClosed)
            projects = projects.Where(p => !p.Closed).ToList();

        return projects;
    }

    /// <summary>
    /// Gets a single project by number.
    /// </summary>
    public async Task<GitHubProject?> GetProjectAsync(
        ProjectScope scope, string owner, int projectNumber, string? repo = null,
        CancellationToken ct = default)
    {
        var query = scope switch
        {
            ProjectScope.Organization => @"
                query($login: String!, $number: Int!) {
                    organization(login: $login) {
                        projectV2(number: $number) {
                            id number title shortDescription url closed public
                            createdAt updatedAt closedAt
                        }
                    }
                }",
            ProjectScope.User => @"
                query($login: String!, $number: Int!) {
                    user(login: $login) {
                        projectV2(number: $number) {
                            id number title shortDescription url closed public
                            createdAt updatedAt closedAt
                        }
                    }
                }",
            ProjectScope.Repository => @"
                query($owner: String!, $name: String!, $number: Int!) {
                    repository(owner: $owner, name: $name) {
                        projectV2(number: $number) {
                            id number title shortDescription url closed public
                            createdAt updatedAt closedAt
                        }
                    }
                }",
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };

        var vars = scope == ProjectScope.Repository
            ? new Dictionary<string, object> { ["owner"] = owner, ["name"] = repo!, ["number"] = projectNumber }
            : new Dictionary<string, object> { ["login"] = owner, ["number"] = projectNumber };

        var data = await _client.ExecuteRawAsync(query, vars, ct);

        var rootProp = scope switch
        {
            ProjectScope.Organization => "organization",
            ProjectScope.User => "user",
            ProjectScope.Repository => "repository",
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };

        var projectEl = data.GetProperty(rootProp).GetProperty("projectV2");
        return ParseProject(projectEl);
    }

    // ──────────────────────────── Items ────────────────────────────

    /// <summary>
    /// Lists items in a project. Uses the project node ID.
    /// </summary>
    public async Task<List<GitHubProjectItem>> ListProjectItemsAsync(
        string projectId, int limit = 100, bool includeArchived = false,
        CancellationToken ct = default)
    {
        const string query = @"
            query($projectId: ID!, $first: Int!, $after: String) {
                node(id: $projectId) {
                    ... on ProjectV2 {
                        items(first: $first, after: $after) {
                            nodes {
                                id type createdAt updatedAt isArchived
                                content {
                                    ... on Issue {
                                        id number title body state url
                                        createdAt updatedAt closedAt
                                        assignees(first: 10) { nodes { login } }
                                        labels(first: 20) { nodes { name } }
                                        repository { nameWithOwner }
                                    }
                                    ... on PullRequest {
                                        id number title body state url
                                        createdAt updatedAt closedAt
                                        assignees(first: 10) { nodes { login } }
                                        labels(first: 20) { nodes { name } }
                                        repository { nameWithOwner }
                                    }
                                    ... on DraftIssue {
                                        title body
                                    }
                                }
                                fieldValues(first: 20) {
                                    nodes {
                                        ... on ProjectV2ItemFieldTextValue {
                                            text
                                            field { ... on ProjectV2Field { id name } }
                                        }
                                        ... on ProjectV2ItemFieldNumberValue {
                                            number
                                            field { ... on ProjectV2Field { id name } }
                                        }
                                        ... on ProjectV2ItemFieldDateValue {
                                            date
                                            field { ... on ProjectV2Field { id name } }
                                        }
                                        ... on ProjectV2ItemFieldSingleSelectValue {
                                            name optionId
                                            field { ... on ProjectV2SingleSelectField { id name } }
                                        }
                                        ... on ProjectV2ItemFieldIterationValue {
                                            title iterationId
                                            field { ... on ProjectV2IterationField { id name } }
                                        }
                                    }
                                }
                            }
                            pageInfo { hasNextPage endCursor }
                        }
                    }
                }
            }";

        var allItems = new List<GitHubProjectItem>();
        string? cursor = null;

        while (true)
        {
            var vars = new Dictionary<string, object?>
            {
                ["projectId"] = projectId,
                ["first"] = Math.Min(limit - allItems.Count, 100),
                ["after"] = cursor
            };

            var data = await _client.ExecuteRawAsync(query, vars, ct);
            var items = data.GetProperty("node").GetProperty("items");
            var nodes = items.GetProperty("nodes");

            foreach (var node in nodes.EnumerateArray())
            {
                var item = ParseProjectItem(node);
                if (!includeArchived && item.IsArchived) continue;
                allItems.Add(item);
            }

            var pageInfo = items.GetProperty("pageInfo");
            if (!pageInfo.GetProperty("hasNextPage").GetBoolean() || allItems.Count >= limit)
                break;

            cursor = pageInfo.GetProperty("endCursor").GetString();
        }

        return allItems;
    }

    // ──────────────────────────── Fields ────────────────────────────

    /// <summary>
    /// Lists fields for a project.
    /// </summary>
    public async Task<List<GitHubProjectField>> ListProjectFieldsAsync(
        string projectId, CancellationToken ct = default)
    {
        const string query = @"
            query($projectId: ID!) {
                node(id: $projectId) {
                    ... on ProjectV2 {
                        fields(first: 50) {
                            nodes {
                                ... on ProjectV2Field {
                                    id name dataType
                                }
                                ... on ProjectV2SingleSelectField {
                                    id name dataType
                                    options { id name color description }
                                }
                                ... on ProjectV2IterationField {
                                    id name dataType
                                    configuration {
                                        iterations { id title startDate duration }
                                    }
                                }
                            }
                        }
                    }
                }
            }";

        var vars = new Dictionary<string, object> { ["projectId"] = projectId };
        var data = await _client.ExecuteRawAsync(query, vars, ct);
        var nodes = data.GetProperty("node").GetProperty("fields").GetProperty("nodes");

        var fields = new List<GitHubProjectField>();
        foreach (var node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("id", out _)) continue; // Skip __typename-only nodes

            var field = new GitHubProjectField
            {
                Id = node.GetProperty("id").GetString()!,
                Name = node.GetProperty("name").GetString()!,
                DataType = node.TryGetProperty("dataType", out var dt) ? dt.GetString()! : "TEXT"
            };

            if (node.TryGetProperty("options", out var options))
            {
                foreach (var opt in options.EnumerateArray())
                {
                    field.Options.Add(new GitHubProjectSelectOption
                    {
                        Id = opt.GetProperty("id").GetString()!,
                        Name = opt.GetProperty("name").GetString()!,
                        Color = opt.TryGetProperty("color", out var c) ? c.GetString() : null,
                        Description = opt.TryGetProperty("description", out var d) ? d.GetString() : null
                    });
                }
            }

            if (node.TryGetProperty("configuration", out var config) &&
                config.TryGetProperty("iterations", out var iters))
            {
                foreach (var iter in iters.EnumerateArray())
                {
                    field.Iterations.Add(new GitHubProjectIteration
                    {
                        Id = iter.GetProperty("id").GetString()!,
                        Title = iter.GetProperty("title").GetString()!,
                        StartDate = iter.TryGetProperty("startDate", out var sd) ? sd.GetString() : null,
                        Duration = iter.TryGetProperty("duration", out var dur) ? dur.GetInt32() : null
                    });
                }
            }

            fields.Add(field);
        }

        return fields;
    }

    /// <summary>
    /// Gets the Status field (single-select) for a project.
    /// </summary>
    public async Task<GitHubProjectField?> GetStatusFieldAsync(
        string projectId, CancellationToken ct = default)
    {
        var fields = await ListProjectFieldsAsync(projectId, ct);
        return fields.FirstOrDefault(f =>
            f.Name.Equals("Status", StringComparison.OrdinalIgnoreCase) &&
            f.DataType == "SINGLE_SELECT");
    }

    // ──────────────────────────── Views ────────────────────────────

    /// <summary>
    /// Lists views (Board, Table, Roadmap) for a project.
    /// </summary>
    public async Task<List<GitHubProjectView>> ListProjectViewsAsync(
        string projectId, CancellationToken ct = default)
    {
        const string query = @"
            query($projectId: ID!) {
                node(id: $projectId) {
                    ... on ProjectV2 {
                        views(first: 20) {
                            nodes { id number name layout }
                        }
                    }
                }
            }";

        var vars = new Dictionary<string, object> { ["projectId"] = projectId };
        var data = await _client.ExecuteRawAsync(query, vars, ct);
        var nodes = data.GetProperty("node").GetProperty("views").GetProperty("nodes");

        var views = new List<GitHubProjectView>();
        foreach (var node in nodes.EnumerateArray())
        {
            views.Add(new GitHubProjectView
            {
                Id = node.GetProperty("id").GetString()!,
                Number = node.GetProperty("number").GetInt32(),
                Name = node.GetProperty("name").GetString()!,
                Layout = node.GetProperty("layout").GetString()!
            });
        }
        return views;
    }

    // ──────────────────────────── Mutations ────────────────────────────

    /// <summary>
    /// Adds an existing issue or PR to a project.
    /// </summary>
    public async Task<string> AddItemToProjectAsync(
        string projectId, string contentId, CancellationToken ct = default)
    {
        const string mutation = @"
            mutation($projectId: ID!, $contentId: ID!) {
                addProjectV2ItemById(input: { projectId: $projectId, contentId: $contentId }) {
                    item { id }
                }
            }";

        var vars = new Dictionary<string, object> { ["projectId"] = projectId, ["contentId"] = contentId };
        var data = await _client.ExecuteRawAsync(mutation, vars, ct);
        return data.GetProperty("addProjectV2ItemById").GetProperty("item").GetProperty("id").GetString()!;
    }

    /// <summary>
    /// Adds a draft issue to a project.
    /// </summary>
    public async Task<string> AddDraftItemAsync(
        string projectId, string title, string? body = null,
        CancellationToken ct = default)
    {
        const string mutation = @"
            mutation($projectId: ID!, $title: String!, $body: String) {
                addProjectV2DraftIssue(input: { projectId: $projectId, title: $title, body: $body }) {
                    projectItem { id }
                }
            }";

        var vars = new Dictionary<string, object?> { ["projectId"] = projectId, ["title"] = title, ["body"] = body };
        var data = await _client.ExecuteRawAsync(mutation, vars, ct);
        return data.GetProperty("addProjectV2DraftIssue").GetProperty("projectItem").GetProperty("id").GetString()!;
    }

    /// <summary>
    /// Updates a field value on a project item.
    /// </summary>
    public async Task UpdateItemFieldValueAsync(
        string projectId, string itemId, string fieldId, object value,
        CancellationToken ct = default)
    {
        const string mutation = @"
            mutation($projectId: ID!, $itemId: ID!, $fieldId: ID!, $value: ProjectV2FieldValue!) {
                updateProjectV2ItemFieldValue(input: {
                    projectId: $projectId, itemId: $itemId, fieldId: $fieldId, value: $value
                }) {
                    projectV2Item { id }
                }
            }";

        var vars = new Dictionary<string, object>
        {
            ["projectId"] = projectId,
            ["itemId"] = itemId,
            ["fieldId"] = fieldId,
            ["value"] = value
        };

        await _client.ExecuteRawAsync(mutation, vars, ct);
    }

    /// <summary>
    /// Moves an item to a specific status by setting the status field's single-select value.
    /// </summary>
    public async Task MoveItemToStatusAsync(
        string projectId, string itemId, string statusFieldId, string optionId,
        CancellationToken ct = default)
    {
        await UpdateItemFieldValueAsync(
            projectId, itemId, statusFieldId,
            new Dictionary<string, string> { ["singleSelectOptionId"] = optionId }, ct);
    }

    /// <summary>
    /// Removes an item from the project.
    /// </summary>
    public async Task DeleteItemAsync(
        string projectId, string itemId, CancellationToken ct = default)
    {
        const string mutation = @"
            mutation($projectId: ID!, $itemId: ID!) {
                deleteProjectV2Item(input: { projectId: $projectId, itemId: $itemId }) {
                    deletedItemId
                }
            }";

        var vars = new Dictionary<string, object> { ["projectId"] = projectId, ["itemId"] = itemId };
        await _client.ExecuteRawAsync(mutation, vars, ct);
    }

    /// <summary>
    /// Archives or unarchives a project item.
    /// </summary>
    public async Task ArchiveItemAsync(
        string projectId, string itemId, bool archive = true,
        CancellationToken ct = default)
    {
        const string mutation = @"
            mutation($projectId: ID!, $itemId: ID!) {
                archiveProjectV2Item(input: { projectId: $projectId, itemId: $itemId }) {
                    item { id }
                }
            }";

        const string unarchiveMutation = @"
            mutation($projectId: ID!, $itemId: ID!) {
                unarchiveProjectV2Item(input: { projectId: $projectId, itemId: $itemId }) {
                    item { id }
                }
            }";

        var vars = new Dictionary<string, object> { ["projectId"] = projectId, ["itemId"] = itemId };
        await _client.ExecuteRawAsync(archive ? mutation : unarchiveMutation, vars, ct);
    }

    // ──────────────────────────── Issue & Project Creation ────────────────────────────

    /// <summary>
    /// Creates a GitHub Issue in a repository via GraphQL.
    /// </summary>
    public async Task<(string Id, int Number, string? Url)> CreateIssueAsync(
        string repositoryId, string title, string? body = null,
        IEnumerable<string>? assigneeIds = null, IEnumerable<string>? labelIds = null,
        string? milestoneId = null, CancellationToken ct = default)
    {
        const string mutation = @"
            mutation($repositoryId: ID!, $title: String!, $body: String, $assigneeIds: [ID!], $labelIds: [ID!], $milestoneId: ID) {
                createIssue(input: {
                    repositoryId: $repositoryId, title: $title, body: $body,
                    assigneeIds: $assigneeIds, labelIds: $labelIds, milestoneId: $milestoneId
                }) {
                    issue { id number url }
                }
            }";

        var vars = new Dictionary<string, object?> { ["repositoryId"] = repositoryId, ["title"] = title, ["body"] = body };
        if (assigneeIds?.Any() == true) vars["assigneeIds"] = assigneeIds.ToList();
        if (labelIds?.Any() == true) vars["labelIds"] = labelIds.ToList();
        if (milestoneId != null) vars["milestoneId"] = milestoneId;

        var data = await _client.ExecuteRawAsync(mutation, vars, ct);
        var issue = data.GetProperty("createIssue").GetProperty("issue");
        return (
            issue.GetProperty("id").GetString()!,
            issue.GetProperty("number").GetInt32(),
            issue.TryGetProperty("url", out var u) ? u.GetString() : null
        );
    }

    /// <summary>
    /// Creates a new GitHub Projects v2 project.
    /// </summary>
    public async Task<GitHubProject> CreateProjectAsync(
        string ownerId, string title, CancellationToken ct = default)
    {
        const string mutation = @"
            mutation($ownerId: ID!, $title: String!) {
                createProjectV2(input: { ownerId: $ownerId, title: $title }) {
                    projectV2 {
                        id number title shortDescription url closed public
                        createdAt updatedAt closedAt
                    }
                }
            }";

        var vars = new Dictionary<string, object> { ["ownerId"] = ownerId, ["title"] = title };
        var data = await _client.ExecuteRawAsync(mutation, vars, ct);
        return ParseProject(data.GetProperty("createProjectV2").GetProperty("projectV2"))
            ?? throw new InvalidOperationException("Failed to parse created project");
    }

    /// <summary>
    /// Gets the node ID for a repository.
    /// </summary>
    public async Task<string> GetRepositoryIdAsync(
        string owner, string name, CancellationToken ct = default)
    {
        const string query = @"
            query($owner: String!, $name: String!) {
                repository(owner: $owner, name: $name) { id }
            }";
        var vars = new Dictionary<string, object> { ["owner"] = owner, ["name"] = name };
        var data = await _client.ExecuteRawAsync(query, vars, ct);
        return data.GetProperty("repository").GetProperty("id").GetString()!;
    }

    /// <summary>
    /// Gets the node ID for an organization.
    /// </summary>
    public async Task<string> GetOrganizationIdAsync(
        string login, CancellationToken ct = default)
    {
        const string query = @"
            query($login: String!) {
                organization(login: $login) { id }
            }";
        var vars = new Dictionary<string, object> { ["login"] = login };
        var data = await _client.ExecuteRawAsync(query, vars, ct);
        return data.GetProperty("organization").GetProperty("id").GetString()!;
    }

    /// <summary>
    /// Gets the node ID for a user.
    /// </summary>
    public async Task<string> GetUserIdAsync(
        string login, CancellationToken ct = default)
    {
        const string query = @"
            query($login: String!) {
                user(login: $login) { id }
            }";
        var vars = new Dictionary<string, object> { ["login"] = login };
        var data = await _client.ExecuteRawAsync(query, vars, ct);
        return data.GetProperty("user").GetProperty("id").GetString()!;
    }

    /// <summary>
    /// Gets the node ID for an owner, trying organization or user based on scope.
    /// </summary>
    public async Task<string> GetOwnerIdAsync(
        string login, ProjectScope scope, CancellationToken ct = default)
    {
        return scope switch
        {
            ProjectScope.User => await GetUserIdAsync(login, ct),
            ProjectScope.Organization => await GetOrganizationIdAsync(login, ct),
            _ => await GetOwnerIdWithFallbackAsync(login, ct)
        };
    }

    private async Task<string> GetOwnerIdWithFallbackAsync(
        string login, CancellationToken ct = default)
    {
        try
        {
            return await GetOrganizationIdAsync(login, ct);
        }
        catch
        {
            return await GetUserIdAsync(login, ct);
        }
    }

    /// <summary>
    /// Lists repositories for an organization (ordered by most recently updated).
    /// </summary>
    public async Task<List<(string Owner, string Name)>> ListOrganizationReposAsync(
        string login, int limit = 30, CancellationToken ct = default)
    {
        const string query = @"
            query($login: String!, $first: Int!) {
                organization(login: $login) {
                    repositories(first: $first, orderBy: {field: UPDATED_AT, direction: DESC}) {
                        nodes { nameWithOwner }
                    }
                }
            }";
        var vars = new Dictionary<string, object> { ["login"] = login, ["first"] = limit };
        var data = await _client.ExecuteRawAsync(query, vars, ct);
        var nodes = data.GetProperty("organization").GetProperty("repositories").GetProperty("nodes");
        return nodes.EnumerateArray()
            .Select(n => n.GetProperty("nameWithOwner").GetString()!)
            .Select(nwo => nwo.Split('/'))
            .Where(parts => parts.Length == 2)
            .Select(parts => (parts[0], parts[1]))
            .ToList();
    }

    /// <summary>
    /// Lists labels for a repository.
    /// </summary>
    public async Task<List<(string Id, string Name, string? Color)>> ListRepositoryLabelsAsync(
        string owner, string name, CancellationToken ct = default)
    {
        const string query = @"
            query($owner: String!, $name: String!) {
                repository(owner: $owner, name: $name) {
                    labels(first: 100, orderBy: {field: NAME, direction: ASC}) {
                        nodes { id name color }
                    }
                }
            }";
        var vars = new Dictionary<string, object> { ["owner"] = owner, ["name"] = name };
        var data = await _client.ExecuteRawAsync(query, vars, ct);
        var nodes = data.GetProperty("repository").GetProperty("labels").GetProperty("nodes");
        return nodes.EnumerateArray()
            .Select(n => (n.GetProperty("id").GetString()!, n.GetProperty("name").GetString()!,
                n.TryGetProperty("color", out var c) ? c.GetString() : null))
            .ToList();
    }

    /// <summary>
    /// Lists open milestones for a repository.
    /// </summary>
    public async Task<List<(string Id, int Number, string Title)>> ListRepositoryMilestonesAsync(
        string owner, string name, CancellationToken ct = default)
    {
        const string query = @"
            query($owner: String!, $name: String!) {
                repository(owner: $owner, name: $name) {
                    milestones(first: 20, states: OPEN, orderBy: {field: DUE_DATE, direction: ASC}) {
                        nodes { id number title }
                    }
                }
            }";
        var vars = new Dictionary<string, object> { ["owner"] = owner, ["name"] = name };
        var data = await _client.ExecuteRawAsync(query, vars, ct);
        var nodes = data.GetProperty("repository").GetProperty("milestones").GetProperty("nodes");
        return nodes.EnumerateArray()
            .Select(n => (n.GetProperty("id").GetString()!, n.GetProperty("number").GetInt32(), n.GetProperty("title").GetString()!))
            .ToList();
    }

    /// <summary>
    /// Lists users who can be assigned to issues in a repository.
    /// </summary>
    public async Task<List<(string Id, string Login)>> ListAssignableUsersAsync(
        string owner, string name, CancellationToken ct = default)
    {
        const string query = @"
            query($owner: String!, $name: String!) {
                repository(owner: $owner, name: $name) {
                    assignableUsers(first: 50) {
                        nodes { id login }
                    }
                }
            }";
        var vars = new Dictionary<string, object> { ["owner"] = owner, ["name"] = name };
        var data = await _client.ExecuteRawAsync(query, vars, ct);
        var nodes = data.GetProperty("repository").GetProperty("assignableUsers").GetProperty("nodes");
        return nodes.EnumerateArray()
            .Select(n => (n.GetProperty("id").GetString()!, n.GetProperty("login").GetString()!))
            .ToList();
    }

    // ──────────────────────────── Issue REST API (v3) ────────────────────────────

    private static readonly JsonSerializerOptions RestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Fetches full issue detail from the GitHub REST v3 API.
    /// </summary>
    public async Task<GitHubIssueDetail> GetIssueDetailAsync(
        string owner, string repo, int number, CancellationToken ct = default)
    {
        var bytes = await _client.ExecuteRestAsync($"/repos/{owner}/{repo}/issues/{number}", ct: ct);
        return JsonSerializer.Deserialize<GitHubIssueDetail>(bytes, RestJsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize issue detail");
    }

    /// <summary>
    /// Lists comments on an issue (up to 100).
    /// </summary>
    public async Task<List<GitHubComment>> ListIssueCommentsAsync(
        string owner, string repo, int number, CancellationToken ct = default)
    {
        var bytes = await _client.ExecuteRestAsync(
            $"/repos/{owner}/{repo}/issues/{number}/comments?per_page=100", ct: ct);
        return JsonSerializer.Deserialize<List<GitHubComment>>(bytes, RestJsonOptions) ?? new();
    }

    /// <summary>
    /// Creates a comment on an issue.
    /// </summary>
    public async Task<GitHubComment> CreateIssueCommentAsync(
        string owner, string repo, int number, string body,
        CancellationToken ct = default)
    {
        var bytes = await _client.ExecuteRestAsync(
            $"/repos/{owner}/{repo}/issues/{number}/comments",
            method: "POST",
            body: new Dictionary<string, object> { ["body"] = body },
            ct: ct);
        return JsonSerializer.Deserialize<GitHubComment>(bytes, RestJsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize created comment");
    }

    /// <summary>
    /// Updates an issue's title, body, state, labels, assignees, or milestone.
    /// </summary>
    public async Task<GitHubIssueDetail> UpdateIssueAsync(
        string owner, string repo, int number,
        string? title = null, string? body = null, string? state = null,
        List<string>? labels = null, List<string>? assignees = null,
        int? milestoneNumber = null, CancellationToken ct = default)
    {
        var updates = new Dictionary<string, object>();
        if (title != null) updates["title"] = title;
        if (body != null) updates["body"] = body;
        if (state != null) updates["state"] = state;
        if (labels != null) updates["labels"] = labels;
        if (assignees != null) updates["assignees"] = assignees;
        if (milestoneNumber.HasValue) updates["milestone"] = milestoneNumber.Value;

        var bytes = await _client.ExecuteRestAsync(
            $"/repos/{owner}/{repo}/issues/{number}",
            method: "PATCH",
            body: updates,
            ct: ct);
        return JsonSerializer.Deserialize<GitHubIssueDetail>(bytes, RestJsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize updated issue");
    }

    /// <summary>
    /// Locks an issue.
    /// </summary>
    public async Task LockIssueAsync(
        string owner, string repo, int number, string? lockReason = null,
        CancellationToken ct = default)
    {
        Dictionary<string, object>? body = null;
        if (lockReason != null)
            body = new Dictionary<string, object> { ["lock_reason"] = lockReason };
        await _client.ExecuteRestAsync(
            $"/repos/{owner}/{repo}/issues/{number}/lock",
            method: "PUT", body: body, ct: ct);
    }

    /// <summary>
    /// Unlocks an issue.
    /// </summary>
    public async Task UnlockIssueAsync(
        string owner, string repo, int number, CancellationToken ct = default)
    {
        await _client.ExecuteRestAsync(
            $"/repos/{owner}/{repo}/issues/{number}/lock",
            method: "DELETE", ct: ct);
    }

    /// <summary>
    /// Deletes an issue via GraphQL (REST API does not support deletion).
    /// </summary>
    public async Task DeleteIssueAsync(string nodeId, CancellationToken ct = default)
    {
        const string mutation = @"
            mutation($id: ID!) {
                deleteIssue(input: { issueId: $id }) {
                    repository { id }
                }
            }";
        await _client.ExecuteRawAsync(mutation, new { id = nodeId }, ct);
    }

    /// <summary>
    /// Pins an issue to its repository.
    /// </summary>
    public async Task PinIssueAsync(
        string nodeId, string repositoryId, CancellationToken ct = default)
    {
        const string mutation = @"
            mutation($issueId: ID!, $repoId: ID!) {
                pinIssue(input: { issueId: $issueId, repositoryId: $repoId }) {
                    issue { id }
                }
            }";
        await _client.ExecuteRawAsync(mutation,
            new Dictionary<string, object> { ["issueId"] = nodeId, ["repoId"] = repositoryId }, ct);
    }

    /// <summary>
    /// Unpins an issue from its repository.
    /// </summary>
    public async Task UnpinIssueAsync(string nodeId, CancellationToken ct = default)
    {
        const string mutation = @"
            mutation($issueId: ID!) {
                unpinIssue(input: { issueId: $issueId }) {
                    issue { id }
                }
            }";
        await _client.ExecuteRawAsync(mutation,
            new Dictionary<string, object> { ["issueId"] = nodeId }, ct);
    }

    /// <summary>
    /// Transfers an issue to a different repository.
    /// Returns the new issue's node ID.
    /// </summary>
    public async Task<string> TransferIssueAsync(
        string nodeId, string targetRepositoryId, CancellationToken ct = default)
    {
        const string mutation = @"
            mutation($issueId: ID!, $repoId: ID!) {
                transferIssue(input: { issueId: $issueId, repositoryId: $repoId }) {
                    issue { id number url }
                }
            }";
        var data = await _client.ExecuteRawAsync(mutation,
            new Dictionary<string, object> { ["issueId"] = nodeId, ["repoId"] = targetRepositoryId }, ct);
        return data.GetProperty("transferIssue").GetProperty("issue").GetProperty("id").GetString()!;
    }

    /// <summary>
    /// Lists branch names for a repository (up to 100).
    /// </summary>
    public async Task<List<string>> ListRepositoryBranchesAsync(
        string owner, string repo, CancellationToken ct = default)
    {
        var bytes = await _client.ExecuteRestAsync(
            $"/repos/{owner}/{repo}/branches?per_page=100", ct: ct);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.EnumerateArray()
            .Select(b => b.GetProperty("name").GetString()!)
            .ToList();
    }

    // ──────────────────────────── Parsing Helpers ────────────────────────────

    private static List<GitHubProject> ParseProjects(JsonElement projectsV2)
    {
        var projects = new List<GitHubProject>();
        foreach (var node in projectsV2.GetProperty("nodes").EnumerateArray())
        {
            var project = ParseProject(node);
            if (project != null) projects.Add(project);
        }
        return projects;
    }

    private static GitHubProject? ParseProject(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Null) return null;

        return new GitHubProject
        {
            Id = el.GetProperty("id").GetString()!,
            Number = el.GetProperty("number").GetInt32(),
            Title = el.GetProperty("title").GetString()!,
            ShortDescription = el.TryGetProperty("shortDescription", out var sd) ? sd.GetString() : null,
            Url = el.TryGetProperty("url", out var u) ? u.GetString() : null,
            Closed = el.TryGetProperty("closed", out var c) && c.GetBoolean(),
            Public = el.TryGetProperty("public", out var p) && p.GetBoolean(),
            CreatedAt = el.TryGetProperty("createdAt", out var ca) ? ca.GetDateTime() : DateTime.UtcNow,
            UpdatedAt = el.TryGetProperty("updatedAt", out var ua) ? ua.GetDateTime() : DateTime.UtcNow,
            ClosedAt = el.TryGetProperty("closedAt", out var cla) && cla.ValueKind != JsonValueKind.Null ? cla.GetDateTime() : null
        };
    }

    private static GitHubProjectItem ParseProjectItem(JsonElement node)
    {
        var item = new GitHubProjectItem
        {
            Id = node.GetProperty("id").GetString()!,
            Type = node.GetProperty("type").GetString()!,
            CreatedAt = node.TryGetProperty("createdAt", out var ca) ? ca.GetDateTime() : DateTime.UtcNow,
            UpdatedAt = node.TryGetProperty("updatedAt", out var ua) ? ua.GetDateTime() : DateTime.UtcNow,
            IsArchived = node.TryGetProperty("isArchived", out var ia) && ia.GetBoolean()
        };

        if (node.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
        {
            if (content.TryGetProperty("number", out _))
            {
                // Issue or Pull Request
                item.Content = new GitHubProjectItemContent
                {
                    Id = content.GetProperty("id").GetString()!,
                    Number = content.GetProperty("number").GetInt32(),
                    Title = content.GetProperty("title").GetString()!,
                    Body = content.TryGetProperty("body", out var b) ? b.GetString() : null,
                    State = content.TryGetProperty("state", out var s) ? s.GetString() : null,
                    Url = content.TryGetProperty("url", out var u) ? u.GetString() : null,
                    CreatedAt = content.TryGetProperty("createdAt", out var cca) ? cca.GetDateTime() : DateTime.UtcNow,
                    UpdatedAt = content.TryGetProperty("updatedAt", out var cua) ? cua.GetDateTime() : DateTime.UtcNow,
                    ClosedAt = content.TryGetProperty("closedAt", out var ccla) && ccla.ValueKind != JsonValueKind.Null ? ccla.GetDateTime() : null,
                    IsPullRequest = item.Type == "PULL_REQUEST"
                };

                if (content.TryGetProperty("repository", out var repo) &&
                    repo.TryGetProperty("nameWithOwner", out var nwo))
                {
                    item.Content.Repository = nwo.GetString();
                }

                if (content.TryGetProperty("assignees", out var assignees))
                {
                    foreach (var a in assignees.GetProperty("nodes").EnumerateArray())
                    {
                        var login = a.GetProperty("login").GetString();
                        if (login != null) item.Content.Assignees.Add(login);
                    }
                }

                if (content.TryGetProperty("labels", out var labels))
                {
                    foreach (var l in labels.GetProperty("nodes").EnumerateArray())
                    {
                        var name = l.GetProperty("name").GetString();
                        if (name != null) item.Content.Labels.Add(name);
                    }
                }
            }
            else if (content.TryGetProperty("title", out var draftTitle))
            {
                // Draft Issue
                item.DraftContent = new GitHubProjectDraftContent
                {
                    Title = draftTitle.GetString()!,
                    Body = content.TryGetProperty("body", out var db) ? db.GetString() : null
                };
            }
        }

        // Parse field values
        if (node.TryGetProperty("fieldValues", out var fieldValues))
        {
            foreach (var fv in fieldValues.GetProperty("nodes").EnumerateArray())
            {
                var fieldValue = ParseFieldValue(fv);
                if (fieldValue != null) item.FieldValues.Add(fieldValue);
            }
        }

        return item;
    }

    private static GitHubProjectFieldValue? ParseFieldValue(JsonElement fv)
    {
        if (!fv.TryGetProperty("field", out var field) || !field.TryGetProperty("id", out _))
            return null;

        var result = new GitHubProjectFieldValue
        {
            FieldId = field.GetProperty("id").GetString()!,
            FieldName = field.GetProperty("name").GetString()!
        };

        if (fv.TryGetProperty("text", out var text))
        {
            result.DataType = "TEXT";
            result.TextValue = text.GetString();
        }
        else if (fv.TryGetProperty("number", out var number))
        {
            result.DataType = "NUMBER";
            result.NumberValue = number.GetDouble();
        }
        else if (fv.TryGetProperty("date", out var date))
        {
            result.DataType = "DATE";
            if (date.ValueKind != JsonValueKind.Null)
                result.DateValue = DateTime.Parse(date.GetString()!);
        }
        else if (fv.TryGetProperty("name", out var selectName))
        {
            result.DataType = "SINGLE_SELECT";
            result.SingleSelectValue = selectName.GetString();
            result.SingleSelectOptionId = fv.TryGetProperty("optionId", out var oid) ? oid.GetString() : null;
        }
        else if (fv.TryGetProperty("title", out var iterTitle))
        {
            result.DataType = "ITERATION";
            result.IterationValue = iterTitle.GetString();
            result.IterationId = fv.TryGetProperty("iterationId", out var iid) ? iid.GetString() : null;
        }
        else
        {
            return null;
        }

        return result;
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
