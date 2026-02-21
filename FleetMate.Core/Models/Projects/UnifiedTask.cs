using System.Text.Json;

namespace FleetMate.Core.Models.Projects;

/// <summary>
/// Represents a task from any provider (Azure DevOps, GitHub, Gitea) in a unified format.
/// </summary>
public class UnifiedTask
{
    /// <summary>Unique identifier within the provider.</summary>
    public string Id { get; set; } = string.Empty;
    
    /// <summary>Provider that owns this task (azdevops, github, gitea).</summary>
    public string Provider { get; set; } = string.Empty;
    
    /// <summary>Task title/summary.</summary>
    public string Title { get; set; } = string.Empty;
    
    /// <summary>Task description/body (markdown supported).</summary>
    public string? Description { get; set; }
    
    /// <summary>Current state of the task.</summary>
    public TaskState State { get; set; } = TaskState.Open;
    
    /// <summary>List of assignee usernames/emails.</summary>
    public List<string> Assignees { get; set; } = new();
    
    /// <summary>Labels/tags applied to the task.</summary>
    public List<string> Labels { get; set; } = new();
    
    /// <summary>Bucket/column/milestone the task belongs to.</summary>
    public string? Bucket { get; set; }
    
    /// <summary>Due date for the task.</summary>
    public DateTime? DueDate { get; set; }
    
    /// <summary>When the task was created.</summary>
    public DateTime CreatedAt { get; set; }
    
    /// <summary>When the task was last updated.</summary>
    public DateTime UpdatedAt { get; set; }
    
    /// <summary>When the task was closed (null if still open).</summary>
    public DateTime? ClosedAt { get; set; }
    
    /// <summary>URL to view the task in the provider's web UI.</summary>
    public string? ExternalUrl { get; set; }
    
    /// <summary>Provider-specific data stored as JSON for round-tripping.</summary>
    public JsonElement? ProviderData { get; set; }
    
    /// <summary>Priority level (1 = highest, 4 = lowest). Null if not set.</summary>
    public int? Priority { get; set; }
    
    /// <summary>
    /// Creates a composite key for this task across providers.
    /// </summary>
    public string CompositeKey => $"{Provider}:{Id}";
}

/// <summary>
/// Request to create a new task.
/// </summary>
public class CreateTaskRequest
{
    /// <summary>Task title (required).</summary>
    public required string Title { get; set; }
    
    /// <summary>Task description/body.</summary>
    public string? Description { get; set; }
    
    /// <summary>Initial state (defaults to Open).</summary>
    public TaskState State { get; set; } = TaskState.Open;
    
    /// <summary>Assignee usernames/emails.</summary>
    public List<string>? Assignees { get; set; }
    
    /// <summary>Labels to apply.</summary>
    public List<string>? Labels { get; set; }
    
    /// <summary>Bucket/column/milestone to place the task in.</summary>
    public string? Bucket { get; set; }
    
    /// <summary>Due date.</summary>
    public DateTime? DueDate { get; set; }
    
    /// <summary>Priority (1-4).</summary>
    public int? Priority { get; set; }
}

/// <summary>
/// Request to update an existing task. Null fields are not updated.
/// </summary>
public class UpdateTaskRequest
{
    /// <summary>New title (null = keep existing).</summary>
    public string? Title { get; set; }
    
    /// <summary>New description (null = keep existing).</summary>
    public string? Description { get; set; }
    
    /// <summary>New state (null = keep existing).</summary>
    public TaskState? State { get; set; }
    
    /// <summary>New assignees (null = keep existing, empty = clear).</summary>
    public List<string>? Assignees { get; set; }
    
    /// <summary>New labels (null = keep existing, empty = clear).</summary>
    public List<string>? Labels { get; set; }
    
    /// <summary>New bucket (null = keep existing).</summary>
    public string? Bucket { get; set; }
    
    /// <summary>New due date (null = keep existing).</summary>
    public DateTime? DueDate { get; set; }
    
    /// <summary>New priority (null = keep existing).</summary>
    public int? Priority { get; set; }
}

/// <summary>
/// Represents a bucket/column/milestone for organizing tasks.
/// </summary>
public class TaskBucket
{
    /// <summary>Unique identifier within the provider.</summary>
    public string Id { get; set; } = string.Empty;
    
    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>Order for display (lower = first).</summary>
    public int Order { get; set; }
}

/// <summary>
/// Represents a label/tag that can be applied to tasks.
/// </summary>
public class TaskLabel
{
    /// <summary>Label name/identifier.</summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>Color in hex format (e.g., "#FF0000").</summary>
    public string? Color { get; set; }
    
    /// <summary>Label description.</summary>
    public string? Description { get; set; }
}
