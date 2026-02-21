using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.GitHub;

/// <summary>
/// Scope from which to query GitHub Projects v2.
/// </summary>
public enum ProjectScope
{
    Organization,
    User,
    Repository
}

/// <summary>
/// Represents a GitHub Projects v2 project.
/// </summary>
public class GitHubProject
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("shortDescription")]
    public string? ShortDescription { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("closed")]
    public bool Closed { get; set; }

    [JsonPropertyName("public")]
    public bool Public { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("closedAt")]
    public DateTime? ClosedAt { get; set; }
}

/// <summary>
/// Represents an item in a GitHub Projects v2 project.
/// Items can be Issues, Pull Requests, or Draft Issues.
/// </summary>
public class GitHubProjectItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty; // ISSUE, PULL_REQUEST, DRAFT_ISSUE, REDACTED

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("isArchived")]
    public bool IsArchived { get; set; }

    /// <summary>
    /// The content of this item (Issue or PR details). Null for DRAFT_ISSUE.
    /// </summary>
    public GitHubProjectItemContent? Content { get; set; }

    /// <summary>
    /// The draft title/body for DRAFT_ISSUE items.
    /// </summary>
    public GitHubProjectDraftContent? DraftContent { get; set; }

    /// <summary>
    /// Field values applied to this item within the project.
    /// </summary>
    public List<GitHubProjectFieldValue> FieldValues { get; set; } = new();
}

/// <summary>
/// Content of a project item when it's an Issue or Pull Request.
/// </summary>
public class GitHubProjectItemContent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; } // OPEN, CLOSED, MERGED (PRs)

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("closedAt")]
    public DateTime? ClosedAt { get; set; }

    /// <summary>Assignee login names.</summary>
    public List<string> Assignees { get; set; } = new();

    /// <summary>Label names.</summary>
    public List<string> Labels { get; set; } = new();

    /// <summary>Whether this is a pull request.</summary>
    public bool IsPullRequest { get; set; }

    /// <summary>Repository full name (owner/repo).</summary>
    public string? Repository { get; set; }
}

/// <summary>
/// Draft content for a DRAFT_ISSUE project item.
/// </summary>
public class GitHubProjectDraftContent
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string? Body { get; set; }
}

/// <summary>
/// Represents a field definition in a GitHub Projects v2 project.
/// </summary>
public class GitHubProjectField
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("dataType")]
    public string DataType { get; set; } = string.Empty; // TEXT, NUMBER, DATE, SINGLE_SELECT, ITERATION

    /// <summary>
    /// Options available for SINGLE_SELECT fields (e.g., Status column options).
    /// </summary>
    public List<GitHubProjectSelectOption> Options { get; set; } = new();

    /// <summary>
    /// Iterations for ITERATION fields.
    /// </summary>
    public List<GitHubProjectIteration> Iterations { get; set; } = new();
}

/// <summary>
/// An option in a single-select field.
/// </summary>
public class GitHubProjectSelectOption
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("color")]
    public string? Color { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>
/// An iteration in an iteration field.
/// </summary>
public class GitHubProjectIteration
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("startDate")]
    public string? StartDate { get; set; }

    [JsonPropertyName("duration")]
    public int? Duration { get; set; }
}

/// <summary>
/// A field value set on a project item.
/// </summary>
public class GitHubProjectFieldValue
{
    /// <summary>The field this value belongs to.</summary>
    public string FieldId { get; set; } = string.Empty;

    /// <summary>The field name for display.</summary>
    public string FieldName { get; set; } = string.Empty;

    /// <summary>The field data type.</summary>
    public string DataType { get; set; } = string.Empty;

    /// <summary>Text value (for TEXT fields).</summary>
    public string? TextValue { get; set; }

    /// <summary>Number value (for NUMBER fields).</summary>
    public double? NumberValue { get; set; }

    /// <summary>Date value (for DATE fields).</summary>
    public DateTime? DateValue { get; set; }

    /// <summary>Selected option name (for SINGLE_SELECT fields like Status).</summary>
    public string? SingleSelectValue { get; set; }

    /// <summary>Selected option ID (for mutations).</summary>
    public string? SingleSelectOptionId { get; set; }

    /// <summary>Iteration title (for ITERATION fields).</summary>
    public string? IterationValue { get; set; }

    /// <summary>Iteration ID (for mutations).</summary>
    public string? IterationId { get; set; }
}

/// <summary>
/// Represents a saved view in a GitHub Projects v2 project (Board, Table, etc.).
/// </summary>
public class GitHubProjectView
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("layout")]
    public string Layout { get; set; } = string.Empty; // BOARD_LAYOUT, TABLE_LAYOUT, ROADMAP_LAYOUT
}

// ──────────────────────────── Issue Detail (REST v3) ────────────────────────────

/// <summary>
/// Detailed issue data from the GitHub REST v3 API.
/// </summary>
public class GitHubIssueDetail
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("node_id")]
    public string NodeId { get; set; } = string.Empty;

    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = "open"; // "open" or "closed"

    [JsonPropertyName("locked")]
    public bool Locked { get; set; }

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("milestone")]
    public GitHubMilestone? Milestone { get; set; }

    [JsonPropertyName("assignees")]
    public List<GitHubUser> Assignees { get; set; } = new();

    [JsonPropertyName("labels")]
    public List<GitHubLabelDetail> Labels { get; set; } = new();

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("closed_at")]
    public DateTime? ClosedAt { get; set; }

    [JsonPropertyName("user")]
    public GitHubUser? User { get; set; }

    [JsonPropertyName("pull_request")]
    public GitHubPullRequestRef? PullRequest { get; set; }
}

/// <summary>
/// A GitHub user from the REST API.
/// </summary>
public class GitHubUser
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("login")]
    public string Login { get; set; } = string.Empty;

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }
}

/// <summary>
/// A GitHub milestone from the REST API.
/// </summary>
public class GitHubMilestone
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("node_id")]
    public string NodeId { get; set; } = string.Empty;

    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = "open";

    [JsonPropertyName("due_on")]
    public DateTime? DueOn { get; set; }
}

/// <summary>
/// A GitHub label with full detail from the REST API.
/// </summary>
public class GitHubLabelDetail
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("node_id")]
    public string NodeId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("color")]
    public string Color { get; set; } = "ccc";

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>
/// Pull request reference on an issue (when the issue has a linked PR).
/// </summary>
public class GitHubPullRequestRef
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("merged_at")]
    public DateTime? MergedAt { get; set; }
}

/// <summary>
/// A comment on an issue from the GitHub REST v3 API.
/// </summary>
public class GitHubComment
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("node_id")]
    public string NodeId { get; set; } = string.Empty;

    [JsonPropertyName("user")]
    public GitHubUser? User { get; set; }

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
