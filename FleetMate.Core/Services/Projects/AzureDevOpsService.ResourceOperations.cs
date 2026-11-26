using System.Text;
using FleetMate.Core.Config;

namespace FleetMate.Core.Services.Projects;

public partial class AzureDevOpsService
{
    public string? ConfiguredProject => _config.Project;

    /// <summary>Perform a resource operation with FleetMate's Azure DevOps sign-in.</summary>
    public async Task<byte[]> RequestResourceAsync(
        string method, string path, string? project, bool organizationScope, bool releases,
        string? body = null, string contentType = "application/json")
    {
        if (!path.StartsWith("_apis/", StringComparison.Ordinal))
            throw new ArgumentException("A relative Azure DevOps API path is required.", nameof(path));

        if (!await SetAuthorizationAsync())
            throw new InvalidOperationException("Not authenticated to Azure DevOps.");

        var root = _client.BaseAddress!;
        if (releases && root.Host == new Uri(AzureDevOpsConfig.DefaultHostUrl).Host)
            root = new UriBuilder(root) { Host = "vsrm." + root.Host }.Uri;

        var prefix = organizationScope ? "" : $"{Uri.EscapeDataString(project ?? "")}/";
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(root, prefix + path));
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("X-TFS-FedAuthRedirect", "Suppress");
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, contentType);

        using var response = await _client.SendAsync(request, _operationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = await response.Content.ReadAsStringAsync(_operationToken);
            throw new HttpRequestException(
                $"Azure DevOps API error ({(int)response.StatusCode}): {Truncate(message)}", null, response.StatusCode);
        }

        return await response.Content.ReadAsByteArrayAsync(_operationToken);
    }

    public string ResourcePullRequestUrl(string project, string repository, int id) =>
        PullRequestWebUrl(_config.BaseUrl, project, repository, id);
}
