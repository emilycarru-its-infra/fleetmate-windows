using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using FleetMate.Core.Config;
using FleetMate.Core.Services.Projects;

namespace FleetMate.Core.Services.Repos;

/// <summary>
/// Lists every repository the signed-in user can see: all projects of the
/// configured Azure DevOps organization, and on GitHub the user's own
/// repositories, those of every organization they belong to, plus any extra
/// owners configured.
///
/// Azure DevOps reuses <see cref="AzureDevOpsService"/> and its signed-in
/// token. GitHub takes its token from <see cref="GitHubTokenSource"/> without
/// the interactive device flow.
/// </summary>
public sealed class RepoCatalogService
{
    private readonly Func<Task<List<DevOpsGitRepository>>>? _azureDevOps;
    private readonly string? _azureDevOpsOrganization;
    private readonly Func<Task<string?>>? _gitHubToken;
    private readonly IReadOnlyList<string> _gitHubOwners;
    private readonly HttpMessageHandler? _handler;

    /// <param name="azureDevOps">lists the organization's repositories, or null to skip Azure DevOps.</param>
    /// <param name="azureDevOpsOrganization">the organization name, used in repository keys.</param>
    /// <param name="gitHubToken">token source, or null to skip GitHub.</param>
    /// <param name="gitHubOwners">owners to list beyond the user's own memberships.</param>
    public RepoCatalogService(
        Func<Task<List<DevOpsGitRepository>>>? azureDevOps,
        string? azureDevOpsOrganization,
        Func<Task<string?>>? gitHubToken,
        IReadOnlyList<string>? gitHubOwners = null,
        HttpMessageHandler? handler = null)
    {
        _azureDevOps = azureDevOps;
        _azureDevOpsOrganization = azureDevOpsOrganization;
        _gitHubToken = gitHubToken;
        _gitHubOwners = gitHubOwners ?? Array.Empty<string>();
        _handler = handler;
    }

    /// <summary>
    /// The catalog service for this machine's configuration. <paramref name="provider"/>
    /// limits it to <c>azdo</c> or <c>github</c>.
    /// </summary>
    public static RepoCatalogService FromConfig(FleetMateConfig config, RepoSettings settings, RepoProvider? provider = null)
    {
        Func<Task<List<DevOpsGitRepository>>>? azure = null;
        var org = config.AzureDevOps?.Organization;
        if (provider is null or RepoProvider.AzureDevOps && !string.IsNullOrWhiteSpace(org))
        {
            var devops = config.AzureDevOps!;
            azure = async () =>
            {
                using var service = new AzureDevOpsService(devops);
                return await service.ListGitRepositoriesAsync();
            };
        }

        Func<Task<string?>>? token = null;
        var gh = config.Tasks?.Providers?.GitHub;
        if (provider is null or RepoProvider.GitHub)
        {
            var ghConfig = gh ?? new GitHubProviderConfig();
            token = async () =>
            {
                using var source = new GitHubTokenSource(ghConfig);
                return await source.GetTokenAsync();
            };
        }

        var owners = settings.GitHubOwners
            .Concat(new[] { gh?.Owner, gh?.Organization }.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new RepoCatalogService(azure, org, token, owners);
    }

    /// <summary>
    /// Fetches both providers concurrently. A provider that fails contributes
    /// an error message, never an exception.
    /// </summary>
    public async Task<RepoCatalog> FetchAsync()
    {
        var azure = FetchAzureDevOpsAsync();
        var github = FetchGitHubAsync();
        var (a, g) = (await azure, await github);
        var seen = new HashSet<string>();
        return new RepoCatalog
        {
            // The same repository can arrive through two owners; keep one.
            Repos = a.Repos.Concat(g.Repos).Where(r => seen.Add(r.Id)).OrderBy(r => r.Id, StringComparer.Ordinal).ToList(),
            Errors = a.Errors.Concat(g.Errors).ToList(),
            FetchedAt = DateTimeOffset.Now,
        };
    }

    // ── Azure DevOps ────────────────────────────────────────────────────

    internal async Task<(List<CatalogRepo> Repos, List<string> Errors)> FetchAzureDevOpsAsync()
    {
        if (_azureDevOps == null) return (new(), new());
        if (string.IsNullOrWhiteSpace(_azureDevOpsOrganization))
            return (new(), new() { "Azure DevOps: no organization configured" });
        try
        {
            var list = await _azureDevOps();
            return (list.Select(r => CatalogRepoFrom(r, _azureDevOpsOrganization!)).OfType<CatalogRepo>().ToList(), new());
        }
        catch (Exception ex)
        {
            return (new(), new() { $"Azure DevOps: {ex.Message}" });
        }
    }

    internal static CatalogRepo? CatalogRepoFrom(DevOpsGitRepository repo, string organization)
    {
        if (repo.IsDisabled) return null;
        // Prefer the key the clone URL itself parses to, so a catalog entry and
        // a checkout of it always share an id.
        var parsed = RepoRemoteUrl.Parse(repo.RemoteUrl);
        var key = parsed?.Provider == RepoProvider.AzureDevOps
            ? parsed
            : new RepoKey(RepoProvider.AzureDevOps, organization, repo.Project, repo.Name);
        var cloneUrl = repo.RemoteUrl ?? repo.WebUrl;
        if (cloneUrl == null) return null;
        return new CatalogRepo
        {
            Key = key,
            CloneUrl = StripUser(cloneUrl),
            SshUrl = repo.SshUrl,
            WebUrl = repo.WebUrl,
            DefaultBranch = repo.DefaultBranch,
        };
    }

    /// <summary>
    /// Azure DevOps returns <c>https://&lt;org&gt;@host/...</c>; the user part
    /// would pin git's credential lookup to the org name, so drop it.
    /// </summary>
    internal static string StripUser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.UserInfo.Length == 0) return url;
        return new UriBuilder(uri) { UserName = "", Password = "" }.Uri.AbsoluteUri;
    }

    // ── GitHub ──────────────────────────────────────────────────────────

    internal sealed class GitHubRepo
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("owner")] public GitHubOwner Owner { get; set; } = new();
        [JsonPropertyName("clone_url")] public string CloneUrl { get; set; } = "";
        [JsonPropertyName("ssh_url")] public string? SshUrl { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("default_branch")] public string? DefaultBranch { get; set; }
        [JsonPropertyName("archived")] public bool? Archived { get; set; }
        [JsonPropertyName("fork")] public bool? Fork { get; set; }
        [JsonPropertyName("private")] public bool? Private { get; set; }
    }

    internal sealed class GitHubOwner
    {
        [JsonPropertyName("login")] public string Login { get; set; } = "";
    }

    internal async Task<(List<CatalogRepo> Repos, List<string> Errors)> FetchGitHubAsync()
    {
        if (_gitHubToken == null) return (new(), new());
        string? token;
        try { token = await _gitHubToken(); }
        catch (Exception ex) { return (new(), new() { $"GitHub: {ex.Message}" }); }
        if (string.IsNullOrEmpty(token)) return (new(), new() { "GitHub: no token (run 'gh auth login')" });

        using var http = _handler != null ? new HttpClient(_handler, disposeHandler: false) : new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FleetMate");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        var repos = new List<GitHubRepo>();
        var errors = new List<string>();
        try
        {
            repos.AddRange(await PagedAsync(http, "https://api.github.com/user/repos?per_page=100&affiliation=owner,collaborator,organization_member"));
        }
        catch (Exception ex)
        {
            errors.Add($"GitHub: {ex.Message}");
        }

        var covered = repos.Select(r => r.Owner.Login).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var owner in _gitHubOwners.Where(o => !covered.Contains(o)))
        {
            var encoded = Uri.EscapeDataString(owner);
            try
            {
                repos.AddRange(await PagedAsync(http, $"https://api.github.com/orgs/{encoded}/repos?per_page=100&type=all"));
            }
            catch
            {
                // Not an organization: try it as a user.
                try
                {
                    repos.AddRange(await PagedAsync(http, $"https://api.github.com/users/{encoded}/repos?per_page=100"));
                }
                catch (Exception ex)
                {
                    errors.Add($"GitHub {owner}: {ex.Message}");
                }
            }
        }

        var catalog = repos.Where(r => r.Name.Length > 0 && r.Owner.Login.Length > 0).Select(r => new CatalogRepo
        {
            Key = new RepoKey(RepoProvider.GitHub, r.Owner.Login, null, r.Name),
            CloneUrl = r.CloneUrl,
            SshUrl = r.SshUrl,
            WebUrl = r.HtmlUrl,
            DefaultBranch = r.DefaultBranch,
            IsArchived = r.Archived ?? false,
            IsFork = r.Fork ?? false,
            IsPrivate = r.Private,
        }).ToList();
        return (catalog, errors);
    }

    private static async Task<List<GitHubRepo>> PagedAsync(HttpClient http, string first)
    {
        var all = new List<GitHubRepo>();
        Uri? next = new(first);
        for (var pages = 0; next != null && pages < 50; pages++)
        {
            using var response = await http.GetAsync(next);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode} for {next.AbsolutePath}");
            var page = JsonSerializer.Deserialize<List<GitHubRepo>>(await response.Content.ReadAsStringAsync());
            if (page != null) all.AddRange(page);
            next = NextLink(response.Headers.TryGetValues("Link", out var values) ? string.Join(",", values) : null);
        }
        return all;
    }

    /// <summary>The <c>rel="next"</c> URL of a GitHub <c>Link</c> header.</summary>
    internal static Uri? NextLink(string? header)
    {
        if (string.IsNullOrEmpty(header)) return null;
        foreach (var part in header.Split(','))
        {
            var pieces = part.Split(';').Select(p => p.Trim()).ToList();
            if (pieces.Count < 2 || !pieces.Skip(1).Contains("rel=\"next\"")) continue;
            return Uri.TryCreate(pieces[0].Trim('<', '>'), UriKind.Absolute, out var uri) ? uri : null;
        }
        return null;
    }
}
