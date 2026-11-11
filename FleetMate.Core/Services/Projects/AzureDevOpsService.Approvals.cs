using System.Text;
using System.Text.Json;
using FleetMate.Core.Models.Projects;
using Serilog;

namespace FleetMate.Core.Services.Projects;

/// <summary>The reads the approval tracker needs, so its matching can be tested without a server.</summary>
public interface IPipelineApprovalSource
{
    Task<List<PipelineApproval>> GetPendingApprovalsAsync(string project);
    Task<HashSet<string>> GetOpenTimelineIdentifiersAsync(string project, long buildId);
}

/// <summary>
/// Pipeline approvals: ManualValidation tasks and environment approval checks,
/// read and answered through the Approvals API.
/// </summary>
public partial class AzureDevOpsService : IPipelineApprovalSource
{
    /// <summary>Pending approvals in a project, ManualValidation tasks and environment checks alike.</summary>
    public async Task<List<PipelineApproval>> GetPendingApprovalsAsync(string project)
    {
        if (!await SetAuthorizationAsync()) throw new InvalidOperationException("Azure DevOps sign-in is unavailable.");
        var json = await GetJsonAsync($"{Uri.EscapeDataString(project)}/_apis/pipelines/approvals?state=pending&$expand=steps&api-version=7.1");
        return ParsePendingApprovals(json, project);
    }

    /// <summary>
    /// Identifiers of the build's timeline records that have not finished,
    /// lowercased. A waiting ManualValidation record carries its approval id here.
    /// </summary>
    public async Task<HashSet<string>> GetOpenTimelineIdentifiersAsync(string project, long buildId)
    {
        if (!await SetAuthorizationAsync()) throw new InvalidOperationException("Azure DevOps sign-in is unavailable.");
        var json = await GetJsonAsync($"{Uri.EscapeDataString(project)}/_apis/build/builds/{buildId}/timeline?api-version=7.0");
        return ParseOpenTimelineIdentifiers(json);
    }

    /// <summary>Approve or reject one approval as the signed-in user.</summary>
    public async Task<PullRequestActionResult> RespondToApprovalAsync(string project, string approvalId, bool approve, string? comment)
    {
        if (!await SetAuthorizationAsync()) return PullRequestActionResult.Failed("Not authenticated to Azure DevOps");
        var label = $"{(approve ? "approve" : "reject")} approval in {project}";
        Log.Information("[azdo] {Action}", label);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch,
                $"{Uri.EscapeDataString(project)}/_apis/pipelines/approvals?api-version=7.1")
            {
                Content = new StringContent(ApprovalResponseBody(approvalId, approve, comment), Encoding.UTF8, "application/json"),
            };
            using var response = await _client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("[azdo] {Action} failed: {Status} - {Error}", label, response.StatusCode, text);
                return PullRequestActionResult.Failed($"{(int)response.StatusCode}: {Truncate(text)}");
            }

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            return ApprovalResponseError(doc.RootElement, approvalId, approve) is { } error
                ? PullRequestActionResult.Failed(error)
                : PullRequestActionResult.Ok();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[azdo] {Action} failed", label);
            return PullRequestActionResult.Failed(ex.Message);
        }
    }

    /// <summary>The PATCH body: one approval, its new status and the comment.</summary>
    internal static string ApprovalResponseBody(string approvalId, bool approve, string? comment) =>
        JsonSerializer.Serialize(new[]
        {
            new { approvalId, status = approve ? "approved" : "rejected", comment = comment ?? "" },
        });

    internal static List<PipelineApproval> ParsePendingApprovals(JsonElement json, string project)
    {
        var result = new List<PipelineApproval>();
        if (!json.TryGetProperty("value", out var list) || list.ValueKind != JsonValueKind.Array) return result;

        foreach (var approval in list.EnumerateArray())
        {
            var id = Str(approval, "id");
            if (string.IsNullOrEmpty(id)) continue;

            // Environment approvals name their build; ManualValidation ones do not.
            long? buildId = approval.TryGetProperty("pipeline", out var pipeline) && pipeline.ValueKind == JsonValueKind.Object
                            && pipeline.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object
                            && owner.TryGetProperty("id", out var ownerId) && ownerId.ValueKind == JsonValueKind.Number
                ? ownerId.GetInt64()
                : null;

            var approvers = approval.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array
                ? steps.EnumerateArray()
                    .Select(step => step.ValueKind == JsonValueKind.Object && step.TryGetProperty("assignedApprover", out var who)
                        ? Str(who, "displayName") : null)
                    .Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().ToList()
                : new List<string>();

            result.Add(new PipelineApproval
            {
                Id = id!,
                Project = project,
                BuildId = buildId,
                Instructions = Str(approval, "instructions") ?? "",
                CreatedOn = PullRequestDateParser.Parse(Str(approval, "createdOn")),
                Approvers = approvers,
            });
        }

        return result;
    }

    internal static HashSet<string> ParseOpenTimelineIdentifiers(JsonElement timeline)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!timeline.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array) return result;

        foreach (var record in records.EnumerateArray())
        {
            if (string.Equals(Str(record, "state"), "completed", StringComparison.OrdinalIgnoreCase)) continue;
            if (Str(record, "identifier") is { Length: > 0 } identifier) result.Add(identifier.ToLowerInvariant());
        }

        return result;
    }

    /// <summary>
    /// When you are not one of its approvers the approval comes back unchanged
    /// rather than as an error; that is turned into a failure here.
    /// </summary>
    internal static string? ApprovalResponseError(JsonElement json, string approvalId, bool approve)
    {
        if (!json.TryGetProperty("value", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var expected = approve ? "approved" : "rejected";
        var returned = list.EnumerateArray()
            .FirstOrDefault(a => string.Equals(Str(a, "id"), approvalId, StringComparison.OrdinalIgnoreCase));
        var status = Str(returned, "status")?.ToLowerInvariant();
        return status != null && status != expected
            ? $"Azure DevOps kept this approval {status}; you may not be one of its approvers."
            : null;
    }
}
