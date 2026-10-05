using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services.Devices;
using Serilog;

namespace FleetMate.Core.Services;

/// <summary>
/// Graph calls the CLI's device lifecycle commands need (macOS parity):
/// resolving a device, a wipe with per-platform options, releasing an
/// Autopilot registration, and disabling an Entra device object.
/// </summary>
public partial class GraphService : IDeviceLifecycleGraph
{
    /// <summary>
    /// Resolve the device a destructive command will act on: a managedDevice id
    /// taken only as a GUID, or a serial matched exactly with eq. Zero or
    /// several matches are refused, never guessed at, and there is no
    /// fallback from serial to device name.
    /// </summary>
    public async Task<DeviceResolution> ResolveManagedDeviceAsync(string identifier)
    {
        var trimmed = identifier.Trim();
        if (CliTargets.IsGuid(trimmed))
            return await GetDeviceByIdAsync(trimmed) is { } byId ? DeviceResolution.One(byId) : DeviceResolution.None();
        if (CliTargets.SerialFilter(trimmed) is not { } filter)
            return DeviceResolution.Refused(CliTargets.RefusedIdentifier);

        var matches = await GetManagedDevicesAsync(filter, 5);
        return matches.Count switch
        {
            0 => DeviceResolution.None(),
            1 => DeviceResolution.One(matches[0]),
            _ => DeviceResolution.Many(matches),
        };
    }

    /// <summary>
    /// Every Autopilot registration whose serial is exactly <paramref name="serial"/>.
    /// Graph rejects eq on this resource (it answers 500), so the server-side
    /// filter is contains() and exactness is enforced here: a near match is
    /// never returned, which matters when the result is about to be deleted.
    /// </summary>
    public async Task<List<AutopilotDevice>> FindAutopilotRegistrationsAsync(string serial)
    {
        var trimmed = serial.Trim();
        if (!CliTargets.IsSerial(trimmed)) return new();
        var candidates = await GetAutopilotDevicesAsync($"contains(serialNumber,{CliTargets.Literal(trimmed)})", 25);
        return candidates.Where(d => string.Equals(d.SerialNumber?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The one Autopilot registration for an exact serial; null when there is none or more than one.</summary>
    public async Task<AutopilotDevice?> FindAutopilotRegistrationAsync(string serial)
    {
        var matches = await FindAutopilotRegistrationsAsync(serial);
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>An Autopilot id taken only as a GUID; anything else must be an exact, unique serial.</summary>
    public async Task<string?> ResolveAutopilotIdAsync(string identifier) =>
        CliTargets.IsGuid(identifier) ? identifier.Trim() : (await FindAutopilotRegistrationAsync(identifier))?.Id;

    /// <summary>Wipe with the options the device's platform accepts.</summary>
    public async Task<DeviceActionResult> WipeDeviceAsync(IntuneDevice device, WipeOptions options, bool confirmed)
    {
        var platform = DevicePlatforms.From(device.OperatingSystem);
        return await PostLifecycleAsync(device.Id, "wipe", confirmed, $"deviceManagement/managedDevices/{device.Id}/wipe",
            options.RequestBody(platform).ToJsonString());
    }

    /// <summary>Delete an Autopilot registration, releasing the hardware hash so the device can be registered elsewhere.</summary>
    public async Task<DeviceActionResult> DeleteAutopilotRegistrationAsync(string autopilotId, bool confirmed)
    {
        var guard = RequireConfirmation(confirmed, "deleteAutopilotRegistration", autopilotId);
        if (guard != null) return guard;
        if (!await SetAuthorizationAsync()) return Result(autopilotId, "deleteAutopilotRegistration", false, "Not authenticated");
        try
        {
            var response = await _client.DeleteAsync($"deviceManagement/windowsAutopilotDeviceIdentities/{autopilotId}");
            if (response.IsSuccessStatusCode)
            {
                Log.Information("Deleted Autopilot registration {Id}", autopilotId);
                return Result(autopilotId, "deleteAutopilotRegistration", true);
            }
            return Result(autopilotId, "deleteAutopilotRegistration", false, await ReadErrorBodyAsync(response));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete Autopilot registration {Id}", autopilotId);
            return Result(autopilotId, "deleteAutopilotRegistration", false, ex.Message);
        }
    }

    /// <summary>Enable or disable an Entra device object (PATCH devices/{objectId} accountEnabled).</summary>
    public async Task<DeviceActionResult> SetEntraDeviceEnabledAsync(string objectId, bool enabled, bool confirmed)
    {
        var action = enabled ? "enableEntraDevice" : "disableEntraDevice";
        var guard = RequireConfirmation(confirmed, action, objectId);
        if (guard != null) return guard;
        if (!await SetAuthorizationAsync()) return Result(objectId, action, false, "Not authenticated");
        try
        {
            var content = new StringContent(JsonSerializer.Serialize(new { accountEnabled = enabled }), Encoding.UTF8, "application/json");
            var response = await _client.PatchAsync($"devices/{objectId}", content);
            if (response.IsSuccessStatusCode) return Result(objectId, action, true);
            return Result(objectId, action, false, await ReadErrorBodyAsync(response));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to {Action} {Id}", action, objectId);
            return Result(objectId, action, false, ex.Message);
        }
    }

    /// <summary>Assign a user to an Autopilot registration for the out-of-box experience.</summary>
    public Task<DeviceActionResult> AssignAutopilotRegistrationUserAsync(string autopilotId, string userPrincipalName, string? displayName) =>
        PostLifecycleAsync(autopilotId, "assignUserToDevice", confirmed: true,
            $"deviceManagement/windowsAutopilotDeviceIdentities/{autopilotId}/assignUserToDevice",
            JsonSerializer.Serialize(new { userPrincipalName, addressableUserName = displayName ?? userPrincipalName }));

    /// <summary>Remove the assigned user from an Autopilot registration.</summary>
    public Task<DeviceActionResult> UnassignAutopilotRegistrationUserAsync(string autopilotId) =>
        PostLifecycleAsync(autopilotId, "unassignUserFromDevice", confirmed: true,
            $"deviceManagement/windowsAutopilotDeviceIdentities/{autopilotId}/unassignUserFromDevice", null);

    private async Task<DeviceActionResult> PostLifecycleAsync(string id, string action, bool confirmed, string url, string? body)
    {
        var guard = RequireConfirmation(confirmed, action, id);
        if (guard != null) return guard;
        if (!await SetAuthorizationAsync()) return Result(id, action, false, "Not authenticated");
        try
        {
            var content = body == null ? null : new StringContent(body, Encoding.UTF8, "application/json");
            var response = await _client.PostAsync(url, content);
            if (response.IsSuccessStatusCode)
            {
                Log.Information("{Action} sent for {Id}", action, id);
                return Result(id, action, true);
            }
            return Result(id, action, false, await ReadErrorBodyAsync(response));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Action} failed for {Id}", action, id);
            return Result(id, action, false, ex.Message);
        }
    }

    private static DeviceActionResult Result(string id, string action, bool success, string? message = null) =>
        new() { Success = success, DeviceId = id, Action = action, Message = message };
}
