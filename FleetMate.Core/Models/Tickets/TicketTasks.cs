using System.Text.Json;
using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Tickets;

/// <summary>
/// What a ticket task is backed by, from the documented <c>TicketTaskType</c>
/// enumeration. Only <see cref="Standard"/> tasks can be created or deleted
/// through the ticket-task routes; workflow tasks belong to the ticket's
/// workflow and maintenance activities to its schedule.
/// </summary>
public enum TicketTaskType
{
    None = 0,
    Standard = 1,
    MaintenanceActivity = 2,
    Workflow = 3,
}

/// <summary>
/// A ticket task or activity, from <c>GET /api/{appId}/tickets/{ticketId}/tasks</c>
/// and <c>/tasks/{id}</c>. Every field the documented <c>TicketTask</c> schema
/// carries is read; only the writable ones are ever sent back (see
/// <see cref="TicketTaskWrite"/>).
/// </summary>
public sealed class TdxTicketTask
{
    [JsonPropertyName("ID")]
    public int Id { get; set; }

    [JsonPropertyName("TicketID")]
    public int TicketId { get; set; }

    [JsonPropertyName("TypeID")]
    public int TypeId { get; set; }

    [JsonPropertyName("Title")]
    public string? Title { get; set; }

    [JsonPropertyName("Description")]
    public string? Description { get; set; }

    [JsonPropertyName("IsActive")]
    public bool IsActive { get; set; }

    [JsonPropertyName("PercentComplete")]
    public int PercentComplete { get; set; }

    [JsonPropertyName("StartDate")]
    public DateTimeOffset? StartDate { get; set; }

    [JsonPropertyName("EndDate")]
    public DateTimeOffset? EndDate { get; set; }

    /// <summary>Operational minutes the task is expected to take, or null.</summary>
    [JsonPropertyName("CompleteWithinMinutes")]
    public int? CompleteWithinMinutes { get; set; }

    [JsonPropertyName("EstimatedMinutes")]
    public int EstimatedMinutes { get; set; }

    [JsonPropertyName("ActualMinutes")]
    public int ActualMinutes { get; set; }

    [JsonPropertyName("Order")]
    public int Order { get; set; }

    [JsonPropertyName("PredecessorID")]
    public int PredecessorId { get; set; }

    [JsonPropertyName("PredecessorTitle")]
    public string? PredecessorTitle { get; set; }

    [JsonPropertyName("ResponsibleUid")]
    public Guid? ResponsibleUid { get; set; }

    [JsonPropertyName("ResponsibleFullName")]
    public string? ResponsibleFullName { get; set; }

    [JsonPropertyName("ResponsibleEmail")]
    public string? ResponsibleEmail { get; set; }

    [JsonPropertyName("ResponsibleGroupID")]
    public int ResponsibleGroupId { get; set; }

    [JsonPropertyName("ResponsibleGroupName")]
    public string? ResponsibleGroupName { get; set; }

    [JsonPropertyName("NotifyResponsible")]
    public bool? NotifyResponsible { get; set; }

    [JsonPropertyName("CompletedDate")]
    public DateTimeOffset? CompletedDate { get; set; }

    [JsonPropertyName("CompletedFullName")]
    public string? CompletedFullName { get; set; }

    [JsonPropertyName("CreatedDate")]
    public DateTimeOffset? CreatedDate { get; set; }

    [JsonPropertyName("CreatedFullName")]
    public string? CreatedFullName { get; set; }

    [JsonPropertyName("ModifiedDate")]
    public DateTimeOffset? ModifiedDate { get; set; }

    [JsonPropertyName("ModifiedFullName")]
    public string? ModifiedFullName { get; set; }

    [JsonPropertyName("DetectedConflictCount")]
    public int DetectedConflictCount { get; set; }

    [JsonIgnore]
    public TicketTaskType Type => Enum.IsDefined(typeof(TicketTaskType), TypeId) ? (TicketTaskType)TypeId : TicketTaskType.None;

    [JsonIgnore]
    public bool IsStandard => Type == TicketTaskType.Standard;

    [JsonIgnore]
    public bool IsComplete => PercentComplete >= 100 || TicketTaskFormat.HasDate(CompletedDate);

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? $"Task {Id}" : Title!.Trim();
}

/// <summary>
/// The body of <c>POST /tasks/{id}/feed</c>, the documented <c>TicketTaskFeedEntry</c>:
/// a comment, a new percent complete, or both. This is how progress is set —
/// <c>PercentComplete</c> is read-only on the task itself, so a task PUT
/// cannot change it.
/// </summary>
public sealed class TdxTicketTaskFeedRequest
{
    /// <summary>Not nullable in the schema, so sent as an empty string when there is no comment.</summary>
    [JsonPropertyName("Comments")]
    public string Comments { get; set; } = "";

    [JsonPropertyName("PercentComplete")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PercentComplete { get; set; }

    [JsonPropertyName("IsPrivate")]
    public bool IsPrivate { get; set; }

    [JsonPropertyName("IsRichHtml")]
    public bool IsRichHtml { get; set; }
}

/// <summary>Which date a task template's tasks are scheduled from (<c>TaskTemplateStartDateOption</c>).</summary>
public enum TaskTemplateStartDate
{
    Current = 0,
    TicketCreated = 1,
    TicketStart = 2,
}

/// <summary>The body of <c>POST /tasks/applytemplate</c> (<c>ApplyTaskTemplateParameters</c>).</summary>
public sealed class TdxApplyTaskTemplateRequest
{
    [JsonPropertyName("TaskTemplateId")]
    public int TaskTemplateId { get; set; }

    [JsonPropertyName("RemoveExistingTasks")]
    public bool RemoveExistingTasks { get; set; }

    [JsonPropertyName("StartDateSource")]
    public TaskTemplateStartDate StartDateSource { get; set; }
}

/// <summary>
/// A task template the operator can apply, as TeamDynamix's own Add Task
/// Template page lists it for the ticket (see
/// <see cref="Services.Tickets.TdxTaskTemplatePicker"/>). The API has no
/// task-template list route, and no list is ever hardcoded.
/// </summary>
public sealed record TaskTemplateChoice(int Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A TeamDynamix group, from <c>POST /api/groups/search</c>, for the responsibility picker.</summary>
public sealed class TdxGroup
{
    [JsonPropertyName("ID")]
    public int Id { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("IsActive")]
    public bool IsActive { get; set; }

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? $"Group {Id}" : Name!;
}

/// <summary>
/// The writable fields of a standard task, as the editor holds them. Read-only
/// fields (progress, completion, type, order, audit fields) are not here,
/// because nothing the editor does may change them.
/// </summary>
public sealed record TicketTaskWrite
{
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public DateTimeOffset? StartDate { get; init; }
    public DateTimeOffset? EndDate { get; init; }
    public int EstimatedMinutes { get; init; }
    public int? CompleteWithinMinutes { get; init; }
    /// <summary>0 for none.</summary>
    public int PredecessorId { get; init; }
    public Guid? ResponsibleUid { get; init; }
    /// <summary>0 for none.</summary>
    public int ResponsibleGroupId { get; init; }
    public bool NotifyResponsible { get; init; } = true;

    public const int TitleMaxLength = 300;

    /// <summary>The editor's values for an existing task.</summary>
    public static TicketTaskWrite From(TdxTicketTask task) => new()
    {
        Title = task.Title ?? "",
        Description = task.Description,
        StartDate = TicketTaskFormat.HasDate(task.StartDate) ? task.StartDate : null,
        EndDate = TicketTaskFormat.HasDate(task.EndDate) ? task.EndDate : null,
        EstimatedMinutes = task.EstimatedMinutes,
        CompleteWithinMinutes = task.CompleteWithinMinutes,
        PredecessorId = task.PredecessorId,
        ResponsibleUid = task.ResponsibleUid,
        ResponsibleGroupId = task.ResponsibleGroupId,
        NotifyResponsible = task.NotifyResponsible ?? true,
    };

    /// <summary>
    /// The description to send from the editor's plain text. Unedited, the
    /// original is kept exactly — rich text and all — rather than flattened
    /// to what the editor showed.
    /// </summary>
    public static string? DescriptionFromEditor(string? original, string? editorText)
    {
        if ((editorText ?? "") == TicketTaskFormat.PlainText(original)) return original;
        return string.IsNullOrWhiteSpace(editorText) ? null : editorText;
    }

    /// <summary>Why these values cannot be sent, or null when they can.</summary>
    public string? Validate(int? taskId = null)
    {
        var title = Title?.Trim() ?? "";
        if (title.Length == 0) return "A task needs a title.";
        if (title.Length > TitleMaxLength) return $"The title is {title.Length} characters; TeamDynamix allows {TitleMaxLength}.";
        if (EstimatedMinutes < 0) return "The estimate cannot be negative.";
        if (CompleteWithinMinutes is < 0) return "Complete-within cannot be negative.";
        if (StartDate is { } start && EndDate is { } end && end < start) return "The end date is before the start date.";
        if (taskId is int id && id > 0 && PredecessorId == id) return "A task cannot be its own predecessor.";
        return null;
    }
}

/// <summary>
/// Builds the bodies the task routes take. The PUT is a full update — a
/// writable field left out may be cleared — so an edit starts from the task
/// as just read, every writable field echoed back, and only the fields the
/// operator changed replaced.
/// </summary>
public static class TicketTaskBody
{
    /// <summary>The writable fields of the documented <c>TicketTask</c> schema.</summary>
    internal static readonly string[] WritableFields =
    {
        "Title", "Description", "StartDate", "EndDate", "CompleteWithinMinutes", "EstimatedMinutes",
        "PredecessorID", "ResponsibleUid", "ResponsibleGroupID", "NotifyResponsible",
    };

    /// <summary>The body for a new standard task.</summary>
    public static Dictionary<string, object?> ForCreate(TicketTaskWrite values) => new(StringComparer.Ordinal)
    {
        ["Title"] = values.Title.Trim(),
        ["Description"] = Blank(values.Description),
        ["StartDate"] = values.StartDate,
        ["EndDate"] = values.EndDate,
        ["EstimatedMinutes"] = values.EstimatedMinutes,
        ["CompleteWithinMinutes"] = values.CompleteWithinMinutes,
        ["PredecessorID"] = values.PredecessorId,
        ["ResponsibleUid"] = values.ResponsibleUid,
        ["ResponsibleGroupID"] = values.ResponsibleGroupId,
        ["NotifyResponsible"] = values.NotifyResponsible,
    };

    /// <summary>
    /// The PUT body: <paramref name="latest"/>'s writable fields as TeamDynamix
    /// sent them, with the fields that differ between <paramref name="original"/>
    /// (what the editor opened with) and <paramref name="edited"/> replaced.
    /// A field the operator did not touch is echoed exactly, even if it is one
    /// FleetMate does not model.
    /// </summary>
    public static Dictionary<string, object?> ForUpdate(JsonElement latest, TicketTaskWrite original, TicketTaskWrite edited)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (latest.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in latest.EnumerateObject())
            {
                var field = WritableFields.FirstOrDefault(f => string.Equals(f, property.Name, StringComparison.OrdinalIgnoreCase));
                if (field != null)
                    body[field] = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.Clone();
            }
        }

        foreach (var (field, value) in Changes(original, edited))
            body[field] = value;
        return body;
    }

    /// <summary>The writable fields that differ, by wire name.</summary>
    public static Dictionary<string, object?> Changes(TicketTaskWrite original, TicketTaskWrite edited)
    {
        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (original.Title.Trim() != edited.Title.Trim()) changes["Title"] = edited.Title.Trim();
        if (Blank(original.Description) != Blank(edited.Description)) changes["Description"] = Blank(edited.Description);
        if (original.StartDate != edited.StartDate) changes["StartDate"] = edited.StartDate;
        if (original.EndDate != edited.EndDate) changes["EndDate"] = edited.EndDate;
        if (original.EstimatedMinutes != edited.EstimatedMinutes) changes["EstimatedMinutes"] = edited.EstimatedMinutes;
        if (original.CompleteWithinMinutes != edited.CompleteWithinMinutes) changes["CompleteWithinMinutes"] = edited.CompleteWithinMinutes;
        if (original.PredecessorId != edited.PredecessorId) changes["PredecessorID"] = edited.PredecessorId;
        if (original.ResponsibleUid != edited.ResponsibleUid) changes["ResponsibleUid"] = edited.ResponsibleUid;
        if (original.ResponsibleGroupId != edited.ResponsibleGroupId) changes["ResponsibleGroupID"] = edited.ResponsibleGroupId;
        if (original.NotifyResponsible != edited.NotifyResponsible) changes["NotifyResponsible"] = edited.NotifyResponsible;
        return changes;
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>How tasks read in the detail pane. Pure, so it can be tested.</summary>
public static class TicketTaskFormat
{
    /// <summary>TeamDynamix sends <c>0001-01-01</c> for a date that is not set.</summary>
    public static bool HasDate(DateTimeOffset? date) => date is { Year: > 1900 };

    public static string TypeLabel(TicketTaskType type) => type switch
    {
        TicketTaskType.Standard => "Task",
        TicketTaskType.Workflow => "Workflow task",
        TicketTaskType.MaintenanceActivity => "Maintenance activity",
        _ => "Other",
    };

    /// <summary>"3 of 5 complete", or "No tasks".</summary>
    public static string Summary(IReadOnlyCollection<TdxTicketTask> tasks) => tasks.Count switch
    {
        0 => "No tasks",
        _ => $"{tasks.Count(t => t.IsComplete)} of {tasks.Count} complete",
    };

    /// <summary>Where the task stands, in a word or three.</summary>
    public static string State(TdxTicketTask task)
    {
        if (task.IsComplete) return "Complete";
        if (!task.IsActive)
            return task.PredecessorId > 0
                ? $"Waiting on {(string.IsNullOrWhiteSpace(task.PredecessorTitle) ? $"task {task.PredecessorId}" : task.PredecessorTitle!.Trim())}"
                : "Not active yet";
        return task.PercentComplete > 0 ? "In progress" : "Not started";
    }

    /// <summary>Who the task is with: a person, a group, both, or nobody.</summary>
    public static string Responsibility(TdxTicketTask task)
    {
        var person = string.IsNullOrWhiteSpace(task.ResponsibleFullName) ? null : task.ResponsibleFullName!.Trim();
        var group = task.ResponsibleGroupId > 0
            ? (string.IsNullOrWhiteSpace(task.ResponsibleGroupName) ? $"Group {task.ResponsibleGroupId}" : task.ResponsibleGroupName!.Trim())
            : null;
        return (person, group) switch
        {
            (null, null) => "Unassigned",
            (not null, null) => person,
            (null, not null) => group,
            _ => $"{person} ({group})",
        };
    }

    /// <summary>
    /// Feed and description HTML as plain text: line breaks kept, tags
    /// dropped, entities decoded. Nothing of the markup is ever rendered.
    /// </summary>
    public static string PlainText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = System.Text.RegularExpressions.Regex.Replace(html, @"<br\s*/?>|</p>|</div>|</li>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text).Replace(' ', ' ');
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    /// <summary>"1h 30m", "45m", "2d 1h" — minutes as people read them.</summary>
    public static string Minutes(int minutes)
    {
        if (minutes <= 0) return "0m";
        var span = TimeSpan.FromMinutes(minutes);
        var parts = new List<string>();
        if (span.Days > 0) parts.Add($"{span.Days}d");
        if (span.Hours > 0) parts.Add($"{span.Hours}h");
        if (span.Minutes > 0) parts.Add($"{span.Minutes}m");
        return string.Join(" ", parts);
    }

    /// <summary>"Starts …  ·  Due …", or null when the task has no dates.</summary>
    public static string? Dates(TdxTicketTask task)
    {
        var parts = new List<string>();
        if (HasDate(task.StartDate)) parts.Add($"Starts {task.StartDate!.Value.ToLocalTime():MMM d, yyyy h:mm tt}");
        if (HasDate(task.EndDate)) parts.Add($"Due {task.EndDate!.Value.ToLocalTime():MMM d, yyyy h:mm tt}");
        if (HasDate(task.CompletedDate))
            parts.Add($"Completed {task.CompletedDate!.Value.ToLocalTime():MMM d, yyyy}"
                      + (string.IsNullOrWhiteSpace(task.CompletedFullName) ? "" : $" by {task.CompletedFullName!.Trim()}"));
        return parts.Count == 0 ? null : string.Join("  ·  ", parts);
    }

    /// <summary>"Estimate 1h  ·  Logged 30m  ·  Within 2h", or null when none is set.</summary>
    public static string? Effort(TdxTicketTask task)
    {
        var parts = new List<string>();
        if (task.EstimatedMinutes > 0) parts.Add($"Estimate {Minutes(task.EstimatedMinutes)}");
        if (task.ActualMinutes > 0) parts.Add($"Logged {Minutes(task.ActualMinutes)}");
        if (task.CompleteWithinMinutes is > 0) parts.Add($"Within {Minutes(task.CompleteWithinMinutes.Value)}");
        return parts.Count == 0 ? null : string.Join("  ·  ", parts);
    }
}

/// <summary>
/// The check every task TeamDynamix sends back has to pass before FleetMate
/// believes it: a positive ID, the ticket that was asked about and, where one
/// task was asked for, that task. A malformed or foreign object is never
/// shown, edited or taken as proof a change happened.
/// </summary>
public static class TicketTaskIdentity
{
    public static bool Belongs(TdxTicketTask? task, int ticketId, int? taskId = null) =>
        task is { Id: > 0 } && task.TicketId == ticketId && (taskId == null || task.Id == taskId);

    /// <summary>The reason <paramref name="task"/> fails the check, or null.</summary>
    public static string? Problem(TdxTicketTask? task, int ticketId, int? taskId = null) => task switch
    {
        null => "no task",
        { Id: <= 0 } => "a task without an ID",
        _ when task.TicketId != ticketId => "a task from another ticket",
        _ when taskId != null && task.Id != taskId => "a different task",
        _ => null,
    };

    /// <summary>
    /// Throws when <paramref name="task"/> fails the check. The status is the
    /// answer's own (a success), so a change that was sent reads as uncertain,
    /// never as refused.
    /// </summary>
    public static TdxTicketTask Require(TdxTicketTask? task, int ticketId, int? taskId, string what, int? status)
    {
        if (Problem(task, ticketId, taskId) is { } problem)
            throw new TdxTicketTaskException($"TeamDynamix answered with {problem} when asked to {what}, so the answer was not used.", status);
        return task!;
    }
}

/// <summary>
/// A ticket-task request TeamDynamix refused or never answered. The message is
/// always one FleetMate wrote — never a response body, URL or exception text,
/// which can carry ticket content or a sign-in page.
/// </summary>
public sealed class TdxTicketTaskException : Exception
{
    /// <summary>The HTTP status, or null when the request never got one.</summary>
    public int? StatusCode { get; }

    public TdxTicketTaskException(string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// The ticket-task calls the Tickets tab makes, separated from
/// <see cref="Services.Tickets.TdxService"/> so the panel's ordering and
/// guarding can be tested without a server. Every call runs as the signed-in
/// operator and throws <see cref="TdxTicketTaskException"/> on failure.
/// </summary>
public interface ITicketTaskApi
{
    /// <summary>The ticket's tasks; with <paramref name="eligiblePredecessorsOnly"/>, only those another task may follow.</summary>
    Task<IReadOnlyList<TdxTicketTask>> GetTasksAsync(int ticketId, bool eligiblePredecessorsOnly = false, CancellationToken ct = default);

    /// <summary>One task, with the JSON it came as so an edit can echo what FleetMate does not model.</summary>
    Task<(TdxTicketTask Task, JsonElement Raw)> GetTaskAsync(int ticketId, int taskId, CancellationToken ct = default);

    Task<IReadOnlyList<TdxFeedEntry>> GetTaskFeedAsync(int ticketId, int taskId, CancellationToken ct = default);

    /// <summary>
    /// Post a comment and/or a new percent complete to the task feed. Returns
    /// the feed entry TeamDynamix generated; an answer without one throws,
    /// because a 200 with no entry is no proof anything was recorded.
    /// </summary>
    Task<TdxFeedEntry> PostTaskFeedAsync(int ticketId, int taskId, TdxTicketTaskFeedRequest request, CancellationToken ct = default);

    /// <summary>The created task; throws unless it is a task on <paramref name="ticketId"/>.</summary>
    Task<TdxTicketTask> CreateTaskAsync(int ticketId, IDictionary<string, object?> body, CancellationToken ct = default);

    /// <summary>The saved task; throws unless it is <paramref name="taskId"/> on <paramref name="ticketId"/>.</summary>
    Task<TdxTicketTask> UpdateTaskAsync(int ticketId, int taskId, IDictionary<string, object?> body, CancellationToken ct = default);

    Task DeleteTaskAsync(int ticketId, int taskId, CancellationToken ct = default);

    Task<IReadOnlyList<TdxTicketTask>> ApplyTaskTemplateAsync(int ticketId, TdxApplyTaskTemplateRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<TdxPerson>> SearchPeopleAsync(string text, CancellationToken ct = default);

    Task<IReadOnlyList<TdxGroup>> SearchGroupsAsync(string text, CancellationToken ct = default);

    /// <summary>The signed-in operator as a person, for Assign to me; null when it cannot be resolved.</summary>
    Task<TdxPerson?> GetMeAsync(CancellationToken ct = default);
}
