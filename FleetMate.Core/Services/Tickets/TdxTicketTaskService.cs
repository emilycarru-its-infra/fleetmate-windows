using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Models.Tickets;
using Serilog;

namespace FleetMate.Core.Services.Tickets;

/// <summary>
/// The ticket-task routes, against the documented TDWebApi contract:
/// <code>
///   GET    api/{appId}/tickets/{id}/tasks[?isEligiblePredecessor=true]
///   POST   api/{appId}/tickets/{id}/tasks                 standard tasks only
///   GET    api/{appId}/tickets/{id}/tasks/{taskId}
///   PUT    api/{appId}/tickets/{id}/tasks/{taskId}        full update; PercentComplete is read-only
///   DELETE api/{appId}/tickets/{id}/tasks/{taskId}        standard tasks only, cannot be undone
///   GET    api/{appId}/tickets/{id}/tasks/{taskId}/feed
///   POST   api/{appId}/tickets/{id}/tasks/{taskId}/feed   comment and/or PercentComplete
///   POST   api/{appId}/tickets/{id}/tasks/applytemplate
/// </code>
/// Every request runs as the operator and nobody else: it carries the
/// operator's own unexpired SSO token, captured once and set on that request,
/// never the client's default header and never any other credential. No token
/// means no request at all.
///
/// Logs and messages name the operation, the ticket, the task and the status
/// only — never a response body, a URL or an exception's text.
/// </summary>
public sealed class TdxTicketTaskService : ITicketTaskApi
{
    private readonly HttpClient _client;
    private readonly TdxConfig _config;
    private readonly JsonSerializerOptions _json;
    private readonly Func<string?> _operatorToken;
    private readonly Func<string?> _operatorEmail;

    internal TdxTicketTaskService(HttpClient client, TdxConfig config, JsonSerializerOptions json,
        Func<string?> operatorToken, Func<string?> operatorEmail)
    {
        _client = client;
        _config = config;
        _json = json;
        _operatorToken = operatorToken;
        _operatorEmail = operatorEmail;
    }

    private string TasksUrl(int ticketId, string? rest = null) =>
        _config.GetTicketsUrl(rest == null ? $"{ticketId}/tasks" : $"{ticketId}/tasks/{rest}");

    /// <inheritdoc />
    public async Task<IReadOnlyList<TdxTicketTask>> GetTasksAsync(int ticketId, bool eligiblePredecessorsOnly = false, CancellationToken ct = default)
    {
        var url = TasksUrl(ticketId) + (eligiblePredecessorsOnly ? "?isEligiblePredecessor=true" : "");
        var what = $"read the tasks of ticket {ticketId}";
        using var response = await SendAsync(HttpMethod.Get, url, null, what, ct);
        var tasks = await ReadAsync<List<TdxTicketTask?>>(response, what, ct)
                    ?? throw Malformed(what, response, "no task list");
        return RequireTasks(tasks, ticketId, what, response).OrderBy(t => t.Order).ThenBy(t => t.Id).ToList();
    }

    /// <inheritdoc />
    public async Task<(TdxTicketTask Task, JsonElement Raw)> GetTaskAsync(int ticketId, int taskId, CancellationToken ct = default)
    {
        var what = $"read task {taskId} on ticket {ticketId}";
        using var response = await SendAsync(HttpMethod.Get, TasksUrl(ticketId, taskId.ToString()), null, what, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("not an object");
            var task = doc.RootElement.Deserialize<TdxTicketTask>(_json);
            return (TicketTaskIdentity.Require(task, ticketId, taskId, what, (int)response.StatusCode), doc.RootElement.Clone());
        }
        catch (JsonException ex)
        {
            Log.Warning("[tdx] Tasks: {Operation} returned an unreadable answer", what);
            throw new TdxTicketTaskException($"TeamDynamix sent an answer FleetMate could not read when asked to {what}.",
                (int)response.StatusCode, ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TdxFeedEntry>> GetTaskFeedAsync(int ticketId, int taskId, CancellationToken ct = default)
    {
        var what = $"read the activity of task {taskId}";
        using var response = await SendAsync(HttpMethod.Get, TasksUrl(ticketId, $"{taskId}/feed"), null, what, ct);
        var feed = await ReadAsync<List<TdxFeedEntry?>>(response, what, ct) ?? throw Malformed(what, response, "no activity list");
        if (feed.Any(e => e is not { Id: > 0 })) throw Malformed(what, response, "an activity entry without an ID");

        // The collection reports RepliesCount but never the replies; only the
        // tenant-level entry carries them. One entry failing to load leaves
        // that entry as it was rather than failing the whole feed.
        var withReplies = await Task.WhenAll(feed.Select(async e =>
        {
            var entry = e!;
            if (!entry.HasUnloadedReplies) return entry;
            try
            {
                using var single = await SendAsync(HttpMethod.Get, _config.GetApiUrl($"feed/{entry.Id}"), null, "read a reply thread", ct);
                var full = await ReadAsync<TdxFeedEntry>(single, "read a reply thread", ct);
                // Only the entry asked for supplies replies.
                return full is { } f && f.Id == entry.Id && f.ReplyList.Count > 0 ? entry.WithReplies(f.ReplyList.ToList()) : entry;
            }
            catch (TdxTicketTaskException)
            {
                return entry;
            }
        }));
        return withReplies.OrderByDescending(e => e.CreatedDate).ToList();
    }

    /// <inheritdoc />
    public async Task<TdxFeedEntry> PostTaskFeedAsync(int ticketId, int taskId, TdxTicketTaskFeedRequest request, CancellationToken ct = default)
    {
        if (request.PercentComplete is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(request), "Percent complete must be between 0 and 100.");
        if (request.PercentComplete == null && string.IsNullOrWhiteSpace(request.Comments))
            throw new ArgumentException("A task update needs a comment or a percent complete.", nameof(request));

        var what = $"update task {taskId} on ticket {ticketId}";
        using var response = await SendAsync(HttpMethod.Post, TasksUrl(ticketId, $"{taskId}/feed"), request, what, ct);
        // The feed entry TeamDynamix generated is the proof the update was
        // recorded. A success with an empty object, an error object or
        // anything else without an entry ID proves nothing, so it is unknown.
        var entry = await ReadAsync<TdxFeedEntry>(response, what, ct);
        if (entry is not { Id: > 0 }) throw Malformed(what, response, "no activity entry");
        Log.Information("[tdx] Tasks: update posted to task {Task} on ticket {Ticket} (progress {Percent}, entry {Entry})",
            taskId, ticketId, request.PercentComplete?.ToString() ?? "unchanged", entry.Id);
        return entry;
    }

    /// <inheritdoc />
    public async Task<TdxTicketTask> CreateTaskAsync(int ticketId, IDictionary<string, object?> body, CancellationToken ct = default)
    {
        var what = $"add a task to ticket {ticketId}";
        using var response = await SendAsync(HttpMethod.Post, TasksUrl(ticketId), body, what, ct);
        var task = TicketTaskIdentity.Require(await ReadAsync<TdxTicketTask>(response, what, ct), ticketId, null, what, (int)response.StatusCode);
        Log.Information("[tdx] Tasks: task {Task} added to ticket {Ticket}", task.Id, ticketId);
        return task;
    }

    /// <inheritdoc />
    public async Task<TdxTicketTask> UpdateTaskAsync(int ticketId, int taskId, IDictionary<string, object?> body, CancellationToken ct = default)
    {
        var what = $"save task {taskId} on ticket {ticketId}";
        using var response = await SendAsync(HttpMethod.Put, TasksUrl(ticketId, taskId.ToString()), body, what, ct);
        Log.Information("[tdx] Tasks: task {Task} on ticket {Ticket} saved (fields: {Fields})",
            taskId, ticketId, string.Join(", ", body.Keys.OrderBy(k => k, StringComparer.Ordinal)));
        return TicketTaskIdentity.Require(await ReadAsync<TdxTicketTask>(response, what, ct), ticketId, taskId, what, (int)response.StatusCode);
    }

    /// <inheritdoc />
    public async Task DeleteTaskAsync(int ticketId, int taskId, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, TasksUrl(ticketId, taskId.ToString()), null,
            $"delete task {taskId} on ticket {ticketId}", ct);
        Log.Information("[tdx] Tasks: delete of task {Task} on ticket {Ticket} accepted", taskId, ticketId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TdxTicketTask>> ApplyTaskTemplateAsync(int ticketId, TdxApplyTaskTemplateRequest request, CancellationToken ct = default)
    {
        if (request.TaskTemplateId <= 0)
            throw new ArgumentException("A task template ID is required.", nameof(request));
        var what = $"apply the task template to ticket {ticketId}";
        using var response = await SendAsync(HttpMethod.Post, TasksUrl(ticketId, "applytemplate"), request, what, ct);
        Log.Information("[tdx] Tasks: task template {Template} applied to ticket {Ticket} (remove existing requested: {Remove})",
            request.TaskTemplateId, ticketId, request.RemoveExistingTasks);
        var tasks = await ReadAsync<List<TdxTicketTask?>>(response, what, ct) ?? throw Malformed(what, response, "no task list");
        return RequireTasks(tasks, ticketId, what, response);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TdxPerson>> SearchPeopleAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<TdxPerson>();
        var url = _config.GetPeopleUrl($"lookup?searchText={Uri.EscapeDataString(text.Trim())}&maxResults=10");
        using var response = await SendAsync(HttpMethod.Get, url, null, "look people up", ct);
        return await ReadAsync<List<TdxPerson>>(response, "look people up", ct) ?? new();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TdxGroup>> SearchGroupsAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<TdxGroup>();
        var body = new Dictionary<string, object?> { ["NameLike"] = text.Trim(), ["IsActive"] = true };
        using var response = await SendAsync(HttpMethod.Post, _config.GetApiUrl("groups/search"), body, "look groups up", ct);
        var groups = await ReadAsync<List<TdxGroup>>(response, "look groups up", ct) ?? new();
        return groups.Where(g => g.Id > 0).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).Take(20).ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Exact identity only: the one person whose primary email is the signed-in
    /// account's. A lookup that finds only someone else, even as its single
    /// result, or more than one exact match is no answer, so nobody is
    /// assigned as "me" by a near miss.
    /// </remarks>
    public async Task<TdxPerson?> GetMeAsync(CancellationToken ct = default)
    {
        var email = _operatorEmail()?.Trim();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) return null;
        var exact = (await SearchPeopleAsync(email, ct))
            .Where(p => p.Uid is { } uid && uid != Guid.Empty
                        && string.Equals(p.PrimaryEmail?.Trim(), email, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return exact.Count == 1 ? exact[0] : null;
    }

    /// <summary>Every task in a list must belong to the ticket; one that does not fails the whole answer.</summary>
    private static List<TdxTicketTask> RequireTasks(List<TdxTicketTask?> tasks, int ticketId, string what, HttpResponseMessage response)
    {
        foreach (var task in tasks) TicketTaskIdentity.Require(task, ticketId, null, what, (int)response.StatusCode);
        if (tasks.Select(t => t!.Id).Distinct().Count() != tasks.Count) throw Malformed(what, response, "the same task twice");
        return tasks.Select(t => t!).ToList();
    }

    private static TdxTicketTaskException Malformed(string what, HttpResponseMessage response, string problem)
    {
        Log.Warning("[tdx] Tasks: {Operation} answered with {Problem}", what, problem);
        return new TdxTicketTaskException($"TeamDynamix answered with {problem} when asked to {what}, so the answer was not used.",
            (int)response.StatusCode);
    }

    // ── Transport ──────────────────────────────────────────────────────

    internal HttpRequestMessage BuildRequest(HttpMethod method, string url, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        if (body != null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, _json), Encoding.UTF8, "application/json");
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body, string what, CancellationToken ct)
    {
        var token = _operatorToken();
        if (token == null)
        {
            Log.Information("[tdx] Tasks: {Operation} not sent: no signed-in TeamDynamix session", what);
            throw new TdxTicketTaskException("Your TeamDynamix sign-in has expired or is missing. Sign in again to work with tasks.", 401);
        }

        using var request = BuildRequest(method, url, body);
        // Per request, so whatever set the client's default header last is
        // never what authorizes a task call.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var timedOut = ex is TaskCanceledException or TimeoutException;
            Log.Warning("[tdx] Tasks: {Operation} failed: {Kind}", what, timedOut ? "timeout" : ex.GetType().Name);
            throw new TdxTicketTaskException(timedOut
                ? $"TeamDynamix did not answer in time when asked to {what}."
                : $"Could not reach TeamDynamix to {what}.", null, ex);
        }

        if (response.IsSuccessStatusCode) return response;

        var status = (int)response.StatusCode;
        var reason = status is 400 or 409 or 422 ? await ValidationMessageAsync(response, ct) : null;
        response.Dispose();
        Log.Warning("[tdx] Tasks: {Operation} failed: HTTP {Status}", what, status);
        throw new TdxTicketTaskException(status switch
        {
            401 => "Your TeamDynamix sign-in has expired. Sign in again to work with tasks.",
            403 => $"You do not have permission to {what}.",
            404 => $"TeamDynamix could not find that when asked to {what}. It may have been removed.",
            429 => "TeamDynamix is limiting requests right now. Wait a minute and try again.",
            _ when reason != null => $"TeamDynamix did not {what}: {reason}",
            _ => $"TeamDynamix could not {what} (HTTP {status}).",
        }, status);
    }

    /// <summary>
    /// The one-line reason a validation failure gives, when TeamDynamix sends
    /// one as a JSON <c>Message</c>: plain text only, one line, capped. Markup
    /// or anything else is dropped rather than shown.
    /// </summary>
    internal static string? SanitizeValidationMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        string? message;
        try
        {
            using var doc = JsonDocument.Parse(body);
            message = doc.RootElement.ValueKind == JsonValueKind.Object
                      && doc.RootElement.TryGetProperty("Message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(message) || message.Contains('<') || message.Contains("://")) return null;
        var line = string.Join(" ", message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        line = new string(line.Where(c => !char.IsControl(c)).ToArray());
        return line.Length > 200 ? line[..200] + "…" : line;
    }

    private static async Task<string?> ValidationMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return SanitizeValidationMessage(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<T?> ReadAsync<T>(HttpResponseMessage response, string what, CancellationToken ct) where T : class
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(text, _json);
        }
        catch (JsonException ex)
        {
            Log.Warning("[tdx] Tasks: {Operation} returned an unreadable answer", what);
            throw new TdxTicketTaskException($"TeamDynamix sent an answer FleetMate could not read when asked to {what}.",
                (int)response.StatusCode, ex);
        }
    }
}
