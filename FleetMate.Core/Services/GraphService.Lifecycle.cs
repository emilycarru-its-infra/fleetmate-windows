using System.Text;
using System.Text.Json;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services.Devices;
using Serilog;

namespace FleetMate.Core.Services;

/// <summary>Per-platform wipe options and the offboard pass across Intune, Autopilot and Entra.</summary>
public partial class GraphService : IDeviceLifecycleClient
{
    /// <summary>A GraphService over a given transport, for tests; it attaches no token.</summary>
    internal GraphService(Config.GraphConfig config, HttpMessageHandler handler)
    {
        _config = config;
        _cacheDuration = TimeSpan.FromMinutes(config.CacheMinutes);
        _useElevation = true;
        _client = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    /// <summary>Wipe a device with the options its platform accepts.</summary>
    public async Task<DeviceActionResult> WipeDeviceAsync(string deviceId, WipeOptions options, DevicePlatform platform, bool confirmed = false)
    {
        var guard = RequireConfirmation(confirmed, "wipe", deviceId);
        if (guard != null) return guard;
        if (!await SetAuthorizationAsync()) return Failed(deviceId, "wipe", "Not authenticated");

        try
        {
            var body = JsonSerializer.Serialize(options.RequestBody(platform));
            var response = await _client.PostAsync($"deviceManagement/managedDevices/{deviceId}/wipe",
                new StringContent(body, Encoding.UTF8, "application/json"));
            if (response.IsSuccessStatusCode)
            {
                Log.Information("Wipe triggered for device {DeviceId} ({Platform})", deviceId, platform);
                return new DeviceActionResult { Success = true, DeviceId = deviceId, Action = "wipe" };
            }
            return Failed(deviceId, "wipe", await ReadErrorBodyAsync(response));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to wipe device {DeviceId}", deviceId);
            return Failed(deviceId, "wipe", ex.Message);
        }
    }

    /// <summary>Wipe several devices, each with the body its own platform accepts.</summary>
    public async Task<List<DeviceActionResult>> WipeDevicesAsync(IEnumerable<IntuneDevice> devices, WipeOptions options, bool confirmed = false)
    {
        var results = new List<DeviceActionResult>();
        foreach (var device in devices)
            results.Add(await WipeDeviceAsync(device.Id, options, device.Platform(), confirmed));
        return results;
    }

    /// <summary>
    /// Enable or disable an Entra device object by object id. A disabled device
    /// cannot obtain tokens, which blocks conditional-access resources at once
    /// without destroying the record.
    /// </summary>
    public async Task<DeviceActionResult> SetEntraDeviceEnabledAsync(string objectId, bool enabled)
    {
        var action = enabled ? "enableEntraDevice" : "disableEntraDevice";
        if (!await SetAuthorizationAsync()) return Failed(objectId, action, "Not authenticated");
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Patch, $"devices/{objectId}")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { accountEnabled = enabled }), Encoding.UTF8, "application/json")
            };
            var response = await _client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                Log.Information("{Action} {ObjectId}", action, objectId);
                return new DeviceActionResult { Success = true, DeviceId = objectId, Action = action };
            }
            return Failed(objectId, action, await ReadErrorBodyAsync(response));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to {Action} {ObjectId}", action, objectId);
            return Failed(objectId, action, ex.Message);
        }
    }

    /// <summary>
    /// Decommission one device across Intune, Autopilot and Entra. Destructive:
    /// the caller has confirmed with the person, and says so.
    /// </summary>
    public Task<OffboardResult> OffboardDeviceAsync(IntuneDevice device, OffboardPlan plan,
        AutopilotDevice? knownAutopilot = null, bool confirmed = false)
    {
        if (!confirmed)
        {
            Log.Warning("Refused unconfirmed offboard for {DeviceId}", device.Id);
            return Task.FromResult(new OffboardResult(device.SerialNumber ?? device.Id, device.DeviceName, device.Platform(),
                new[] { new OffboardStepResult("Offboard", OffboardStepResult.StepOutcome.Failed,
                    "Confirmation required: offboard must be invoked with confirmed: true.") }));
        }
        return new DeviceOffboarder(this).OffboardAsync(device, plan, knownAutopilot);
    }

    // The decommission steps run already-confirmed: OffboardDeviceAsync is the gate.
    Task<DeviceActionResult> IDeviceLifecycleClient.WipeAsync(IntuneDevice device, WipeOptions options) =>
        WipeDeviceAsync(device.Id, options, device.Platform(), confirmed: true);
    Task<DeviceActionResult> IDeviceLifecycleClient.RetireAsync(string deviceId) =>
        RetireDeviceAsync(deviceId, confirmed: true);
    Task<AutopilotDevice?> IDeviceLifecycleClient.FindAutopilotBySerialAsync(string serialNumber) =>
        GetAutopilotDeviceBySerialAsync(serialNumber);
    Task<DeviceActionResult> IDeviceLifecycleClient.DeleteAutopilotAsync(string autopilotId) =>
        DeleteAutopilotIdentityAsync(autopilotId, confirmed: true);
    Task<EntraDevice?> IDeviceLifecycleClient.FindEntraDeviceAsync(string deviceId) =>
        GetEntraDeviceByDeviceIdAsync(deviceId);
    Task<DeviceActionResult> IDeviceLifecycleClient.DeleteEntraDeviceAsync(string objectId) =>
        DeleteEntraDeviceAsync(objectId, confirmed: true);
    Task<DeviceActionResult> IDeviceLifecycleClient.SetEntraDeviceEnabledAsync(string objectId, bool enabled) =>
        SetEntraDeviceEnabledAsync(objectId, enabled);
    Task<DeviceActionResult> IDeviceLifecycleClient.DeleteManagedDeviceAsync(string deviceId) =>
        DeleteManagedDeviceAsync(deviceId, confirmed: true);
}
