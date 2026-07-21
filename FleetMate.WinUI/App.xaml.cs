using Microsoft.UI.Xaml;
using FleetMate.Core.Config;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Services.Tickets;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Reporting;

namespace FleetMate.WinUI;

/// <summary>
/// Application bootstrap. Loads config and constructs the framework-agnostic
/// FleetMate.Core services (mirrors the WPF App's InitializeServices), then shows
/// the shell window. Pages reach services via <see cref="App.Current"/>.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public static new App Current => (App)Application.Current;

    public FleetMateConfig Config { get; private set; } = null!;
    public GraphService? GraphService { get; private set; }
    public SnipeService? SnipeService { get; private set; }
    public TdxService? TdxService { get; private set; }
    public AzureDevOpsService? DevOpsService { get; private set; }
    public DevOpsSsoService? DevOpsSsoService { get; private set; }
    public ReportMateService? ReportMateService { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Config = FleetMateConfig.Load();
            InitializeServices();

            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            Program.Log($"CRASH in OnLaunched: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
            throw;
        }
    }

    /// <summary>
    /// Construct Core services from config. Each is optional — only created when its
    /// dependency is configured, so an unconfigured box still launches to an empty shell.
    /// </summary>
    private void InitializeServices()
    {
        try
        {
            if (Config.Graph != null && !string.IsNullOrEmpty(Config.Graph.TenantId))
                GraphService = new GraphService(Config.Graph);

            if (!string.IsNullOrEmpty(Config.SnipeUrl) && !string.IsNullOrEmpty(Config.SnipeApiKey))
                SnipeService = new SnipeService(Config.SnipeUrl, Config.SnipeApiKey, Config.CacheMinutes);

            if (Config.Tdx != null && !string.IsNullOrEmpty(Config.Tdx.BaseUrl))
                TdxService = new TdxService(Config.Tdx);

            if (Config.AzureDevOps != null && !string.IsNullOrEmpty(Config.AzureDevOps.Organization))
            {
                DevOpsService = new AzureDevOpsService(Config.AzureDevOps);
                if (!string.IsNullOrEmpty(Config.AzureDevOps.ClientId) && !string.IsNullOrEmpty(Config.AzureDevOps.TenantId))
                    DevOpsSsoService = new DevOpsSsoService(Config.AzureDevOps.ClientId, Config.AzureDevOps.TenantId);
            }

            if (!string.IsNullOrEmpty(Config.ReportMateUrl))
                ReportMateService = new ReportMateService(Config.ReportMateUrl, Config.ReportMatePassphrase, Config.CacheMinutes);
        }
        catch (Exception ex)
        {
            Program.Log($"InitializeServices failed: {ex.Message}");
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Program.Log($"UNHANDLED: {e.Exception.GetType().Name}: {e.Exception.Message}\n{e.Exception.StackTrace}\n");
        e.Handled = true;
    }
}
