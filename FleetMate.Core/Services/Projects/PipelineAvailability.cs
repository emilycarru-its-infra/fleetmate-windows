namespace FleetMate.Core.Services.Projects;

/// <summary>
/// Wording for the Pipelines summary line. A refresh where one project failed
/// still shows that project's cached runs and every other project's fresh ones,
/// so it names what failed rather than calling the whole provider unavailable.
/// </summary>
public static class PipelineAvailability
{
    /// <summary>
    /// The Azure DevOps part of the summary, or null when every call succeeded.
    /// <paramref name="failedProjects"/> are the distinct projects whose run query
    /// failed, out of <paramref name="queriedProjects"/> queried.
    /// </summary>
    public static string? AzureDevOps(bool aborted, bool directoryFailed, int failedProjects, int queriedProjects)
    {
        if (aborted || (queriedProjects > 0 && failedProjects >= queriedProjects) || (queriedProjects == 0 && directoryFailed))
            return "Azure DevOps unavailable";
        if (failedProjects > 0)
            return $"{Count(failedProjects, "project")} unavailable";
        return directoryFailed ? "Azure DevOps project list unavailable" : null;
    }

    /// <summary>"1 run", "2 runs".</summary>
    public static string Count(int n, string singular, string? plural = null) =>
        $"{n} {(n == 1 ? singular : plural ?? singular + "s")}";
}
