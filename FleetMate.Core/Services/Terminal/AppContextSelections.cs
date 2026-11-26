using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Models.Tickets;

namespace FleetMate.Core.Services.Terminal;

/// <summary>
/// What a page's selection is written to the FLEETMATE_CONTEXT file as: the
/// kind, the id, and the fields an agent most often needs. Both desktop apps
/// publish through these (assets through <c>Inventory.AssetContext</c>), so
/// an agent reads the same shape from either.
/// </summary>
public static class AppContextSelections
{
    public static IReadOnlyList<ContextSelection> PullRequest(UnifiedPullRequest? pr) =>
        pr == null ? Array.Empty<ContextSelection>() : new[]
        {
            new ContextSelection("pullRequest", $"{pr.Repository}#{pr.Number}", new Dictionary<string, string?>
            {
                ["title"] = pr.Title,
                ["repository"] = string.IsNullOrEmpty(pr.Container) ? pr.Repository : $"{pr.Container}/{pr.Repository}",
                ["sourceBranch"] = pr.SourceBranch,
                ["targetBranch"] = pr.TargetBranch,
                ["author"] = pr.AuthorName,
                ["url"] = pr.WebUrl,
                ["source"] = pr.Source.ToString(),
            })
        };

    public static IReadOnlyList<ContextSelection> Devices(IEnumerable<IntuneDevice> devices) =>
        devices.Select(d => new ContextSelection("device", d.Id, new Dictionary<string, string?>
        {
            ["name"] = d.DeviceName,
            ["serial"] = d.SerialNumber,
            ["user"] = d.UserPrincipalName,
            ["os"] = d.OperatingSystem,
            ["compliance"] = d.ComplianceState,
        })).ToList();

    public static IReadOnlyList<ContextSelection> Ticket(TdxTicket? ticket) =>
        ticket == null ? Array.Empty<ContextSelection>() : new[]
        {
            new ContextSelection("ticket", ticket.Id.ToString(), new Dictionary<string, string?>
            {
                ["title"] = ticket.Title,
                ["status"] = ticket.StatusName,
                ["requestor"] = ticket.RequestorName,
                ["responsible"] = ticket.ResponsibleFullName,
            })
        };

    public static IReadOnlyList<ContextSelection> WorkItem(UnifiedTask? task) =>
        task == null ? Array.Empty<ContextSelection>() : new[]
        {
            new ContextSelection("workItem", task.Id, new Dictionary<string, string?>
            {
                ["title"] = task.Title,
                ["provider"] = task.Provider,
                ["state"] = task.State.ToString(),
                ["assignees"] = string.Join(", ", task.Assignees),
            })
        };
}
