using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;

namespace FleetMate.Core.Providers.Devices;

/// <summary>
/// Wraps GraphService to expose Intune devices through the unified IManagementProvider interface.
/// </summary>
public class IntuneManagementProvider : IManagementProvider
{
    private readonly GraphService _graphService;

    public string ProviderId => "intune";
    public string ProviderName => "Microsoft Intune";
    public bool IsEnabled => true;

    public IntuneManagementProvider(GraphService graphService)
    {
        _graphService = graphService;
    }

    public Task<bool> AuthenticateAsync(CancellationToken ct = default) =>
        Task.FromResult(true); // Graph handles its own auth

    public async Task<List<UnifiedManagedDevice>> ListDevicesAsync(DeviceFilter? filter = null, CancellationToken ct = default)
    {
        var devices = filter?.SearchQuery != null
            ? await _graphService.SearchDevicesAsync(filter.SearchQuery)
            : await _graphService.GetManagedDevicesAsync();

        return devices.Select(ToUnified).ToList();
    }

    public async Task<UnifiedManagedDevice?> GetDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        var device = await _graphService.GetDeviceByIdAsync(deviceId);
        return device != null ? ToUnified(device) : null;
    }

    public async Task<UnifiedManagedDevice?> GetDeviceBySerialAsync(string serial, CancellationToken ct = default)
    {
        var device = await _graphService.GetDeviceBySerialAsync(serial);
        return device != null ? ToUnified(device) : null;
    }

    public async Task<List<UnifiedManagedDevice>> SearchDevicesAsync(string query, CancellationToken ct = default)
    {
        var devices = await _graphService.SearchDevicesAsync(query);
        return devices.Select(ToUnified).ToList();
    }

    public async Task SyncDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        await _graphService.SyncDeviceAsync(deviceId);
    }

    private static UnifiedManagedDevice ToUnified(IntuneDevice d) => new()
    {
        Id = d.Id,
        ProviderId = "intune",
        DeviceName = d.DeviceName,
        SerialNumber = d.SerialNumber,
        OperatingSystem = d.OperatingSystem,
        OsVersion = d.OsVersion,
        Model = d.Model,
        Manufacturer = d.Manufacturer,
        UserPrincipalName = d.UserPrincipalName,
        ComplianceState = d.ComplianceState,
        ManagementState = d.ManagementState,
        LastSyncDateTime = d.LastSyncDateTime,
        EnrolledDateTime = d.EnrolledDateTime,
        EntraDeviceId = d.AzureAdDeviceId,
        TotalStorageSpaceInBytes = d.TotalStorageSpaceInBytes,
        FreeStorageSpaceInBytes = d.FreeStorageSpaceInBytes,
        WiFiMacAddress = d.WiFiMacAddress,
        EthernetMacAddress = d.EthernetMacAddress,
    };
}
