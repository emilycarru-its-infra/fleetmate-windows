using System.Windows;
using FleetMate.Core.Links;
using FleetMate.GUI.Links;
using FleetMate.GUI.Views.Shared;
using Serilog;

namespace FleetMate.GUI;

/// <summary>Opening <c>fleetmate:</c> links: route each to the tab that shows it.</summary>
public partial class App
{
    /// <summary>A pull request, commit or pipeline link waiting for the Development tab.</summary>
    public FleetMateLink? PendingDevelopmentLink { get; set; }

    /// <summary>A GitHub issue link waiting for the Projects tab.</summary>
    public FleetMateLink.GitHubIssue? PendingNavigateGitHubIssue { get; set; }

    /// <summary>Raised after a Development link is queued, for a Development tab that is already showing.</summary>
    public event EventHandler? DevelopmentLinkRequested;

    /// <summary>Start listening for links from later launches, register the protocol, and open this launch's own link.</summary>
    private void StartLinks(string? startupLink)
    {
        LinkHost.Listen(message => Dispatcher.BeginInvoke(() => OpenLink(message)));
        // fleetmate: links belong to FleetMate; TicketsMate never takes them over.
        if (!FleetMate.Core.Config.AppEdition.Current.IsTicketsOnly) _ = Task.Run(LinkHost.RegisterProtocol);
        if (startupLink != null)
            Dispatcher.BeginInvoke(() => OpenLink(startupLink), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Bring the window forward and open <paramref name="link"/>; an empty
    /// message (a second launch with no link) only brings the window forward.
    /// </summary>
    public void OpenLink(string link)
    {
        if (MainWindow is not MainWindow window) return;
        BringToFront(window);
        if (string.IsNullOrWhiteSpace(link)) return;

        if (FleetMateLink.TryParse(link, out var error) is not { } parsed)
        {
            Log.Information("Unopenable link {Link}: {Reason}", link, error?.Message);
            window.ShowLinkBanner(error?.Message ?? $"FleetMate can't open {link}.");
            return;
        }

        Log.Information("Opening {Link}", parsed.ToLink());
        window.HideLinkBanner();
        switch (parsed)
        {
            case FleetMateLink.WorkItem workItem:
                PendingNavigateWorkItemId = workItem.Id;
                window.NavigateToTab("Projects");
                break;
            case FleetMateLink.GitHubIssue issue:
                PendingNavigateGitHubIssue = issue;
                window.NavigateToTab("Projects");
                break;
            case FleetMateLink.Device device:
                PendingNavigateDeviceId = device.IntuneId;
                window.NavigateToTab("Devices");
                break;
            case FleetMateLink.Asset asset:
                PendingNavigateAssetId = asset.Id;
                window.NavigateToTab("Inventory");
                break;
            case FleetMateLink.Ticket ticket:
                PendingNavigateTicketId = ticket.Id;
                window.NavigateToTab("Tickets");
                break;
            case FleetMateLink.Reporting reporting:
                window.OpenReportingLink(reporting.ToReportMateUrl());
                break;
            case FleetMateLink.User or FleetMateLink.Group:
                window.NavigateToTab("Identity");
                break;
            default:
                PendingDevelopmentLink = parsed;
                window.NavigateToTab("Development");
                DevelopmentLinkRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        // Windows only lets a background process take the foreground briefly;
        // a topmost blink lifts the window over whatever opened the link.
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }
}
