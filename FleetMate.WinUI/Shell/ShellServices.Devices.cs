using FleetMate.Core.Config;
using FleetMate.Core.Devices;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using FleetMate.Core.Services.Reporting;
using Serilog;

namespace FleetMate.WinUI.Shell;

/// <summary>
/// The Devices tab's services, following FleetMate.GUI's App: Intune and
/// Autopilot through the shared <see cref="Graph"/>, ReportMate for
/// the Errors by Category card, the Intune device cache preloaded at launch,
/// and the Autopilot identities and Apple organizations the page joins to it.
///
/// <code>
/// App.Services.CacheChanged += key => { if (key == "Devices") Render(); };
/// var devices = App.Services.CachedDevices;
/// </code>
/// </summary>
public sealed partial class ShellServices
{
    private DateTime? _devicesCacheTime;
    private readonly Dictionary<string, AppleOrgService> _appleServices = new(StringComparer.Ordinal);

    /// <summary>ReportMate: null when no URL is configured, and always in a demo.</summary>
    public ReportMateService? ReportMateService { get; private set; }

    /// <summary>What the device detail panel and compliance lightbox read on selection: Graph, or fixtures in a demo.</summary>
    public IDeviceDetailSource? DeviceDetails { get; private set; }

    /// <summary>Whether the Devices list can load at all: Graph is set up, or a demo is serving fixtures.</summary>
    public bool DevicesConfigured => Graph != null || (DemoMode && CachedDevices.Count > 0);

    /// <summary>Intune's managed devices, the whole fleet once loaded.</summary>
    public List<IntuneDevice> CachedDevices { get; private set; } = new();

    /// <summary>Autopilot identities, joined to <see cref="CachedDevices"/> by serial.</summary>
    public List<AutopilotDevice> CachedAutopilot { get; set; } = new();

    /// <summary>Every configured Apple organization's devices and services.</summary>
    public List<AppleOrgSnapshot> CachedAppleOrgs { get; set; } = new();

    /// <summary>ReportMate's install errors for the Errors by Category card; null until read, or when ReportMate is absent.</summary>
    public DeviceErrorFigures? DeviceErrors { get; private set; }

    /// <summary>The device cache is younger than the configured cache time.</summary>
    public bool IsDevicesCacheValid => DemoMode
        || (_devicesCacheTime is { } t && DateTime.Now - t < TimeSpan.FromMinutes(Config.CacheMinutes));

    public void UpdateDevicesCache(List<IntuneDevice> devices)
    {
        CachedDevices = devices;
        _devicesCacheTime = DateTime.Now;
        NotifyCacheChanged("Devices");
    }

    /// <summary>Forget the device cache, so the next load reads Intune again.</summary>
    public void InvalidateDevices()
    {
        if (DemoMode) return;
        _devicesCacheTime = null;
        CachedDevices = new();
        CachedAutopilot = new();
        CachedAppleOrgs = new();
    }

    /// <summary>A device a <c>fleetmate:</c> link or search result asked for, waiting for the Devices page.</summary>
    public string? PendingDeviceId { get; set; }

    /// <summary>Raised after a device link is queued, for a Devices page that is already showing.</summary>
    public event Action? DeviceLinkRequested;

    public void RequestDeviceLink(string intuneId)
    {
        PendingDeviceId = intuneId;
        DeviceLinkRequested?.Invoke();
    }

    /// <summary>
    /// Enrollment records (Autopilot identities and Apple organizations) are
    /// read only while the Enrollment module is on; switched off, Devices
    /// lists management records alone.
    /// </summary>
    public static bool DeviceEnrollmentOn => AppModules.IsOn(UserPreferences.HiddenModules, AppModules.Enrollment);

    /// <summary>The Apple organization connection for a profile, kept for the session; null in a demo.</summary>
    public AppleOrgService? AppleService(string profileName)
    {
        if (DemoMode) return null;
        lock (_appleServices)
        {
            if (_appleServices.TryGetValue(profileName, out var cached)) return cached;
            var service = AppleOrgService.Connect(profileName);
            if (service != null) _appleServices[profileName] = service;
            return service;
        }
    }

    // MARK: - Demo mode

    /// <summary>Demo mode's fixture (registered in App): fictional devices, identities, an Apple organization and install errors.</summary>
    public void SeedDevicesDemo()
    {
        var now = DateTime.UtcNow;
        CachedDevices = DeviceDemoData.Devices(now);
        CachedAutopilot = DeviceDemoData.Autopilot(now);
        CachedAppleOrgs = DeviceDemoData.AppleOrgs(now);
        DeviceErrors = DeviceDemoData.Errors();
        DeviceDetails = DeviceDemoData.DetailSource;
        _devicesCacheTime = DateTime.Now;
    }

    // MARK: - Lifetime

    /// <summary>Build Devices' services from the configuration. Called from the constructor, on the UI thread.</summary>
    private void InitializeDevices(FleetMateConfig config)
    {
        try
        {
            // Graph is built once, by Identity (InitializeIdentity runs first), and shared.
            if (Graph is { } graph) DeviceDetails = new GraphDeviceDetailSource(graph);
            if (!string.IsNullOrEmpty(config.ReportMateUrl))
            {
                ReportMateService = ReportMateService.FromConfig(config);
                Log.Information("ReportMateService initialized ({Auth})", ReportMateService.UsesOidc ? "Entra SSO" : "legacy passphrase");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Devices services could not be set up");
        }
    }

    /// <summary>Preload the whole fleet at launch, as FleetMate.GUI does, so the tab opens on data.</summary>
    private void StartDevices()
    {
        if (Graph is { } graph) _ = PreloadDevicesAsync(graph);
    }

    private async Task PreloadDevicesAsync(GraphService graph)
    {
        if (IsDevicesCacheValid && CachedDevices.Count > 0) return;
        using var loading = TrackCacheLoad("Devices");
        try
        {
            // The whole fleet: the list trusts this cache, so a first page alone
            // would leave every other device looking unenrolled once Autopilot is joined.
            var devices = await Task.Run(() => graph.GetManagedDevicesAsync(limit: 10000));
            PostToUi(() => UpdateDevicesCache(devices));
            Log.Information("Preloaded {Count} devices", devices.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to preload devices");
        }
    }

    private bool _loadingDeviceErrors;

    /// <summary>
    /// Read ReportMate's install errors once for the Errors by Category card;
    /// the card redraws from <c>CacheChanged("Devices")</c> when they arrive.
    /// </summary>
    public void EnsureDeviceErrors()
    {
        if (DemoMode || ReportMateService is not { } reportMate || DeviceErrors is { Loading: false } || _loadingDeviceErrors) return;
        _loadingDeviceErrors = true;
        DeviceErrors = new DeviceErrorFigures(null, null, Loading: true);
        _ = Task.Run(async () =>
        {
            DeviceErrorFigures result;
            try
            {
                var devices = reportMate.GetDevicesAsync();
                var errors = reportMate.GetErrorsByItemAsync(false);
                await Task.WhenAll(devices, errors);
                result = new DeviceErrorFigures(devices.Result.Count, errors.Result, Loading: false);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[devices] ReportMate errors unavailable");
                result = new DeviceErrorFigures(0, new List<Core.Models.Reporting.ErrorSummary>(), Loading: false);
            }
            PostToUi(() =>
            {
                DeviceErrors = result;
                _loadingDeviceErrors = false;
                NotifyCacheChanged("Devices");
            });
        });
    }

    private void DisposeDevices()
    {
        // Graph belongs to Identity, which disposes it.
        ReportMateService?.Dispose();
    }
}
