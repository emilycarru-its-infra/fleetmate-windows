using FleetMate.Core.Services.Projects;

namespace FleetMate.Core.Models.Projects;

/// <summary>
/// Demo mode's Projects data: a made-up project, people and work items, a
/// GitHub Project board and issues. Every name, address and id is fictional,
/// and addresses use example.* domains.
/// </summary>
public static class ProjectsDemoData
{
    public const string Organization = "https://dev.example.com/demo";
    public const string Project = "Campus";
    public const string Viewer = "avery-quinn";

    private static readonly string[] People = { "Avery Quinn", "Jordan Lee", "Sam Rivera", "Taylor Brooks" };

    private static readonly (int Id, string Title, string Type, string State, string Area, string Iteration, int Priority, int Person, int Days, string Tags)[] Items =
    {
        (101, "Replace the lab printers' expired certificates", "Task", "Active", @"Campus\Devices\Printers", @"Campus\Sprint 14", 2, 0, 0, "Blocked"),
        (102, "Roll the new Wi-Fi profile out to the library laptops", "User Story", "New", @"Campus\Network", @"Campus\Sprint 14", 3, 0, 1, ""),
        (103, "Kiosk browser crashes after the overnight update", "Bug", "Active", @"Campus\Devices\Kiosks", @"Campus\Sprint 14", 1, 1, 2, "Regression"),
        (104, "Document the loaner checkout steps", "Task", "Resolved", @"Campus\Service Desk", @"Campus\Sprint 13", 4, 2, 4, "Docs"),
        (105, "Retire the old file share after the move", "Feature", "New", @"Campus\Storage", @"Campus\Sprint 15", 3, 3, 6, ""),
        (106, "Group policy for studio workstations' sleep timers", "Task", "Active", @"Campus\Devices\Studios", @"Campus\Sprint 14", 2, 0, 3, ""),
        (107, "Renew the shared mailbox licences", "Task", "Closed", @"Campus\Identity", @"Campus\Sprint 13", 3, 1, 12, ""),
        (108, "Onboarding checklist for spring term staff", "User Story", "New", @"Campus\Identity", @"Campus\Sprint 15", 2, 0, 9, ""),
        (109, "Audit the stale computer accounts", "Task", "Active", @"Campus\Identity", @"Campus\Sprint 14", 3, 2, 40, "Cleanup"),
        (110, "Projector firmware in the lecture halls", "Bug", "New", @"Campus\Devices\AV", "", 0, -1, 5, ""),
    };

    public static List<WorkItem> WorkItems(DateTime nowUtc) => Items.Select(i => new WorkItem
    {
        Id = i.Id,
        Rev = 3,
        Url = $"{Organization}/{Project}/_apis/wit/workItems/{i.Id}",
        Fields = new WorkItemFields
        {
            Title = i.Title,
            State = i.State,
            WorkItemType = i.Type,
            AssignedTo = i.Person < 0 ? null : new IdentityRef
            {
                DisplayName = People[i.Person],
                UniqueName = $"{People[i.Person].ToLowerInvariant().Replace(' ', '.')}@example.edu",
                Id = $"00000000-0000-0000-0000-00000000000{i.Person + 1}",
            },
            AreaPath = i.Area,
            IterationPath = i.Iteration.Length > 0 ? i.Iteration : null,
            Priority = i.Priority > 0 ? i.Priority : null,
            Description = $"<p>{i.Title}. This item is part of the demo data.</p>",
            CreatedDate = nowUtc.AddDays(-i.Days - 3),
            ChangedDate = nowUtc.AddDays(-i.Days).AddHours(-i.Id % 5),
            Tags = i.Tags.Length > 0 ? i.Tags : null,
            TeamProject = Project,
            DueDate = i.Id == 101 ? nowUtc.AddDays(-2) : null,
        },
    }).ToList();

    public static List<UnifiedTask> Tasks(DateTime nowUtc)
    {
        var tasks = WorkItems(nowUtc).Select(w =>
        {
            var task = w.AsUnifiedTask();
            task.ExternalUrl = $"{Organization}/{Project}/_workitems/edit/{w.Id}";
            task.Metadata["project"] = Project;
            if (w.Fields.AssignedTo is { } who) task.Metadata["assignedToUniqueName"] = who.UniqueName;
            task.DueDate = w.Fields.DueDate;
            return task;
        }).ToList();
        tasks.AddRange(Issues(nowUtc).Take(3).Select((i, n) => new UnifiedTask
        {
            Id = i.Number.ToString(),
            Provider = "github",
            Title = i.Title,
            State = n == 2 ? TaskState.InProgress : TaskState.Open,
            Assignees = new() { Viewer },
            Labels = n == 0 ? new() { "enhancement" } : new() { "bug" },
            CreatedAt = i.UpdatedAt.AddDays(-2),
            UpdatedAt = i.UpdatedAt,
            ExternalUrl = i.WebUrl,
            Metadata = new() { ["state"] = n == 2 ? "In Progress" : "Open", ["repository"] = i.Repository },
        }));
        return tasks;
    }

    /// <summary>The viewer's own open items, as the strip and Mine count them.</summary>
    public static List<WorkItem> MyWorkItems(DateTime nowUtc) =>
        WorkItems(nowUtc).Where(w => w.AssignedTo == People[0] && !ProjectsGraphCards.IsClosed(w.State)).ToList();

    public static List<Sprint> Sprints(DateTime nowUtc) => new()
    {
        new Sprint { Id = "s13", Name = "Sprint 13", Path = @"Campus\Sprint 13", Attributes = new() { TimeFrame = "past", StartDate = nowUtc.AddDays(-21), FinishDate = nowUtc.AddDays(-8) } },
        new Sprint { Id = "s14", Name = "Sprint 14", Path = @"Campus\Sprint 14", Attributes = new() { TimeFrame = "current", StartDate = nowUtc.AddDays(-7), FinishDate = nowUtc.AddDays(7) } },
    };

    public static List<GitHubIssueSummary> Issues(DateTime nowUtc) => new()
    {
        new() { Number = 42, Title = "Add a dark mode screenshot to the README", Repository = "example-org/field-notes", WebUrl = "https://github.example.com/example-org/field-notes/issues/42", UpdatedAt = nowUtc.AddHours(-5) },
        new() { Number = 17, Title = "Sync fails when the cache folder is read-only", Repository = "example-org/sync-tool", WebUrl = "https://github.example.com/example-org/sync-tool/issues/17", UpdatedAt = nowUtc.AddDays(-1) },
        new() { Number = 8, Title = "Package the installer for ARM64", Repository = "example-org/sync-tool", WebUrl = "https://github.example.com/example-org/sync-tool/issues/8", UpdatedAt = nowUtc.AddDays(-3) },
    };

    /// <summary>The Queries view's Shared Queries, each with its rows.</summary>
    public static List<QueryRunDisplay> QueryRuns(DateTime nowUtc)
    {
        var tasks = Tasks(nowUtc).Where(t => t.Provider == "azdevops").ToDictionary(t => t.Id);
        QueryRunDisplay Run(string id, string folder, string name, params (int Id, int Depth)[] rows)
        {
            var display = rows.Select((r, i) => new QueryRowDisplay(tasks[r.Id.ToString()], r.Depth,
                i + 1 < rows.Length && rows[i + 1].Depth > r.Depth)).ToList();
            var query = new AdoSharedQuery { Id = id, Name = name, FolderPath = folder, QueryType = rows.Any(r => r.Depth > 0) ? "tree" : "flat" };
            return new QueryRunDisplay(query, display, false, Models.Projects.QueryRuns.AreaBucket(display, name));
        }
        return new()
        {
            Run("q1", "Shared Queries", "Devices - Active work", (101, 0), (103, 0), (106, 0), (110, 0)),
            Run("q2", "Shared Queries", "Identity - Term start", (108, 0), (109, 1), (107, 1)),
            Run("q3", "Shared Queries/Service Desk", "Everything changed this week", (101, 0), (102, 0), (103, 0), (104, 0), (106, 0)),
        };
    }

    public static GitHubProjectField StatusField() => new()
    {
        Id = "status",
        Name = "Status",
        DataType = "SINGLE_SELECT",
        Options = new()
        {
            new() { Id = "todo", Name = "Todo" },
            new() { Id = "progress", Name = "In Progress" },
            new() { Id = "review", Name = "In Review" },
            new() { Id = "done", Name = "Done" },
        },
    };

    public static List<GitHubProjectItem> ProjectItems(DateTime nowUtc)
    {
        GitHubProjectItem Item(string id, string type, string title, string status, string? repo, int number, params string[] labels) => new()
        {
            Id = id,
            Type = type,
            CreatedAt = nowUtc.AddDays(-number),
            UpdatedAt = nowUtc.AddHours(-number),
            Content = type == "DRAFT_ISSUE" ? null : new GitHubProjectItemContent
            {
                Id = $"c{id}", Number = number, Title = title, Repository = repo,
                Url = $"https://github.example.com/{repo}/{(type == "PULL_REQUEST" ? "pull" : "issues")}/{number}",
                Assignees = new() { Viewer }, Labels = labels.ToList(), IsPullRequest = type == "PULL_REQUEST",
            },
            DraftContent = type == "DRAFT_ISSUE" ? new GitHubProjectDraftContent { Title = title } : null,
            FieldValues = new() { new GitHubProjectFieldValue { FieldId = "status", FieldName = "Status", DataType = "SINGLE_SELECT", SingleSelectValue = status } },
        };
        return new()
        {
            Item("p1", "ISSUE", "Sync fails when the cache folder is read-only", "In Progress", "example-org/sync-tool", 17, "bug"),
            Item("p2", "PULL_REQUEST", "Retry uploads with backoff", "In Review", "example-org/sync-tool", 21),
            Item("p3", "ISSUE", "Package the installer for ARM64", "Todo", "example-org/sync-tool", 8, "packaging"),
            Item("p4", "DRAFT_ISSUE", "Write the release notes", "Todo", null, 0),
            Item("p5", "ISSUE", "Add a dark mode screenshot to the README", "Done", "example-org/field-notes", 42, "docs"),
        };
    }
}
