using System.Net.Http.Json;
using System.Text.Json;
using FleetMate.Core.Models.Projects;
using Serilog;

namespace FleetMate.Core.Services.Projects;

/// <summary>Single items by id, for fleetmate: links to things not already loaded.</summary>
public partial class AzureDevOpsService
{
    /// <summary>One pull request by repository and id.</summary>
    public async Task<UnifiedPullRequest?> GetPullRequestAsync(string project, string repository, int pullRequestId)
    {
        if (!await SetAuthorizationAsync()) return null;
        try
        {
            var path = $"{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repository)}" +
                       $"/pullrequests/{pullRequestId}?api-version=7.0";
            var response = await _client.GetAsync(path);
            if (!response.IsSuccessStatusCode) return null;
            var pr = await response.Content.ReadFromJsonAsync<GitPullRequest>(_jsonOptions);
            return pr == null ? null : MapPullRequest(pr, project, PullRequestRelation.Organization, OrgUrl);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[azdo] pull request {Repo}!{Id} unavailable", repository, pullRequestId);
            return null;
        }
    }

    /// <summary>One pipeline run (build) by id.</summary>
    public async Task<PipelineRun?> GetPipelineRunAsync(string project, long buildId)
    {
        if (!await SetAuthorizationAsync()) return null;
        try
        {
            var build = await GetJsonAsync($"{Uri.EscapeDataString(project)}/_apis/build/builds/{buildId}?api-version=7.0");
            return ParseSingleBuild(build, project);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[azdo] run {Project}#{Id} unavailable", project, buildId);
            return null;
        }
    }

    /// <summary>A pipeline definition's most recent run.</summary>
    public async Task<PipelineRun?> GetLatestPipelineRunAsync(string project, int definitionId)
    {
        if (!await SetAuthorizationAsync()) return null;
        try
        {
            var json = await GetJsonAsync($"{Uri.EscapeDataString(project)}/_apis/build/builds?definitions={definitionId}" +
                                          "&$top=1&queryOrder=queueTimeDescending&api-version=7.0");
            return ParseBuilds(json, project, Root).FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[azdo] latest run of definition {Project}/{Id} unavailable", project, definitionId);
            return null;
        }
    }

    private PipelineRun? ParseSingleBuild(JsonElement build, string project)
    {
        using var wrapped = JsonDocument.Parse($"{{\"value\":[{build.GetRawText()}]}}");
        return ParseBuilds(wrapped.RootElement, project, Root).FirstOrDefault();
    }
}
