using FleetMate.Core.Models.Tasks;
using Serilog;

namespace FleetMate.Core.Services.Tasks;

/// <summary>
/// Aggregates tasks from multiple enabled providers.
/// Acts as the central point for unified task operations.
/// </summary>
public class TaskProviderRegistry
{
    private readonly Dictionary<string, ITaskProvider> _providers = new();
    private readonly ILogger _logger;
    
    public TaskProviderRegistry(ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
    }
    
    /// <summary>
    /// Registers a task provider.
    /// </summary>
    /// <param name="provider">The provider to register.</param>
    public void RegisterProvider(ITaskProvider provider)
    {
        _providers[provider.ProviderId] = provider;
        _logger.Debug("Registered task provider: {ProviderId} ({ProviderName})", 
            provider.ProviderId, provider.ProviderName);
    }
    
    /// <summary>
    /// Gets all registered providers.
    /// </summary>
    public IEnumerable<ITaskProvider> AllProviders => _providers.Values;
    
    /// <summary>
    /// Gets only enabled providers.
    /// </summary>
    public IEnumerable<ITaskProvider> EnabledProviders => 
        _providers.Values.Where(p => p.IsEnabled);
    
    /// <summary>
    /// Gets a provider by its ID.
    /// </summary>
    /// <param name="providerId">Provider identifier (e.g., "azdevops", "github").</param>
    /// <returns>The provider, or null if not found.</returns>
    public ITaskProvider? GetProvider(string providerId)
    {
        return _providers.GetValueOrDefault(providerId);
    }
    
    /// <summary>
    /// Authenticates all enabled providers.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dictionary of provider ID to authentication result.</returns>
    public async Task<Dictionary<string, bool>> AuthenticateAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, bool>();
        
        foreach (var provider in EnabledProviders)
        {
            try
            {
                results[provider.ProviderId] = await provider.AuthenticateAsync(cancellationToken);
                _logger.Information("Authenticated with {ProviderId}: {Result}", 
                    provider.ProviderId, results[provider.ProviderId]);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to authenticate with {ProviderId}", provider.ProviderId);
                results[provider.ProviderId] = false;
            }
        }
        
        return results;
    }
    
    /// <summary>
    /// Lists tasks from all enabled providers (or specific providers).
    /// </summary>
    /// <param name="filter">Optional filter criteria.</param>
    /// <param name="providerIds">Provider IDs to query. Null = all enabled providers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Aggregated list of tasks from all queried providers.</returns>
    public async Task<List<UnifiedTask>> ListTasksAsync(
        TaskFilter? filter = null, 
        IEnumerable<string>? providerIds = null,
        CancellationToken cancellationToken = default)
    {
        var providers = providerIds == null 
            ? EnabledProviders 
            : providerIds.Select(id => GetProvider(id)).Where(p => p != null && p.IsEnabled).Cast<ITaskProvider>();
        
        var allTasks = new List<UnifiedTask>();
        
        // Query providers in parallel
        var tasks = providers.Select(async provider =>
        {
            try
            {
                var providerTasks = await provider.ListTasksAsync(filter, cancellationToken);
                return providerTasks;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to list tasks from {ProviderId}", provider.ProviderId);
                return new List<UnifiedTask>();
            }
        });
        
        var results = await Task.WhenAll(tasks);
        
        foreach (var result in results)
        {
            allTasks.AddRange(result);
        }
        
        // Sort by updated date descending (most recent first)
        return allTasks.OrderByDescending(t => t.UpdatedAt).ToList();
    }
    
    /// <summary>
    /// Gets a task by its composite key (provider:id).
    /// </summary>
    /// <param name="compositeKey">Composite key in format "provider:id".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The task, or null if not found.</returns>
    public async Task<UnifiedTask?> GetTaskByCompositeKeyAsync(
        string compositeKey, 
        CancellationToken cancellationToken = default)
    {
        var parts = compositeKey.Split(':', 2);
        if (parts.Length != 2)
        {
            _logger.Warning("Invalid composite key format: {Key}", compositeKey);
            return null;
        }
        
        var providerId = parts[0];
        var taskId = parts[1];
        
        var provider = GetProvider(providerId);
        if (provider == null)
        {
            _logger.Warning("Provider not found: {ProviderId}", providerId);
            return null;
        }
        
        return await provider.GetTaskAsync(taskId, cancellationToken);
    }
    
    /// <summary>
    /// Creates a task in the specified provider.
    /// </summary>
    /// <param name="providerId">Provider to create the task in.</param>
    /// <param name="request">Task creation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created task.</returns>
    /// <exception cref="InvalidOperationException">If provider not found or not enabled.</exception>
    public async Task<UnifiedTask> CreateTaskAsync(
        string providerId,
        CreateTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        var provider = GetProvider(providerId);
        if (provider == null)
        {
            throw new InvalidOperationException($"Provider not found: {providerId}");
        }
        
        if (!provider.IsEnabled)
        {
            throw new InvalidOperationException($"Provider is not enabled: {providerId}");
        }
        
        return await provider.CreateTaskAsync(request, cancellationToken);
    }
    
    /// <summary>
    /// Updates a task by its composite key.
    /// </summary>
    /// <param name="compositeKey">Composite key in format "provider:id".</param>
    /// <param name="request">Update request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated task.</returns>
    public async Task<UnifiedTask> UpdateTaskByCompositeKeyAsync(
        string compositeKey,
        UpdateTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        var parts = compositeKey.Split(':', 2);
        if (parts.Length != 2)
        {
            throw new ArgumentException($"Invalid composite key format: {compositeKey}");
        }
        
        var providerId = parts[0];
        var taskId = parts[1];
        
        var provider = GetProvider(providerId) 
            ?? throw new InvalidOperationException($"Provider not found: {providerId}");
        
        return await provider.UpdateTaskAsync(taskId, request, cancellationToken);
    }
    
    /// <summary>
    /// Lists all buckets from all enabled providers.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dictionary of provider ID to list of buckets.</returns>
    public async Task<Dictionary<string, List<TaskBucket>>> ListAllBucketsAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, List<TaskBucket>>();
        
        foreach (var provider in EnabledProviders)
        {
            try
            {
                results[provider.ProviderId] = await provider.ListBucketsAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to list buckets from {ProviderId}", provider.ProviderId);
                results[provider.ProviderId] = new List<TaskBucket>();
            }
        }
        
        return results;
    }
    
    /// <summary>
    /// Lists all labels from all enabled providers.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dictionary of provider ID to list of labels.</returns>
    public async Task<Dictionary<string, List<TaskLabel>>> ListAllLabelsAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, List<TaskLabel>>();
        
        foreach (var provider in EnabledProviders)
        {
            try
            {
                results[provider.ProviderId] = await provider.ListLabelsAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to list labels from {ProviderId}", provider.ProviderId);
                results[provider.ProviderId] = new List<TaskLabel>();
            }
        }
        
        return results;
    }
}
