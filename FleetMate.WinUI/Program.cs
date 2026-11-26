using FleetMate.Core.Config;
using FleetMate.Core.Links;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Serilog;

namespace FleetMate.WinUI;

/// <summary>
/// The entry point, ahead of XAML: logging, then one window per person and
/// edition. A second launch (a <c>fleetmate:</c> or <c>ticketsmate:</c> link
/// opened from a browser or chat) hands its link to the running app through
/// <see cref="LinkHost"/> and exits before any window is created.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        ShellLogging.Configure();

        // Its own lock while FleetMate.GUI ships, so the two never hand off to each other.
        LinkHost.Instance = "WinUI";
        var link = LinkHost.LinkFromArgs(args);
        // A QA capture runs beside an open window, so it takes no lock.
        var capturing = Shell.CaptureHarness.DirectoryFromArgs(args) != null;
        if (!capturing && !LinkHost.TryBecomePrimary(link))
        {
            Log.CloseAndFlush();
            return 0;
        }

        Log.Information("{Edition} WinUI starting (pid {Pid}, version {Version})",
            AppEdition.Current.Name, Environment.ProcessId, typeof(Program).Assembly.GetName().Version);

        Application.Start(callback =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(link);
        });

        LinkHost.Stop();
        Log.CloseAndFlush();
        return Environment.ExitCode;
    }
}

/// <summary>Serilog for the WinUI shell, beside the WPF app's log in the edition's log folder.</summary>
internal static class ShellLogging
{
    /// <summary>
    /// The WinUI shell's log file. While both apps ship it is its own file, so
    /// a report from one is never read as the other's; at cutover it becomes debug.log.
    /// </summary>
    public const string FileName = "winui.log";

    public static void Configure()
    {
        var logDir = AppEdition.Current.LogDirectory;
        Directory.CreateDirectory(logDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Debug()
            .WriteTo.File(Path.Combine(logDir, FileName),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // Record every failure that would otherwise leave no trace.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception (terminating={IsTerminating})", e.IsTerminating);
            Log.CloseAndFlush();
        };
    }
}
