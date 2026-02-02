using FleetMate.Core.Models.Tasks;

namespace FleetMate.Core.Services.Tasks;

/// <summary>
/// Interface for task providers (Azure DevOps, GitHub, Gitea).
/// Abstracts provider-specific APIs behind a unified interface.
/// </summary>
public interface ITaskProvider
{
    /// <summary>
    /// Provider identifier (e.g., "azdevops", "github", "gitea").
    /// </summary>
    string ProviderId { get; }
    
    /// <summary>
    /// Human-readable provider name for display.
    /// </summary>
    string ProviderName { get; }
    
    /// <summary>
    /// Whether the provider is currently configured and enabled.
    /// </summary>
    bool IsEnabled { get; }
    
    /// <summary>
    /// Authenticates with the provider. Call before other operations.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if authenticated successfully.</returns>
    Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Lists all tasks matching the optional filter.
    /// </summary>
    /// <param name="filter">Optional filter criteria.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of unified tasks.</returns>
    Task<List<UnifiedTask>> ListTasksAsync(TaskFilter? filter = null, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets a single task by ID.
    /// </summary>
    /// <param name="taskId">Provider-specific task ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The task, or null if not found.</returns>
    Task<UnifiedTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Creates a new task.
    /// </summary>
    /// <param name="request">Task creation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created task.</returns>
    Task<UnifiedTask> CreateTaskAsync(CreateTaskRequest request, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Updates an existing task.
    /// </summary>
    /// <param name="taskId">Provider-specific task ID.</param>
    /// <param name="request">Update request (null fields are not updated).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated task.</returns>
    Task<UnifiedTask> UpdateTaskAsync(string taskId, UpdateTaskRequest request, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Deletes a task.
    /// </summary>
    /// <param name="taskId">Provider-specific task ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if deleted successfully.</returns>
    Task<bool> DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Lists available buckets/columns/milestones.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of buckets.</returns>
    Task<List<TaskBucket>> ListBucketsAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Lists available labels/tags.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of labels.</returns>
    Task<List<TaskLabel>> ListLabelsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Filter criteria for listing tasks.
/// </summary>
public class TaskFilter
{
    /// <summary>Filter by state(s). Null = all states.</summary>
    public List<TaskState>? States { get; set; }
    
    /// <summary>Filter by assignee(s). Null = all assignees.</summary>
    public List<string>? Assignees { get; set; }
    
    /// <summary>Filter by label(s). Null = all labels.</summary>
    public List<string>? Labels { get; set; }
    
    /// <summary>Filter by bucket. Null = all buckets.</summary>
    public string? Bucket { get; set; }
    
    /// <summary>Search text in title/description. Null = no text filter.</summary>
    public string? SearchText { get; set; }
    
    /// <summary>Maximum number of tasks to return. Null = provider default.</summary>
    public int? Limit { get; set; }
    
    /// <summary>Include closed tasks. Default is false (open tasks only).</summary>
    public bool IncludeClosed { get; set; } = false;
}
