using FleetMate.Core.Config;
using FleetMate.Core.Links;
using FleetMate.WinUI.Pages;
using FleetMate.WinUI.Pages.Tickets;
using FleetMate.WinUI.Shell;
using Microsoft.UI.Xaml;
using Serilog;

namespace FleetMate.WinUI;

/// <summary>
/// The WinUI application: configuration, theme, services, links, then the
/// window. <see cref="Program.Main"/> has already configured logging and
/// taken the single-instance lock.
/// </summary>
public partial class App : Application
{
    private readonly string? _startupLink;
    private MainWindow? _window;

    /// <summary>The services every page reads; see <see cref="ShellServices"/>.</summary>
    public static ShellServices Services { get; private set; } = null!;

    public static new App Current => (App)Application.Current;

    /// <summary>The one window, once launched.</summary>
    public MainWindow? Window => _window;

    public App(string? startupLink)
    {
        _startupLink = startupLink;

        // Application.RequestedTheme can only be set before the first window,
        // and setting it here also themes the caption buttons. `--theme
        // light|dark` overrides the saved choice, for screenshots and checks.
        if ((ThemeArgument() ?? UserPreferences.UiTheme) is { } theme)
            RequestedTheme = theme == "Dark" ? ApplicationTheme.Dark : ApplicationTheme.Light;

        InitializeComponent();

        UnhandledException += (_, e) =>
        {
            Log.Fatal(e.Exception, "Unhandled exception on the UI thread");
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
            _window?.DispatcherQueue.TryEnqueue(() =>
                Services?.ReportAppError(e.Exception.InnerException?.Message ?? e.Exception.Message));
        };

        // Each ported page registers here; every other tab shows a placeholder.
        ShellPages.Register("Inventory", typeof(Pages.Inventory.InventoryPage));
        Shell.Widgets.WidgetStrip.RegisterTab("Inventory");
        ShellServices.RegisterDemoFixtures(s => s.SeedInventoryDemo());
        ShellPages.Register("Tickets", typeof(TicketsPage));
        Shell.Widgets.WidgetStrip.RegisterTab("Tickets");
        ShellServices.RegisterDemoFixtures(s => s.TicketCache.Update(DemoTickets.Tickets.ToList()));
        ShellPages.Register("Development", typeof(Pages.Development.DevelopmentPage));
        Shell.Widgets.WidgetStrip.RegisterTab("Development");
        ShellServices.RegisterDemoFixtures(s => s.SeedDevelopmentDemo());
        ShellPages.Register("Identity", typeof(Pages.Identity.IdentityPage));
        ShellServices.RegisterDemoFixtures(s => s.Identity = new Pages.Identity.IdentityDemo());
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // The config file, then HKLM and HKCU policy for this edition;
        // TicketsMate keeps TeamDynamix and drops every other module.
        // Demo mode reads no configuration at all, so a capture can never
        // reach a live service whatever is set up on the machine.
        var demo = DemoMode.IsRequested();
        Services = demo
            ? new ShellServices(new FleetMateConfig(), demoMode: true)
            : new ShellServices(FleetMateConfig.LoadDesktop());

        _window = new MainWindow();
        _window.Closed += (_, _) =>
        {
            Services.Dispose();
            Exit();
        };
        _window.Activate();

        // `--capture <dir>`: the per-panel QA run, which exits when done. It
        // renders the sample and placeholder content and signs in to nothing.
        if (CaptureHarness.DirectoryFromArgs(Environment.GetCommandLineArgs()) is { } captureDir)
        {
            _window.DispatcherQueue.TryEnqueue(async () => await CaptureHarness.RunAsync(_window, captureDir));
            return;
        }

        LinkHost.Listen(message => _window.DispatcherQueue.TryEnqueue(() => _window.OpenLink(message)));
        // The fleetmate: and ticketsmate: protocols stay with FleetMate.GUI, the
        // app that ships, until cutover; a link passed on the command line still opens.
        if (_startupLink != null) _window.DispatcherQueue.TryEnqueue(() => _window.OpenLink(_startupLink));

        Services.Start();
    }

    private static string? ThemeArgument()
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.FindIndex(args, a => a.Equals("--theme", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length) return null;
        return args[i + 1].ToLowerInvariant() switch { "dark" => "Dark", "light" => "Light", _ => null };
    }
}
