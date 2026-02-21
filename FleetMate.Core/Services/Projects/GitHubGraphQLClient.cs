using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FleetMate.Core.Config;
using Serilog;

namespace FleetMate.Core.Services.Projects;

/// <summary>
/// Lightweight GitHub GraphQL API client.
/// Sends POST requests to https://api.github.com/graphql with query + variables.
/// Auth chain: config token → gh CLI → GITHUB_TOKEN/GH_TOKEN env → credential store → OAuth Device Flow.
/// </summary>
public class GitHubGraphQLClient : IDisposable
{
    private const string GraphQLEndpoint = "https://api.github.com/graphql";
    
    private readonly HttpClient _client;
    private readonly GitHubTokenSource _tokenSource;
    private readonly JsonSerializerOptions _jsonOptions;

    public GitHubGraphQLClient(
        GitHubProviderConfig config,
        Func<string, string, CancellationToken, Task>? deviceFlowPrompt = null)
    {
        _tokenSource = new GitHubTokenSource(config) { DeviceFlowPrompt = deviceFlowPrompt };
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.Add("User-Agent", "FleetMate");
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// Authenticates with GitHub by verifying the token.
    /// </summary>
    public async Task<bool> AuthenticateAsync(CancellationToken ct = default)
    {
        var token = await _tokenSource.GetTokenAsync(ct);
        if (string.IsNullOrEmpty(token))
        {
            Log.Warning("GitHub GraphQL: No token available");
            return false;
        }

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var result = await ExecuteAsync<JsonElement>("query { viewer { login } }", ct: ct);
            var login = result.GetProperty("viewer").GetProperty("login").GetString();
            Log.Information("GitHub GraphQL: Authenticated as {Login}", login);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "GitHub GraphQL: Authentication failed");
            return false;
        }
    }

    /// <summary>
    /// Executes a GraphQL query/mutation and returns the "data" portion of the response.
    /// Throws on GraphQL errors.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(string query, object? variables = null, CancellationToken ct = default)
    {
        await EnsureTokenAsync();

        var requestBody = new Dictionary<string, object?> { ["query"] = query };
        if (variables != null)
            requestBody["variables"] = variables;

        var json = JsonSerializer.Serialize(requestBody, _jsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _client.PostAsync(GraphQLEndpoint, content, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            Log.Error("GitHub GraphQL HTTP {Status}: {Body}", (int)response.StatusCode, responseBody);
            throw new InvalidOperationException($"GitHub GraphQL HTTP {(int)response.StatusCode}: {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        // Check for GraphQL errors
        if (root.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
        {
            var firstError = errors[0].GetProperty("message").GetString() ?? "Unknown GraphQL error";
            Log.Error("GitHub GraphQL error: {Error}", firstError);
            throw new InvalidOperationException($"GitHub GraphQL error: {firstError}");
        }

        if (!root.TryGetProperty("data", out var data))
        {
            throw new InvalidOperationException("GitHub GraphQL: No 'data' field in response");
        }

        // Re-serialize data portion and deserialize to target type
        var dataJson = data.GetRawText();
        return JsonSerializer.Deserialize<T>(dataJson, _jsonOptions)
            ?? throw new InvalidOperationException("GitHub GraphQL: Failed to deserialize response data");
    }

    /// <summary>
    /// Executes a GraphQL query and returns the raw JsonElement data.
    /// </summary>
    public async Task<JsonElement> ExecuteRawAsync(string query, object? variables = null, CancellationToken ct = default)
    {
        return await ExecuteAsync<JsonElement>(query, variables, ct);
    }

    // ──────────────────────────── REST v3 ────────────────────────────

    /// <summary>
    /// Executes a GitHub REST v3 API call and returns the raw response bytes.
    /// </summary>
    /// <param name="method">HTTP method (GET, POST, PATCH, PUT, DELETE).</param>
    /// <param name="path">Path relative to https://api.github.com (e.g. "/repos/owner/repo/issues/7").</param>
    /// <param name="body">Optional JSON body dictionary.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<byte[]> ExecuteRestAsync(
        string path,
        string method = "GET",
        Dictionary<string, object>? body = null,
        CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);

        var url = $"https://api.github.com{path}";
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        if (body != null)
        {
            var json = JsonSerializer.Serialize(body, _jsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        var response = await _client.SendAsync(request, ct);
        var responseBytes = await response.Content.ReadAsByteArrayAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = Encoding.UTF8.GetString(responseBytes);
            Log.Error("GitHub REST HTTP {Status}: {Body}", (int)response.StatusCode, responseBody);
            throw new HttpRequestException(
                $"GitHub REST HTTP {(int)response.StatusCode}: {responseBody}",
                null,
                response.StatusCode);
        }

        return responseBytes;
    }

    private async Task EnsureTokenAsync(CancellationToken ct = default)
    {
        if (_client.DefaultRequestHeaders.Authorization != null)
            return;

        var token = await _tokenSource.GetTokenAsync(ct);
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("GitHub GraphQL: No authentication token available");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public void Dispose()
    {
        _client.Dispose();
        _tokenSource.Dispose();
    }
}
