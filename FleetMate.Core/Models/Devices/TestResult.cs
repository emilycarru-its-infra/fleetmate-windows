namespace FleetMate.Core.Models.Devices;

/// <summary>
/// Result from running a quality test
/// </summary>
public class TestResult
{
    public string TestName { get; set; } = string.Empty;
    public string? PackageName { get; set; }
    public TestStatus Status { get; set; }
    public string? Message { get; set; }
    public TimeSpan Duration { get; set; }
    public List<TestIssue> Issues { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Status of a test run
/// </summary>
public enum TestStatus
{
    Passed,
    Failed,
    Skipped,
    Warning
}

/// <summary>
/// An issue found during testing
/// </summary>
public class TestIssue
{
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public int? LineNumber { get; set; }
    public IssueSeverity Severity { get; set; }
    public string? SuggestedFix { get; set; }
    public bool AutoFixable { get; set; }
}

public enum IssueSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

/// <summary>
/// Summary of a test run
/// </summary>
public class TestRunSummary
{
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int Warnings { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public List<TestResult> Results { get; set; } = new();
}
