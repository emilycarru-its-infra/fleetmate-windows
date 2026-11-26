namespace FleetMate.Core.Links;

/// <summary>Which tab opens a <c>fleetmate:</c> link, for every window that routes them.</summary>
public static class LinkRoutes
{
    /// <summary>
    /// The tab that shows <paramref name="link"/>: work items and GitHub issues
    /// in Projects, devices in Devices, assets in Inventory, tickets in Tickets,
    /// ReportMate pages in Reporting, users and groups in Identity, and pull
    /// requests, commits and pipelines in Development.
    /// </summary>
    public static string TabFor(FleetMateLink link) => link switch
    {
        FleetMateLink.WorkItem or FleetMateLink.GitHubIssue => "Projects",
        FleetMateLink.Device => "Devices",
        FleetMateLink.Asset => "Inventory",
        FleetMateLink.Ticket => "Tickets",
        FleetMateLink.Reporting => "Reporting",
        FleetMateLink.User or FleetMateLink.Group => "Identity",
        _ => "Development",
    };
}
