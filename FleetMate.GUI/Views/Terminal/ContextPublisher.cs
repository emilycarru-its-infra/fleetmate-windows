using System.Windows;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Terminal;

namespace FleetMate.GUI.Views.Terminal;

/// <summary>
/// Pages report what is selected here, and the FLEETMATE_CONTEXT file every
/// terminal session points at is rewritten to match, so an agent can ask
/// "what is Rod looking at" by reading one JSON file.
/// </summary>
public static class ContextPublisher
{
    private static AppContextFile? File => (Application.Current as App)?.Context;

    public static void Tab(string tab) => File?.SetTab(tab);

    public static void Devices(IEnumerable<IntuneDevice> devices) =>
        File?.SetSelection("device", devices.Select(d => new ContextSelection("device", d.Id, new Dictionary<string, string?>
        {
            ["name"] = d.DeviceName,
            ["serial"] = d.SerialNumber,
            ["user"] = d.UserPrincipalName,
            ["os"] = d.OperatingSystem,
            ["compliance"] = d.ComplianceState,
        })).ToList());

    public static void Asset(SnipeAsset? asset) =>
        File?.SetSelection("asset", asset == null ? Array.Empty<ContextSelection>() : new[]
        {
            new ContextSelection("asset", asset.Id.ToString(), new Dictionary<string, string?>
            {
                ["name"] = asset.DisplayName,
                ["tag"] = asset.AssetTag,
                ["serial"] = asset.Serial,
                ["model"] = asset.Model?.Name,
                ["assignedTo"] = asset.AssignedTo?.Name,
                ["status"] = asset.StatusLabel?.Name,
            })
        });

    public static void Ticket(TdxTicket? ticket) =>
        File?.SetSelection("ticket", ticket == null ? Array.Empty<ContextSelection>() : new[]
        {
            new ContextSelection("ticket", ticket.Id.ToString(), new Dictionary<string, string?>
            {
                ["title"] = ticket.Title,
                ["status"] = ticket.StatusName,
                ["requestor"] = ticket.RequestorName,
                ["responsible"] = ticket.ResponsibleFullName,
            })
        });

    public static void WorkItem(UnifiedTask? task) =>
        File?.SetSelection("workItem", task == null ? Array.Empty<ContextSelection>() : new[]
        {
            new ContextSelection("workItem", task.Id, new Dictionary<string, string?>
            {
                ["title"] = task.Title,
                ["provider"] = task.Provider,
                ["state"] = task.State.ToString(),
                ["assignees"] = string.Join(", ", task.Assignees),
            })
        });
}
