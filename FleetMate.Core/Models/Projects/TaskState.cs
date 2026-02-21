namespace FleetMate.Core.Models.Projects;

/// <summary>
/// Unified task state across all providers.
/// Maps to provider-specific states (e.g., Azure DevOps states, GitHub open/closed).
/// </summary>
public enum TaskState
{
    /// <summary>Task has not been started.</summary>
    Open,
    
    /// <summary>Task is actively being worked on.</summary>
    InProgress,
    
    /// <summary>Task has been completed or closed.</summary>
    Closed
}
