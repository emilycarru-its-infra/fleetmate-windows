using System.Text;
using System.Text.RegularExpressions;
using FleetMate.Config;
using FleetMate.Core.Models.Tasks;
using Serilog;

namespace FleetMate.Core.Services.Tasks;

/// <summary>
/// Service to sync tasks bidirectionally with Markdown files.
/// Uses a simple markdown format for task representation.
/// </summary>
public class MarkdownSyncService
{
    private readonly MarkdownSyncConfig _config;
    
    public bool IsEnabled => _config.Enabled && !string.IsNullOrEmpty(_config.FilePath);

    public MarkdownSyncService(FleetMateConfig config)
    {
        _config = config.Tasks?.MarkdownSync ?? new MarkdownSyncConfig();
    }

    /// <summary>
    /// Write tasks to a markdown file.
    /// </summary>
    public async Task<SyncResult> WriteTasksAsync(IEnumerable<UnifiedTask> tasks, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return new SyncResult { Success = false, Message = "Markdown sync not enabled" };
        }
        
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# FleetMate Tasks");
            sb.AppendLine();
            sb.AppendLine($"_Last synced: {DateTime.Now:yyyy-MM-dd HH:mm:ss}_");
            sb.AppendLine();
            
            // Group by provider
            var grouped = tasks.GroupBy(t => t.Provider);
            
            foreach (var group in grouped.OrderBy(g => g.Key))
            {
                sb.AppendLine($"## {GetProviderDisplayName(group.Key)}");
                sb.AppendLine();
                
                // Group by bucket within provider
                var byBucket = group.GroupBy(t => t.Bucket ?? "Uncategorized");
                
                foreach (var bucket in byBucket.OrderBy(b => b.Key))
                {
                    sb.AppendLine($"### {bucket.Key}");
                    sb.AppendLine();
                    
                    foreach (var task in bucket.OrderBy(t => t.State).ThenBy(t => t.CreatedAt))
                    {
                        WriteTask(sb, task);
                    }
                    
                    sb.AppendLine();
                }
            }
            
            // Ensure directory exists
            var directory = Path.GetDirectoryName(_config.FilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            await File.WriteAllTextAsync(_config.FilePath!, sb.ToString(), cancellationToken);
            
            var count = tasks.Count();
            Log.Information("Markdown: Wrote {Count} tasks to {Path}", count, _config.FilePath);
            
            return new SyncResult 
            { 
                Success = true, 
                Created = count,
                Message = $"Wrote {count} tasks to {_config.FilePath}" 
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Markdown: Failed to write tasks");
            return new SyncResult { Success = false, Message = ex.Message, Errors = 1 };
        }
    }

    /// <summary>
    /// Read tasks from a markdown file.
    /// </summary>
    public async Task<List<UnifiedTask>> ReadTasksAsync(CancellationToken cancellationToken = default)
    {
        var tasks = new List<UnifiedTask>();
        
        if (!IsEnabled || !File.Exists(_config.FilePath))
        {
            return tasks;
        }
        
        try
        {
            var content = await File.ReadAllTextAsync(_config.FilePath!, cancellationToken);
            var lines = content.Split('\n');
            
            string? currentProvider = null;
            string? currentBucket = null;
            
            foreach (var line in lines)
            {
                var trimmed = line.TrimEnd('\r');
                
                // Provider header
                if (trimmed.StartsWith("## "))
                {
                    currentProvider = ParseProviderFromHeader(trimmed[3..]);
                    continue;
                }
                
                // Bucket header
                if (trimmed.StartsWith("### "))
                {
                    currentBucket = trimmed[4..].Trim();
                    continue;
                }
                
                // Task line
                if (trimmed.StartsWith("- ["))
                {
                    var task = ParseTask(trimmed, currentProvider, currentBucket);
                    if (task != null)
                    {
                        tasks.Add(task);
                    }
                }
            }
            
            Log.Information("Markdown: Read {Count} tasks from {Path}", tasks.Count, _config.FilePath);
            return tasks;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Markdown: Failed to read tasks");
            return tasks;
        }
    }

    /// <summary>
    /// Sync tasks bidirectionally - merge changes from both sources.
    /// </summary>
    public async Task<SyncResult> SyncBidirectionalAsync(
        List<UnifiedTask> providerTasks, 
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return new SyncResult { Success = false, Message = "Markdown sync not enabled" };
        }
        
        var result = new SyncResult();
        
        // Read existing tasks from markdown
        var markdownTasks = await ReadTasksAsync(cancellationToken);
        var markdownById = markdownTasks
            .Where(t => !string.IsNullOrEmpty(t.Id))
            .ToDictionary(t => $"{t.Provider}:{t.Id}", t => t);
        
        // Merge: provider tasks are authoritative for existing items
        // but markdown may have local-only tasks
        var mergedTasks = new List<UnifiedTask>(providerTasks);
        
        // Find tasks that exist only in markdown (local-only)
        foreach (var mdTask in markdownTasks)
        {
            var key = $"{mdTask.Provider}:{mdTask.Id}";
            if (!providerTasks.Any(p => $"{p.Provider}:{p.Id}" == key))
            {
                // This task only exists in markdown - keep it
                if (mdTask.Provider == "local")
                {
                    mergedTasks.Add(mdTask);
                    result.Created++;
                }
            }
        }
        
        // Write merged tasks back
        var writeResult = await WriteTasksAsync(mergedTasks, cancellationToken);
        result.Success = writeResult.Success;
        result.Message = $"Synced {mergedTasks.Count} tasks ({result.Created} local-only preserved)";
        
        return result;
    }

    private void WriteTask(StringBuilder sb, UnifiedTask task)
    {
        var checkbox = task.State switch
        {
            TaskState.Closed => "[x]",
            TaskState.InProgress => "[-]",
            _ => "[ ]"
        };
        
        var labels = task.Labels?.Count > 0 
            ? $" `{string.Join("` `", task.Labels)}`" 
            : "";
        
        var assignees = task.Assignees?.Count > 0 
            ? $" @{string.Join(" @", task.Assignees)}" 
            : "";
        
        var priority = task.Priority.HasValue 
            ? $" !{task.Priority}" 
            : "";
        
        var due = task.DueDate.HasValue 
            ? $" 📅 {task.DueDate.Value:yyyy-MM-dd}" 
            : "";
        
        var link = !string.IsNullOrEmpty(task.ExternalUrl) 
            ? $" [{task.Provider}#{task.Id}]({task.ExternalUrl})" 
            : $" [{task.Provider}#{task.Id}]";
        
        sb.AppendLine($"- {checkbox} **{EscapeMarkdown(task.Title)}**{labels}{priority}{due}{assignees}{link}");
        
        if (!string.IsNullOrEmpty(task.Description))
        {
            var desc = task.Description.Length > 200 
                ? task.Description[..200] + "..." 
                : task.Description;
            sb.AppendLine($"  > {EscapeMarkdown(desc.Replace("\n", " "))}");
        }
    }

    private UnifiedTask? ParseTask(string line, string? provider, string? bucket)
    {
        // Pattern: - [x] **Title** `label` !priority 📅 2024-01-01 @user [provider#id](url)
        var match = Regex.Match(line, @"^- \[(.)\] \*\*(.+?)\*\*(.*)$");
        if (!match.Success) return null;
        
        var checkChar = match.Groups[1].Value;
        var title = match.Groups[2].Value;
        var rest = match.Groups[3].Value;
        
        var state = checkChar switch
        {
            "x" or "X" => TaskState.Closed,
            "-" => TaskState.InProgress,
            _ => TaskState.Open
        };
        
        // Parse labels
        var labels = new List<string>();
        foreach (Match labelMatch in Regex.Matches(rest, @"`([^`]+)`"))
        {
            labels.Add(labelMatch.Groups[1].Value);
        }
        
        // Parse priority
        int? priority = null;
        var priorityMatch = Regex.Match(rest, @"!(\w+)");
        if (priorityMatch.Success && int.TryParse(priorityMatch.Groups[1].Value, out var parsedPriority))
        {
            priority = parsedPriority;
        }
        
        // Parse due date
        DateTime? dueDate = null;
        var dueDateMatch = Regex.Match(rest, @"📅\s*(\d{4}-\d{2}-\d{2})");
        if (dueDateMatch.Success && DateTime.TryParse(dueDateMatch.Groups[1].Value, out var parsed))
        {
            dueDate = parsed;
        }
        
        // Parse assignees
        var assignees = new List<string>();
        foreach (Match assigneeMatch in Regex.Matches(rest, @"@(\w+)"))
        {
            assignees.Add(assigneeMatch.Groups[1].Value);
        }
        
        // Parse source reference
        var sourceMatch = Regex.Match(rest, @"\[(\w+)#(\d+)\](?:\(([^)]+)\))?");
        var parsedProvider = sourceMatch.Success ? sourceMatch.Groups[1].Value : (provider ?? "local");
        var id = sourceMatch.Success ? sourceMatch.Groups[2].Value : Guid.NewGuid().ToString("N")[..8];
        var url = sourceMatch.Success && sourceMatch.Groups[3].Success ? sourceMatch.Groups[3].Value : null;
        
        return new UnifiedTask
        {
            Id = id,
            Provider = parsedProvider,
            Title = UnescapeMarkdown(title),
            State = state,
            Labels = labels,
            Priority = priority,
            DueDate = dueDate,
            Assignees = assignees,
            Bucket = bucket,
            ExternalUrl = url
        };
    }

    private static string GetProviderDisplayName(string provider) => provider switch
    {
        "azdo" or "azure-devops" => "Azure DevOps",
        "github" => "GitHub",
        "gitea" => "Gitea",
        "local" => "Local Tasks",
        _ => provider
    };

    private static string? ParseProviderFromHeader(string header) => header.Trim().ToLower() switch
    {
        "azure devops" => "azdo",
        "github" => "github",
        "gitea" => "gitea",
        "local tasks" => "local",
        _ => header.Trim().ToLower().Replace(" ", "-")
    };

    private static string EscapeMarkdown(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace("*", "\\*")
            .Replace("_", "\\_")
            .Replace("[", "\\[")
            .Replace("]", "\\]");
    }

    private static string UnescapeMarkdown(string text)
    {
        return text
            .Replace("\\*", "*")
            .Replace("\\_", "_")
            .Replace("\\[", "[")
            .Replace("\\]", "]")
            .Replace("\\\\", "\\");
    }
}
