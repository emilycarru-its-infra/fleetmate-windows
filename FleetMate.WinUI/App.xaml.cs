using Microsoft.UI.Xaml;
using FleetMate.Core.Config;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Services.Tickets;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Reporting;
using Microsoft.Win32;

namespace FleetMate.WinUI;

/// <summary>
/// Application bootstrap. Loads config and constructs the framework-agnostic
/// FleetMate.Core services (mirrors the WPF App's InitializeServices), then shows
/// the shell window. Pages reach services via <see cref="App.Current"/>.
/// </summary>
public partial class App : Application
{
    public static new App Current => (App)Application.Current;
    public static MainWindow Window { get; private set; } = null!;

    public FleetMateConfig Config { get; private set; } = null!;
    public AuthManager AuthManager { get; private set; } = null!;
    public GraphService? GraphService { get; private set; }
    public SnipeService? SnipeService { get; private set; }
    public TdxService? TdxService { get; private set; }
    public AzureDevOpsService? DevOpsService { get; private set; }
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
            Config = FleetMateConfig.LoadDesktop();
            InitializeServices();
            AuthManager = new AuthManager(Config);

            Window = new MainWindow();
            using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\FleetMate"))
            {
                if (Window.Content is FrameworkElement root)
                    root.RequestedTheme = key?.GetValue("UiTheme")?.ToString() switch
                    {
                        "Light" => ElementTheme.Light,
                        "Dark" => ElementTheme.Dark,
                        _ => ElementTheme.Default
                    };
            }
            Window.Activate();
            EntraTokenSource.ParentWindowProvider = () => WinRT.Interop.WindowNative.GetWindowHandle(Window);
        }
        catch (Exception ex)
        {
            Program.Log($"CRASH in OnLaunched: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
            throw;
        }
    }

    /// <summary>
    /// Reload config from the registry and rebuild the Core services — called after the
    /// onboarding wizard saves, so new settings apply without a restart. Re-navigate the
    /// shell afterwards to pick up the fresh services.
    /// </summary>
    public void ReloadConfigAndServices()
    {
        GraphService?.Dispose();
        SnipeService?.Dispose();
        TdxService?.Dispose();
        DevOpsService?.Dispose();
        ReportMateService?.Dispose();
        GraphService = null;
        SnipeService = null;
        TdxService = null;
        DevOpsService = null;
        ReportMateService = null;

        Config = FleetMateConfig.LoadDesktop();
        InitializeServices();
        AuthManager = new AuthManager(Config);
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
                GraphService = new GraphService(Config.Graph, Config.Elevation);

            if (!string.IsNullOrEmpty(Config.SnipeUrl))
                SnipeService = FleetMate.Core.Services.Inventory.SnipeService.FromConfig(Config);

            if (Config.Tdx != null && !string.IsNullOrEmpty(Config.Tdx.BaseUrl))
                TdxService = new TdxService(Config.Tdx);

            if (Config.AzureDevOps != null && !string.IsNullOrEmpty(Config.AzureDevOps.Organization))
                DevOpsService = new AzureDevOpsService(Config.AzureDevOps);

            if (!string.IsNullOrEmpty(Config.ReportMateUrl))
                ReportMateService = FleetMate.Core.Services.Reporting.ReportMateService.FromConfig(Config);
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
