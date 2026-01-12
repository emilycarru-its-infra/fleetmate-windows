using System.Text.Json.Serialization;

namespace FleetMate.Models.AzureDevOps;

/// <summary>
/// Sprint/Iteration in Azure DevOps
/// </summary>
public class Sprint
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public SprintAttributes? Attributes { get; set; }
    public string Url { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsCurrent => Attributes?.TimeFrame == "current";
    [JsonIgnore]
    public DateTime? StartDate => Attributes?.StartDate;
    [JsonIgnore]
    public DateTime? FinishDate => Attributes?.FinishDate;
}

/// <summary>
/// Sprint attributes
/// </summary>
public class SprintAttributes
{
    public DateTime? StartDate { get; set; }
    public DateTime? FinishDate { get; set; }
    public string? TimeFrame { get; set; } // "past", "current", "future"
}

/// <summary>
/// Response from iterations endpoint
/// </summary>
public class IterationsResponse
{
    public int Count { get; set; }
    public List<Sprint> Value { get; set; } = new();
}

/// <summary>
/// Board in Azure DevOps
/// </summary>
public class Board
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// Response from boards endpoint
/// </summary>
public class BoardsResponse
{
    public int Count { get; set; }
    public List<Board> Value { get; set; } = new();
}

/// <summary>
/// Board column definition
/// </summary>
public class BoardColumn
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ItemLimit { get; set; }
    public string? ColumnType { get; set; }
    public bool? IsSplit { get; set; }
    public Dictionary<string, string>? StateMappings { get; set; }
}

/// <summary>
/// Response from board columns endpoint
/// </summary>
public class BoardColumnsResponse
{
    public int Count { get; set; }
    public List<BoardColumn> Value { get; set; } = new();
}

/// <summary>
/// Team in Azure DevOps project
/// </summary>
public class Team
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Url { get; set; } = string.Empty;
    public IdentityRef? Identity { get; set; }
}

/// <summary>
/// Response from teams endpoint
/// </summary>
public class TeamsResponse
{
    public int Count { get; set; }
    public List<Team> Value { get; set; } = new();
}
