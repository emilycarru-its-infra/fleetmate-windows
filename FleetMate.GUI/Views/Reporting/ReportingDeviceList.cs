using System.Windows;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Reporting;
using Serilog;
using RmApi = ReportMate.App.Services.FleetApiClient;
using RmDevice = ReportMate.App.Services.FleetDevice;

namespace FleetMate.GUI.Views.Reporting;

/// <summary>
/// ReportMate's device list for global search, read through the same API
/// client and connection the Reporting tab uses, so its devices can be found
/// before that tab has ever been opened (macOS parity).
/// </summary>
public static class ReportingDeviceList
{
    /// <summary>A loaded list is reused for five minutes, as the dashboard does.</summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);

    /// <summary>A failed load is not retried on every keystroke.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    private static Task? _loading;
    private static DateTime? _loadedAt;
    private static DateTime? _attemptedAt;

    public static IReadOnlyList<ReportingDevice> Devices { get; private set; } = Array.Empty<ReportingDevice>();

    /// <summary>
    /// Whether a load would fetch: ReportMate is set up, and the list was never
    /// loaded or is stale, and was not just tried.
    /// </summary>
    public static bool NeedsLoad =>
        Application.Current is App app
        && AppModules.IsConfigured("Reporting", app.Config)
        && Stale(_loadedAt, _attemptedAt, DateTime.UtcNow);

    internal static bool Stale(DateTime? loadedAt, DateTime? attemptedAt, DateTime now) =>
        (loadedAt is not { } loaded || now - loaded >= Fresh)
        && (attemptedAt is not { } tried || now - tried >= RetryAfter);

    /// <summary>
    /// Load the list unless a fresh copy is held. Does nothing when FleetMate
    /// has no ReportMate connection; concurrent callers share one fetch.
    /// </summary>
    public static Task LoadAsync()
    {
        if (_loading is { } running) return running;
        if (!NeedsLoad || Application.Current is not App app) return Task.CompletedTask;
        return _loading = FetchAsync(app.Config);
    }

    private static async Task FetchAsync(FleetMateConfig config)
    {
        try
        {
            _attemptedAt = DateTime.UtcNow;
            ReportingPage.Connect(config);
            var result = await RmApi.Instance.GetDevicesAsync();
            if (!result.Ok)
            {
                Log.Information("[search] ReportMate devices not loaded: {Status}", result.Status);
                return;
            }
            Devices = result.Data!.Devices.Where(d => !d.Archived).Select(Record).ToList();
            _loadedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[search] ReportMate devices failed to load");
        }
        finally
        {
            _loading = null;
        }
    }

    /// <summary>The fields global search matches on.</summary>
    internal static ReportingDevice Record(RmDevice d) => new(
        d.SerialNumber,
        string.IsNullOrWhiteSpace(d.Name) ? d.SerialNumber : d.Name,
        d.Hostname,
        d.Owner,
        d.AssetTag ?? d.Modules?.Inventory?.AssetTag,
        d.Platform ?? d.OsName);
}
