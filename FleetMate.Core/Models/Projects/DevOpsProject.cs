namespace FleetMate.Core.Models.Projects;

/// <summary>
/// Azure DevOps project from the Projects API.
/// </summary>
public class DevOpsProject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? State { get; set; }
}

/// <summary>
/// Response wrapper for the Projects API.
/// </summary>
public class DevOpsProjectsResponse
{
    public int Count { get; set; }
    public List<DevOpsProject> Value { get; set; } = new();
}
