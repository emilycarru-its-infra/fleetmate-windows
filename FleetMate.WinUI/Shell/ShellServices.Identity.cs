using FleetMate.Core.Config;
using FleetMate.Core.Identity;
using FleetMate.Core.Services;
using Serilog;

namespace FleetMate.WinUI.Shell;

/// <summary>Microsoft Graph and the Identity tab's directory.</summary>
public sealed partial class ShellServices
{
    /// <summary>Microsoft Graph, or null when no tenant is configured (and always in demo mode).</summary>
    public GraphService? Graph { get; private set; }

    /// <summary>
    /// The directory the Identity tab reads and changes: Graph when it is
    /// configured, a fictional one in demo mode, otherwise null and the tab
    /// says Graph is not configured.
    /// </summary>
    public IIdentityDirectory? Identity { get; set; }

    private void InitializeIdentity(FleetMateConfig config)
    {
        if (config.Graph == null || string.IsNullOrEmpty(config.Graph.TenantId)) return;
        // Elevation rides along: without it the Graph transport reports that
        // elevation is not configured even where it is.
        Graph = new GraphService(config.Graph, config.Elevation);
        Identity = new GraphIdentityDirectory(Graph);
        Log.Information("GraphService initialized");
    }

    private void DisposeIdentity() => Graph?.Dispose();
}
