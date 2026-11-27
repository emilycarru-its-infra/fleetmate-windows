using FleetMate.Core.Config;
using FleetMate.Core.Manage;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Reporting;
using FleetMate.Core.Services.Manage;
using Serilog;

namespace FleetMate.WinUI.Shell;

/// <summary>
/// The Manage tab's services, following FleetMate.GUI's App: SSH to the fleet
/// (only when the admin key is on disk), the ReportMate inventory the scan
/// asks for addresses, and the operator's own state (history, custom groups,
/// sidebar order). The page builds its <see cref="ManageViewModel"/> here:
///
/// <code>
/// var vm = App.Services.CreateManageViewModel();
/// </code>
///
/// Without the SSH key the tab still loads rooms and scans; it cannot fetch
/// machine details or run commands, and says so.
/// </summary>
public sealed partial class ShellServices
{
    private ReportMateService? _manageReportMate;
    private IRemoteRunner? _manageRunner;
    private IDeviceDirectory? _manageDirectory;
    private IReachabilityProbe? _manageProbe;
    private IProcessLauncher? _manageLauncher;

    /// <summary>The Manage settings: the config's, or demo mode's own.</summary>
    public ManageConfig ManageConfig { get; private set; } = new();

    /// <summary>History, custom groups and sidebar layout, under the per-user folder (demo mode: a throwaway one).</summary>
    public ManageStateStore ManageState { get; private set; } = new();

    /// <summary>Fleet SSH: null when the admin key is not on disk.</summary>
    public SecureShellService? SecureShell { get; private set; }

    /// <summary>Whether machine details and commands can run (an SSH transport exists).</summary>
    public bool ManageHasSsh => _manageRunner != null;

    /// <summary>The roster demo mode shows instead of a file; null outside demo mode.</summary>
    public string? ManageDemoRosterPath { get; private set; }

    public ManageViewModel CreateManageViewModel()
    {
        var launcher = new RemoteSessionLauncher(ManageConfig, new RdpCredentialStore(), _manageLauncher);
        var vm = new ManageViewModel(ManageConfig, ManageState, _manageDirectory, _manageProbe ?? new NetworkReachabilityProbe(),
            _manageRunner, launcher);
        if (ManageDemoRosterPath != null) vm.RosterPathOverride = ManageDemoRosterPath;
        return vm;
    }

    /// <summary>
    /// Demo mode's fixture (registered in App): a fictional roster and a
    /// transport that answers from canned output. Every file it writes lives
    /// in a throwaway folder, so a demo never touches the person's own history,
    /// groups or command library, and nothing is ever dialled.
    /// </summary>
    public void SeedManageDemo(string rosterCsv, IReachabilityProbe probe, IRemoteRunner runner, IProcessLauncher launcher)
    {
        var root = Path.Combine(Path.GetTempPath(), "FleetMate-demo", "manage-" + Environment.ProcessId);
        Directory.CreateDirectory(root);
        ManageState = new ManageStateStore(root);
        ManageConfig = new ManageConfig { CommandsPath = Path.Combine(root, "commands.yaml") };
        ManageDemoRosterPath = Path.Combine(root, "computers.csv");
        File.WriteAllText(ManageDemoRosterPath, rosterCsv);
        _manageProbe = probe;
        _manageRunner = runner;
        _manageLauncher = launcher;
    }

    /// <summary>Build Manage's services from the configuration. Called from the constructor.</summary>
    private void InitializeManage(FleetMateConfig config)
    {
        ManageConfig = config.Manage ?? new ManageConfig();
        ManageState = new ManageStateStore();
        // Its own client until the Devices tab's ReportMate service is on main to share.
        if (!string.IsNullOrEmpty(config.ReportMateUrl))
        {
            try
            {
                _manageReportMate = ReportMateService.FromConfig(config);
                _manageDirectory = new ReportMateDeviceDirectory(_manageReportMate);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ReportMate could not be set up for Manage");
            }
        }
        if (ManageConfig.HasSshKey)
        {
            try
            {
                SecureShell = new SecureShellService(ManageConfig.ToSecureShellConfig());
                _manageRunner = new SecureShellRemoteRunner(SecureShell);
                Log.Information("SecureShellService initialized for Manage (user {User})", ManageConfig.ResolvedSshUser);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "SecureShellService could not load the key at {Path}", ManageConfig.ResolvedSshKeyPath);
            }
        }
    }

    private void DisposeManage()
    {
        _manageReportMate?.Dispose();
        SecureShell?.Dispose();
    }
}
