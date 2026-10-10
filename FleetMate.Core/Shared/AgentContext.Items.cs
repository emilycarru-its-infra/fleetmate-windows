using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Identity;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Models.Manage;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Models.Reporting;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Repos;
using static FleetMate.Core.Shared.AgentContext;

namespace FleetMate.Core.Shared;

/// <summary>
/// One builder per kind of thing FleetMate lists. Each names the item, its
/// IDs, where it lives, and the <c>fleetmate</c> commands that fetch or act on
/// it. Commands are left out where the CLI has none for that kind.
/// </summary>
public static class AgentContexts
{
    // ── Projects ─────────────────────────────────────────────────────────

    /// <summary>A work item or issue from any task provider.</summary>
    public static AgentContext WorkItem(UnifiedTask task)
    {
        var isDevOps = task.Provider == "azdevops";
        string? Meta(string key) => task.Metadata.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        var fields = new List<Field> { new(isDevOps ? "ID" : "Number", $"#{task.Id}") };
        if (Meta("workItemType") is { } type) fields.Add(new("Type", type));
        fields.Add(new("State", Meta("state") ?? task.State.ToString()));
        if (task.Assignees.Count > 0) fields.Add(new("Assigned to", string.Join(", ", task.Assignees)));
        if (Meta("areaPath") is { } area) fields.Add(new("Area", area));
        if ((Meta("iterationPath") ?? task.Bucket) is { } iteration) fields.Add(new("Iteration", iteration));
        if (task.Priority is { } priority) fields.Add(new("Priority", priority.ToString()));
        if (task.Labels.Count > 0) fields.Add(new("Tags", string.Join(", ", task.Labels)));

        var commands = isDevOps
            ? new[]
            {
                new Command("show the work item", FleetMateCommandLine.Make("devops", "item", task.Id)),
                new Command("comment or change state", FleetMateCommandLine.Make("devops", "update", task.Id, "--comment", "<text>")),
            }
            : new[] { new Command("show the issue", FleetMateCommandLine.Make("tasks", "show", task.Provider, task.Id)) };
        return new AgentContext(ContextKind.WorkItem, task.Title, ProviderName(task.Provider),
            project: Meta("teamProject"), url: task.ExternalUrl, fields: fields, commands: commands);
    }

    /// <summary>
    /// An Azure DevOps shared query, with its WIQL when the API sent it. The
    /// CLI has no command that runs a stored query, so none is listed.
    /// </summary>
    public static AgentContext Query(AdoSharedQuery query, string? project, string? url, int? resultCount = null)
    {
        var fields = new List<Field> { new("ID", query.Id), new("Query type", query.QueryType) };
        if (query.FolderPath.Length > 0) fields.Add(new("Folder", $"Shared Queries/{query.FolderPath}"));
        if (resultCount is { } count) fields.Add(new("Results shown", count.ToString()));
        return new AgentContext(ContextKind.Query, query.Name, "Azure DevOps", project: project, url: url,
            fields: fields, queryText: query.Wiql, queryLanguage: "sql");
    }

    // ── Development ──────────────────────────────────────────────────────

    public static AgentContext PullRequest(UnifiedPullRequest pr)
    {
        var fields = new List<Field>
        {
            new("Number", pr.Reference),
            new("Repository", $"{pr.Container}/{pr.Repository}"),
            new("Branches", $"{pr.SourceBranch} → {pr.TargetBranch}"),
            new("State", pr.State + (pr.HasConflicts ? " (conflicts)" : "")),
            new("Author", pr.AuthorName),
        };
        if (pr.Reviewers.Count > 0) fields.Add(new("Reviewers", string.Join(", ", pr.Reviewers.Select(r => r.DisplayName))));
        var source = pr.Source == PullRequestSource.AzureDevOps ? "devops" : "github";
        return new AgentContext(ContextKind.PullRequest, pr.Title, pr.Source.DisplayName(),
            project: pr.Container, url: pr.WebUrl, fields: fields,
            commands: new[]
            {
                new Command("your open pull requests", FleetMateCommandLine.Make("prs", "--source", source)),
                new Command("the repository's recent commits",
                    FleetMateCommandLine.Make("repos", "log", $"{pr.Container}/{pr.Repository}", "--ref", $"origin/{pr.SourceBranch}")),
            });
    }

    public static AgentContext Commit(PullRequestCommit commit, RepositoryCommits repository)
    {
        var fields = new List<Field> { new("SHA", commit.Id), new("Repository", repository.DisplayName) };
        if (repository.DefaultBranch is { } branch) fields.Add(new("Branch", branch));
        if (commit.AuthorName is { } author) fields.Add(new("Author", author));
        if (commit.Date is { } date) fields.Add(new("Date", IsoDate(date)));
        return new AgentContext(ContextKind.Commit, commit.Subject, repository.Source.DisplayName(),
            project: repository.Container, url: commit.Url, fields: fields,
            commands: new[] { new Command("recent commits", FleetMateCommandLine.Make("repos", "log", repository.DisplayName)) });
    }

    /// <summary>A pipeline run. The CLI has no pipeline command, so none is listed.</summary>
    public static AgentContext PipelineRun(PipelineRun run)
    {
        var fields = new List<Field> { new("Run ID", run.RunId.ToString()), new("Run", run.RunNumber) };
        if (run.PipelineId is { } id) fields.Add(new("Pipeline ID", id.ToString()));
        fields.Add(new("Status", run.Status.DisplayName()));
        if (run.Repository is { } repository) fields.Add(new("Repository", $"{run.Container}/{repository}"));
        if (run.Branch is { } branch) fields.Add(new("Branch", branch));
        if (run.CommitSha is { } sha) fields.Add(new("Commit", sha));
        if (run.TriggeredBy is { } by) fields.Add(new("Triggered by", by));
        if (run.StartedAt is { } started) fields.Add(new("Started", IsoDate(started)));
        return new AgentContext(ContextKind.PipelineRun, run.PipelineName, run.Source.DisplayName(),
            project: run.Container, url: run.WebUrl, fields: fields);
    }

    /// <summary>A repository FleetMate knows, with its local checkout when there is one.</summary>
    public static AgentContext Repository(RepoRecord record)
    {
        var name = record.Key.DisplayName;
        var fields = new List<Field> { new("Repository", name) };
        if (record.Local?.Path is { } path) fields.Add(new("Checkout", path));
        if (record.DefaultBranch is { } branch) fields.Add(new("Default branch", branch));
        var commands = record.IsLocal
            ? new[]
            {
                new Command("branch and changes", FleetMateCommandLine.Make("repos", "status", name, "--files")),
                new Command("recent commits", FleetMateCommandLine.Make("repos", "log", name)),
            }
            : new[] { new Command("clone it", FleetMateCommandLine.Make("repos", "clone", name)) };
        return new AgentContext(ContextKind.Repository, record.Key.Name, RepoProviderName(record.Key.Provider),
            project: record.Key.Scope, url: record.Catalog?.WebUrl ?? record.Local?.RemoteUrl, fields: fields, commands: commands);
    }

    /// <summary>A file inside a local checkout; <paramref name="line"/> is 1-based.</summary>
    public static AgentContext File(string path, RepoRecord record, int? line = null)
    {
        var fields = new List<Field> { new("Path", path) };
        if (line is { } l) fields.Add(new("Line", l.ToString()));
        fields.Add(new("Repository", record.Key.DisplayName));
        if (record.Local?.Path is { } root)
            fields.Add(new("Full path", System.IO.Path.Combine(root, path.Replace('/', System.IO.Path.DirectorySeparatorChar))));
        return new AgentContext(ContextKind.File, System.IO.Path.GetFileName(path.TrimEnd('/', '\\')),
            RepoProviderName(record.Key.Provider), project: record.Key.Scope, fields: fields,
            commands: new[] { new Command("uncommitted changes", FleetMateCommandLine.Make("repos", "diff", record.Key.DisplayName, path)) });
    }

    // ── Devices and Identity ─────────────────────────────────────────────

    /// <summary>A device row joined from Intune, Apple's organization and Autopilot.</summary>
    public static AgentContext Device(DeviceListRow row)
    {
        var intune = row.Intune;
        var fields = new List<Field>();
        if (row.SerialNumber is { } serial) fields.Add(new("Serial", serial));
        if (intune != null) fields.Add(new("Intune ID", intune.Id));
        if (intune?.AzureAdDeviceId is { } entra) fields.Add(new("Entra device ID", entra));
        if (row.PlatformLabel is { } platform)
            fields.Add(new("Platform", string.Join(" ", new[] { platform, intune?.OsVersion }.Where(p => !string.IsNullOrWhiteSpace(p)))));
        if ((Blank(intune?.Model) ?? Blank(row.Apple?.Model) ?? Blank(row.Autopilot?.Model)) is { } model) fields.Add(new("Model", model));
        if (intune?.UserPrincipalName is { } user) fields.Add(new("User", user));
        if (intune?.ComplianceState is { } compliance) fields.Add(new("Compliance", compliance));
        if (intune?.LastSyncDateTime is { } sync) fields.Add(new("Last sync", sync.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")));
        if (row.Discrepancies.Count > 0) fields.Add(new("Discrepancies", string.Join(", ", row.Discrepancies)));

        var commands = new List<Command>();
        if (row.SerialNumber is { } s)
        {
            commands.Add(new("look it up in every system", FleetMateCommandLine.Make("device", s)));
            if (intune != null) commands.Add(new("the Intune record", FleetMateCommandLine.Make("intune", "device", s)));
        }
        var title = Blank(intune?.DeviceName) ?? row.SerialNumber ?? "Device";
        var source = intune != null ? "Intune" : row.Apple != null ? "Apple Business Manager" : "Autopilot";
        var url = intune == null ? null
            : $"https://intune.microsoft.com/#view/Microsoft_Intune_Devices/DeviceSettingsMenuBlade/~/overview/mdmDeviceId/{intune.Id}";
        return new AgentContext(ContextKind.Device, title, source, url: url, fields: fields, commands: commands);
    }

    public static AgentContext User(EntraUser user)
    {
        var fields = new List<Field>();
        if (Blank(user.UserPrincipalName) is { } upn) fields.Add(new("UPN", upn));
        if (Blank(user.Id) is { } id) fields.Add(new("Object ID", id));
        if (user.JobTitle is { } title) fields.Add(new("Job title", title));
        if (user.Department is { } department) fields.Add(new("Department", department));
        if (user.AccountEnabled is { } enabled) fields.Add(new("Account", enabled ? "Enabled" : "Disabled"));
        var handle = Blank(user.UserPrincipalName) ?? Blank(user.Id) ?? "";
        var url = Blank(user.Id) is { } objectId
            ? $"https://entra.microsoft.com/#view/Microsoft_AAD_UsersAndTenants/UserProfileMenuBlade/~/overview/userId/{objectId}"
            : null;
        return new AgentContext(ContextKind.User, Blank(user.DisplayName) ?? handle, "Entra ID", url: url, fields: fields,
            commands: handle.Length == 0 ? null : new[]
            {
                new Command("the user and their groups", FleetMateCommandLine.Make("entra", "user", handle, "--groups")),
            });
    }

    public static AgentContext Group(EntraGroup group)
    {
        var fields = new List<Field>();
        if (Blank(group.Id) is { } id) fields.Add(new("Object ID", id));
        if (group.Mail is { } mail) fields.Add(new("Mail", mail));
        var kinds = new List<string>();
        if (group.GroupTypes.Contains("Unified")) kinds.Add("Microsoft 365");
        if (group.SecurityEnabled == true) kinds.Add("Security");
        if (group.GroupTypes.Contains("DynamicMembership")) kinds.Add("Dynamic");
        if (kinds.Count > 0) fields.Add(new("Type", string.Join(", ", kinds)));
        if (group.Description is { } description) fields.Add(new("Description", description));
        var handle = Blank(group.Id) ?? Blank(group.DisplayName) ?? "";
        var url = Blank(group.Id) is { } objectId
            ? $"https://entra.microsoft.com/#view/Microsoft_AAD_IAM/GroupDetailsMenuBlade/~/Overview/groupId/{objectId}"
            : null;
        return new AgentContext(ContextKind.Group, Blank(group.DisplayName) ?? handle, "Entra ID", url: url, fields: fields,
            commands: handle.Length == 0 ? null : new[]
            {
                new Command("the group and its members", FleetMateCommandLine.Make("entra", "group", handle, "--members")),
            });
    }

    // ── Inventory, Tickets, Manage, Reporting ────────────────────────────

    /// <summary><paramref name="webBase"/> is the Snipe-IT address the asset page hangs off.</summary>
    public static AgentContext Asset(SnipeAsset asset, string? webBase)
    {
        var fields = new List<Field> { new("Asset ID", asset.Id.ToString()) };
        if (Blank(asset.AssetTag) is { } tag) fields.Add(new("Asset tag", tag));
        if (Blank(asset.Serial) is { } serial) fields.Add(new("Serial", serial));
        if (Blank(asset.Model?.Name) is { } model) fields.Add(new("Model", model));
        if (Blank(asset.StatusLabel?.Name) is { } status) fields.Add(new("Status", status));
        if (Blank(asset.AssignedTo?.Name) is { } assigned) fields.Add(new("Assigned to", assigned));
        if (Blank(asset.Location?.Name) is { } location) fields.Add(new("Location", location));
        var commands = new List<Command>();
        if ((Blank(asset.AssetTag) ?? Blank(asset.Serial)) is { } handle)
            commands.Add(new("the asset record", FleetMateCommandLine.Make("snipe", "asset", handle)));
        if (Blank(asset.Serial) is { } s)
            commands.Add(new("look it up in every system", FleetMateCommandLine.Make("device", s)));
        var trimmed = webBase?.Trim().TrimEnd('/');
        var url = string.IsNullOrEmpty(trimmed) ? null : $"{trimmed}/hardware/{asset.Id}";
        return new AgentContext(ContextKind.Asset, Blank(asset.Name) ?? Blank(asset.AssetTag) ?? $"Asset #{asset.Id}",
            "Snipe-IT", url: url, fields: fields, commands: commands);
    }

    /// <summary><paramref name="url"/> comes from <see cref="TdxConfig.GetTicketWebUrl"/>.</summary>
    public static AgentContext Ticket(TdxTicket ticket, string? url)
    {
        var id = ticket.Id > 0 ? ticket.Id.ToString() : "";
        var fields = new List<Field>();
        if (id.Length > 0) fields.Add(new("ID", id));
        if (ticket.TypeName is { } type) fields.Add(new("Type", type));
        if (ticket.StatusName is { } status) fields.Add(new("Status", status));
        if (ticket.PriorityName is { } priority) fields.Add(new("Priority", priority));
        if (ticket.RequestorName is { } requestor) fields.Add(new("Requestor", requestor));
        if ((Blank(ticket.ResponsibleFullName) ?? Blank(ticket.ResponsibleGroupName)) is { } responsible)
            fields.Add(new("Responsible", responsible));
        fields.Add(new("Age", $"{ticket.DaysOld}d"));
        return new AgentContext(ContextKind.Ticket, Blank(ticket.Title) ?? $"Ticket {id}", "TeamDynamix",
            project: ticket.AccountName, url: url, fields: fields,
            commands: id.Length == 0 ? null : new[]
            {
                new Command("the ticket and its feed", FleetMateCommandLine.Make("tdx", "ticket", id, "--feed")),
                new Command("add a comment", FleetMateCommandLine.Make("tdx", "comment", id, "<text>")),
            });
    }

    /// <summary>A machine in the Manage roster, with its address when a scan found one.</summary>
    public static AgentContext ManageTarget(RosterComputer computer, string? address = null)
    {
        var fields = new List<Field>();
        if (computer.HasHostname) fields.Add(new("Hostname", computer.Hostname));
        if (!computer.IsAdhoc) fields.Add(new("Serial", computer.Serial));
        fields.Add(new("Asset tag", computer.Asset));
        if (address != null) fields.Add(new("Address", address));
        fields.Add(new("Group", computer.Fleet.Length == 0 ? computer.Location : computer.Fleet));
        fields.Add(new("Platform", computer.Platform));
        fields.Add(new("Status", computer.Status));
        fields.Add(new("Allocation", computer.Allocation));

        var commands = new List<Command>();
        var host = computer.HasHostname ? computer.Hostname : address;
        if (!string.IsNullOrWhiteSpace(host))
        {
            commands.Add(new("check SSH", FleetMateCommandLine.Make("ssh", "test", host)));
            commands.Add(new("run a command", FleetMateCommandLine.Make("ssh", "exec", host, "<command>")));
        }
        if (!computer.IsAdhoc)
            commands.Add(new("look it up in every system", FleetMateCommandLine.Make("device", computer.Serial)));
        return new AgentContext(ContextKind.ManageTarget, computer.DisplayName, "Manage roster", fields: fields, commands: commands);
    }

    public static AgentContext ReportingDevice(ReportingDevice device)
    {
        var fields = new List<Field> { new("Serial", device.Serial) };
        if (device.Hostname is { } hostname) fields.Add(new("Hostname", hostname));
        if (device.AssetTag is { } tag) fields.Add(new("Asset tag", tag));
        if (device.Platform is { } platform) fields.Add(new("Platform", platform));
        if (device.User is { } user) fields.Add(new("User", user));
        return new AgentContext(ContextKind.ReportingDevice, device.Name, "ReportMate", fields: fields,
            commands: new[]
            {
                new Command("the ReportMate record", FleetMateCommandLine.Make("reportmate", "device", device.Serial)),
                new Command("look it up in every system", FleetMateCommandLine.Make("device", device.Serial)),
            });
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string ProviderName(string provider) => provider switch
    {
        "azdevops" => "Azure DevOps",
        "github" => "GitHub",
        "gitea" => "Gitea",
        _ => provider,
    };

    private static string RepoProviderName(RepoProvider provider) => provider switch
    {
        RepoProvider.AzureDevOps => "Azure DevOps",
        RepoProvider.GitHub => "GitHub",
        _ => "Git",
    };

    private static string IsoDate(DateTime date) =>
        (date.Kind == DateTimeKind.Local ? date.ToUniversalTime() : date).ToString("yyyy-MM-dd");

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
