using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Projects;
using Serilog;

namespace FleetMate.Core.Services.Projects.Tasks;

/// <summary>
/// Service to sync tasks one-way to Microsoft Planner.
/// Uses MS Graph API to push UnifiedTask items as Planner tasks.
/// </summary>
public class PlannerSyncService : IDisposable
{
    private readonly HttpClient _client;
    private readonly PlannerSyncConfig _config;
    private readonly JsonSerializerOptions _jsonOptions;
    private string? _accessToken;
    
    public bool IsEnabled => _config.Enabled && !string.IsNullOrEmpty(_config.PlanId);

    public PlannerSyncService(FleetMateConfig config)
    {
        _config = config.Tasks?.PlannerSync ?? new PlannerSyncConfig();
        
        _client = new HttpClient
        {
            BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// Authenticate using Azure CLI SSO token for MS Graph.
    /// </summary>
    public async Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Get token from Azure CLI for MS Graph
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "az",
                Arguments = "account get-access-token --resource https://graph.microsoft.com --query accessToken -o tsv",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null) return false;

            var token = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(token))
            {
                Log.Warning("Planner: Could not get Graph token from Azure CLI");
                return false;
            }

            _accessToken = token.Trim();
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            
            Log.Information("Planner: Authenticated via Azure CLI");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Planner: Authentication failed");
            return false;
        }
    }

    /// <summary>
    /// Get all tasks in the configured Planner plan.
    /// </summary>
    public async Task<List<PlannerTask>> GetPlannerTasksAsync(CancellationToken cancellationToken = default)
    {
        var url = $"planner/plans/{_config.PlanId}/tasks";
        var allTasks = new List<PlannerTask>();
        
        try
        {
            while (!string.IsNullOrEmpty(url))
            {
                var response = await _client.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(cancellationToken);
                    Log.Warning("Planner: Failed to get tasks: {Error}", error);
                    break;
                }
                
                var result = await response.Content.ReadFromJsonAsync<ODataResponse<PlannerTask>>(_jsonOptions, cancellationToken);
                if (result?.Value != null)
                {
                    allTasks.AddRange(result.Value);
                }
                
                url = result?.NextLink ?? "";
            }
            
            return allTasks;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Planner: Failed to get tasks");
            return allTasks;
        }
    }

    /// <summary>
    /// Create a new task in the Planner plan.
    /// </summary>
    public async Task<PlannerTask?> CreateTaskAsync(UnifiedTask task, CancellationToken cancellationToken = default)
    {
        var plannerTask = new
        {
            planId = _config.PlanId,
            bucketId = await GetOrCreateBucketAsync(task.Bucket ?? "Tasks", cancellationToken),
            title = task.Title,
            percentComplete = task.State == TaskState.Closed ? 100 : (task.State == TaskState.InProgress ? 50 : 0),
            dueDateTime = task.DueDate?.ToString("o"),
            priority = MapPriority(task.Priority),
            assignments = task.Assignees?.ToDictionary(
                a => a, 
                _ => new { }),
            appliedCategories = MapLabelsToCategories(task.Labels)
        };
        
        try
        {
            var content = new StringContent(
                JsonSerializer.Serialize(plannerTask, _jsonOptions), 
                Encoding.UTF8, 
                "application/json"
            );
            
            var response = await _client.PostAsync("planner/tasks", content, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                Log.Warning("Planner: Failed to create task '{Title}': {Error}", task.Title, error);
                return null;
            }
            
            var created = await response.Content.ReadFromJsonAsync<PlannerTask>(_jsonOptions, cancellationToken);
            
            // Add description in task details
            if (!string.IsNullOrEmpty(task.Description) && created != null)
            {
                await UpdateTaskDetailsAsync(created.Id, task.Description, task, cancellationToken);
            }
            
            Log.Information("Planner: Created task '{Title}'", task.Title);
            return created;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Planner: Failed to create task '{Title}'", task.Title);
            return null;
        }
    }

    /// <summary>
    /// Update an existing Planner task.
    /// </summary>
    public async Task<bool> UpdateTaskAsync(string plannerTaskId, UnifiedTask task, string etag, CancellationToken cancellationToken = default)
    {
        var plannerTask = new Dictionary<string, object?>
        {
            ["title"] = task.Title,
            ["percentComplete"] = task.State == TaskState.Closed ? 100 : (task.State == TaskState.InProgress ? 50 : 0)
        };
        
        if (task.DueDate.HasValue)
            plannerTask["dueDateTime"] = task.DueDate.Value.ToString("o");
        if (task.Priority != null)
            plannerTask["priority"] = MapPriority(task.Priority);
        if (task.Labels != null)
            plannerTask["appliedCategories"] = MapLabelsToCategories(task.Labels);
        
        try
        {
            var request = new HttpRequestMessage(new HttpMethod("PATCH"), $"planner/tasks/{plannerTaskId}")
            {
                Content = new StringContent(JsonSerializer.Serialize(plannerTask, _jsonOptions), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("If-Match", etag);
            
            var response = await _client.SendAsync(request, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                Log.Warning("Planner: Failed to update task '{Id}': {Error}", plannerTaskId, error);
                return false;
            }
            
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Planner: Failed to update task '{Id}'", plannerTaskId);
            return false;
        }
    }

    /// <summary>
    /// Sync all tasks from unified providers to Planner.
    /// </summary>
    public async Task<SyncResult> SyncTasksAsync(IEnumerable<UnifiedTask> tasks, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return new SyncResult { Success = false, Message = "Planner sync not enabled" };
        }
        
        var result = new SyncResult();
        var existingTasks = await GetPlannerTasksAsync(cancellationToken);
        var existingByTitle = existingTasks.ToDictionary(t => t.Title, t => t);
        
        foreach (var task in tasks)
        {
            try
            {
                // Simple matching by title - a real implementation would use external references
                if (existingByTitle.TryGetValue(task.Title, out var existing))
                {
                    // Task exists - update if needed
                    if (await UpdateTaskAsync(existing.Id, task, existing.ETag ?? "", cancellationToken))
                    {
                        result.Updated++;
                    }
                }
                else
                {
                    // Create new task
                    if (await CreateTaskAsync(task, cancellationToken) != null)
                    {
                        result.Created++;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Planner: Error syncing task '{Title}'", task.Title);
                result.Errors++;
            }
        }
        
        result.Success = result.Errors == 0;
        result.Message = $"Synced to Planner: {result.Created} created, {result.Updated} updated, {result.Errors} errors";
        Log.Information(result.Message);
        
        return result;
    }

    private async Task UpdateTaskDetailsAsync(string taskId, string description, UnifiedTask task, CancellationToken cancellationToken)
    {
        try
        {
            // First get the details to get the etag
            var detailsResponse = await _client.GetAsync($"planner/tasks/{taskId}/details", cancellationToken);
            if (!detailsResponse.IsSuccessStatusCode) return;
            
            var etag = detailsResponse.Headers.ETag?.Tag ?? "";
            
            var reference = $"Source: [{task.Provider}:{task.Id}]({task.ExternalUrl})";
            var fullDescription = $"{description}\n\n---\n{reference}";
            
            var body = new { description = fullDescription };
            
            var request = new HttpRequestMessage(new HttpMethod("PATCH"), $"planner/tasks/{taskId}/details")
            {
                Content = new StringContent(JsonSerializer.Serialize(body, _jsonOptions), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("If-Match", etag);
            
            await _client.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Planner: Could not update task details");
        }
    }

    private async Task<string?> GetOrCreateBucketAsync(string bucketName, CancellationToken cancellationToken)
    {
        try
        {
            // Get existing buckets
            var response = await _client.GetAsync($"planner/plans/{_config.PlanId}/buckets", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var buckets = await response.Content.ReadFromJsonAsync<ODataResponse<PlannerBucket>>(_jsonOptions, cancellationToken);
                var existing = buckets?.Value?.FirstOrDefault(b => b.Name == bucketName);
                if (existing != null)
                {
                    return existing.Id;
                }
            }
            
            // Create new bucket
            var newBucket = new { planId = _config.PlanId, name = bucketName };
            var createResponse = await _client.PostAsJsonAsync("planner/buckets", newBucket, cancellationToken);
            
            if (createResponse.IsSuccessStatusCode)
            {
                var created = await createResponse.Content.ReadFromJsonAsync<PlannerBucket>(_jsonOptions, cancellationToken);
                return created?.Id;
            }
            
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static int MapPriority(int? priority)
    {
        // Map from UnifiedTask priority (1-4 where 1=highest) to Planner priority (1-9)
        return priority switch
        {
            1 => 1,  // Urgent
            2 => 3,  // Important
            3 => 5,  // Medium
            4 => 7,  // Low
            _ => 5   // Default to Medium
        };
    }

    private static Dictionary<string, bool> MapLabelsToCategories(List<string>? labels)
    {
        // Map first 6 labels to Planner categories (category1-category6)
        var categories = new Dictionary<string, bool>();
        if (labels == null) return categories;
        
        for (int i = 0; i < Math.Min(labels.Count, 6); i++)
        {
            categories[$"category{i + 1}"] = true;
        }
        return categories;
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}

// Planner API Models
public class PlannerTask
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? PlanId { get; set; }
    public string? BucketId { get; set; }
    public int PercentComplete { get; set; }
    public int Priority { get; set; }
    public DateTime? DueDateTime { get; set; }
    [JsonPropertyName("@odata.etag")]
    public string? ETag { get; set; }
}

public class PlannerBucket
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public class ODataResponse<T>
{
    public List<T>? Value { get; set; }
    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }
}

public class SyncResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Deleted { get; set; }
    public int Errors { get; set; }
}
