using System.Text.Json;

namespace FleetMate.Core.Services.Projects;

/// <summary>One Git repository as the organization-wide listing reports it, with its clone URLs.</summary>
public sealed record DevOpsGitRepository(
    string Id, string Name, string Project, string? RemoteUrl, string? SshUrl, string? WebUrl,
    string? DefaultBranch, bool IsDisabled);

public partial class AzureDevOpsService
{
    /// <summary>The organization this service talks to.</summary>
    public string? Organization => _config.Organization;

    /// <summary>
    /// Every Git repository in the organization, across all projects, with its
    /// clone URLs — one request. Unlike <see cref="GetRepositoriesAsync"/>, a
    /// failure throws, so the repository catalog can report it instead of
    /// showing an empty list as if it were the truth.
    /// </summary>
    public async Task<List<DevOpsGitRepository>> ListGitRepositoriesAsync()
    {
        if (!await SetAuthorizationAsync())
            throw new InvalidOperationException("not signed in to Azure DevOps");
        var json = await GetJsonAsync("_apis/git/repositories?api-version=7.0");
        return ParseGitRepositories(json);
    }

    internal static List<DevOpsGitRepository> ParseGitRepositories(JsonElement json) =>
        Values(json)
            .Select(r =>
            {
                var project = r.TryGetProperty("project", out var p) ? p : default;
                return new DevOpsGitRepository(
                    Str(r, "id") ?? "",
                    Str(r, "name") ?? "",
                    Str(project, "name") ?? "",
                    Str(r, "remoteUrl"),
                    Str(r, "sshUrl"),
                    Str(r, "webUrl"),
                    Str(r, "defaultBranch"),
                    r.TryGetProperty("isDisabled", out var d) && d.ValueKind == JsonValueKind.True);
            })
            .Where(r => r.Name.Length > 0)
            .ToList();
}
