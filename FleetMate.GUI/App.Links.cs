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
        LinkHost.Listen(message => Dispatcher.BeginInvoke(() => OnHandoff(message)));
        _ = Task.Run(LinkHost.RegisterProtocol);
        if (startupLink != null)
            Dispatcher.BeginInvoke(() => OpenLink(startupLink), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// The real main window, remembered when it is created. Application.MainWindow
    /// alone is not enough: WPF makes the first window created while it is null
    /// the main window, and that can be a hidden sign-in host.
    /// </summary>
    internal MainWindow? Shell { get; set; }

    /// <summary>The main window to bring forward, never a hidden sign-in host or a dialog.</summary>
    internal MainWindow? ShellWindow() =>
        WindowActivation.Target(Shell, MainWindow, Windows.Cast<object>());

    /// <summary>
    /// Keep <paramref name="window"/> (a window that is never meant to be seen)
    /// from standing in as Application.MainWindow, which WPF assigns to it when
    /// it is created while MainWindow is null.
    /// </summary>
    internal static void NeverMainWindow(Window window)
    {
        if (Current is App app && ReferenceEquals(app.MainWindow, window))
            app.MainWindow = app.ShellWindow();
    }

    /// <summary>A later launch handed over a link, or an empty message to come to the front.</summary>
    private void OnHandoff(string message)
    {
        var window = ShellWindow();
        if (window == null)
        {
            Log.Warning("Handoff {Link} received, but there is no main window to show", Describe(message));
            return;
        }
        var wasVisible = window.IsVisible;
        var wasMinimized = window.WindowState == WindowState.Minimized;
        OpenLink(message);
        Log.Information("Handoff {Link} received; main window brought forward (was visible {WasVisible}, minimized {WasMinimized})",
            Describe(message), wasVisible, wasMinimized);
    }

    private static string Describe(string message) =>
        string.IsNullOrWhiteSpace(message) ? "(activate)" : message;

    /// <summary>
    /// Bring the window forward and open <paramref name="link"/>; an empty
    /// message (a second launch with no link) only brings the window forward.
    /// </summary>
    public void OpenLink(string link)
    {
        if (ShellWindow() is not { } window)
        {
            Log.Warning("No main window to open {Link} in", Describe(link));
            return;
        }
        BringToFront(window);
        if (string.IsNullOrWhiteSpace(link)) return;

        if (!link.Trim().Trim('"').StartsWith(FleetMate.Core.Config.AppEdition.Current.LinkScheme + ":", StringComparison.OrdinalIgnoreCase))
            return;

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
                window.OpenTicketLink(ticket.Id);
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
        var plan = WindowActivation.Plan(window.IsVisible, window.WindowState == WindowState.Minimized);
        if (plan.Show) window.Show();
        if (plan.Restore) window.WindowState = WindowState.Normal;
        window.Activate();
        // Windows only lets a background process take the foreground briefly
        // (the launch that handed over allows it); a topmost blink lifts the
        // window over whatever opened the link.
        window.Topmost = true;
        window.Topmost = false;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
        window.Focus();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
