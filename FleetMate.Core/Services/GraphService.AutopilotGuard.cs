using System.Net;
using System.Net.Http.Json;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services.Devices;
using Serilog;

namespace FleetMate.Core.Services;

/// <summary>
/// Reads for the Autopilot delete guard. Unlike <see cref="GetManagedDevicesAsync"/>,
/// which logs a failure and answers with an empty list, these report the
/// failure, because "no Intune record" is what permits the delete.
/// </summary>
public partial class GraphService : IAutopilotDeleteGraph
{
    public async Task<ManagedDeviceLookup> LookupManagedDevicesAsync(string filter)
    {
        if (!await SetAuthorizationAsync()) return ManagedDeviceLookup.Failed("not authenticated to Microsoft Graph");
        try
        {
            var response = await _client.GetAsync($"deviceManagement/managedDevices?$top=5&$filter={Uri.EscapeDataString(filter)}");
            if (!response.IsSuccessStatusCode)
                return ManagedDeviceLookup.Failed($"{(int)response.StatusCode} {await ReadErrorBodyAsync(response)}");
            var result = await response.Content.ReadFromJsonAsync<IntuneDeviceListResponse>(_jsonOptions);
            return result == null
                ? ManagedDeviceLookup.Failed("empty response")
                : ManagedDeviceLookup.Found(result.Value);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Intune lookup for the Autopilot delete guard failed");
            return ManagedDeviceLookup.Failed(ex.Message);
        }
    }

    /// <summary>
    /// One Autopilot registration by id: the device, null when Graph answers
    /// 404, or an error message for any other failure.
    /// </summary>
    public async Task<(AutopilotDevice? Device, string? Error)> GetAutopilotRegistrationAsync(string autopilotId)
    {
        if (!CliTargets.IsGuid(autopilotId)) return (null, "not an Autopilot id");
        if (!await SetAuthorizationAsync()) return (null, "not authenticated to Microsoft Graph");
        try
        {
            var response = await _client.GetAsync($"deviceManagement/windowsAutopilotDeviceIdentities/{autopilotId.Trim()}");
            if (response.StatusCode == HttpStatusCode.NotFound) return (null, null);
            if (!response.IsSuccessStatusCode)
                return (null, $"{(int)response.StatusCode} {await ReadErrorBodyAsync(response)}");
            return (await response.Content.ReadFromJsonAsync<AutopilotDevice>(_jsonOptions), null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get Autopilot registration {Id}", autopilotId);
            return (null, ex.Message);
        }
    }
}
