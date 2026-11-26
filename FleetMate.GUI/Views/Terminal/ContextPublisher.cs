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
/// "what is the operator looking at" by reading one JSON file. The shape of
/// each selection is <see cref="AppContextSelections"/>'s, shared with the WinUI app.
/// </summary>
public static class ContextPublisher
{
    private static AppContextFile? File => (Application.Current as App)?.Context;

    public static void Tab(string tab) => File?.SetTab(tab);

    /// <summary>The segment a tab shows (Development: Pulls, Inbox, Commits, ...).</summary>
    public static void Segment(string tab, string? segment) => File?.SetSegment(tab, segment);

    public static void PullRequest(UnifiedPullRequest? pr) => File?.SetSelection("pullRequest", AppContextSelections.PullRequest(pr));

    public static void Devices(IEnumerable<IntuneDevice> devices) => File?.SetSelection("device", AppContextSelections.Devices(devices));

    public static void Asset(SnipeAsset? asset) =>
        File?.SetSelection("asset", FleetMate.Core.Inventory.AssetContext.Selection(asset));

    public static void Ticket(TdxTicket? ticket) => File?.SetSelection("ticket", AppContextSelections.Ticket(ticket));

    public static void WorkItem(UnifiedTask? task) => File?.SetSelection("workItem", AppContextSelections.WorkItem(task));
}
