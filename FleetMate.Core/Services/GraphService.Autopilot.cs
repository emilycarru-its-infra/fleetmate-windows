using System.Net.Http.Json;
using FleetMate.Core.Models.Devices;
using Serilog;

namespace FleetMate.Core.Services;

/// <summary>Windows Autopilot identity actions and hardware hash import.</summary>
public partial class GraphService
{
    private const string AutopilotIdentities = "deviceManagement/windowsAutopilotDeviceIdentities";

    public Task<DeviceActionResult> SetAutopilotGroupTagAsync(string identityId, string groupTag) =>
        AutopilotPostAsync(identityId, "updateDeviceProperties", new { groupTag });

    public Task<DeviceActionResult> AssignAutopilotUserAsync(string identityId, string userPrincipalName, string? displayName = null) =>
        AutopilotPostAsync(identityId, "assignUserToDevice",
            new { userPrincipalName, addressableUserName = displayName ?? userPrincipalName });

    public Task<DeviceActionResult> UnassignAutopilotUserAsync(string identityId) =>
        AutopilotPostAsync(identityId, "unassignUserFromDevice", null);

    /// <summary>Ask Intune to sync Autopilot registrations with the Autopilot service (tenant-wide).</summary>
    public async Task<DeviceActionResult> SyncAutopilotAsync()
    {
        if (!await SetAuthorizationAsync())
            return Failed("", "autopilotSync", "Not authenticated");
        try
        {
            var response = await _client.PostAsync("deviceManagement/windowsAutopilotSettings/sync", null);
            if (response.IsSuccessStatusCode)
                return new DeviceActionResult { Success = true, Action = "autopilotSync" };
            return Failed("", "autopilotSync", await ReadErrorBodyAsync(response));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Autopilot sync failed");
            return Failed("", "autopilotSync", ex.Message);
        }
    }

    /// <summary>Delete an Autopilot identity. The device is untouched; it no longer gets an Autopilot profile.</summary>
    public async Task<DeviceActionResult> DeleteAutopilotIdentityAsync(string identityId, bool confirmed = false)
    {
        var guard = RequireConfirmation(confirmed, "deleteAutopilotIdentity", identityId);
        if (guard != null) return guard;
        if (!await SetAuthorizationAsync())
            return Failed(identityId, "deleteAutopilotIdentity", "Not authenticated");
        try
        {
            var response = await _client.DeleteAsync($"{AutopilotIdentities}/{identityId}");
            if (response.IsSuccessStatusCode)
            {
                Log.Information("Deleted Autopilot identity {Id}", identityId);
                return new DeviceActionResult { Success = true, DeviceId = identityId, Action = "deleteAutopilotIdentity" };
            }
            return Failed(identityId, "deleteAutopilotIdentity", await ReadErrorBodyAsync(response));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete Autopilot identity {Id}", identityId);
            return Failed(identityId, "deleteAutopilotIdentity", ex.Message);
        }
    }

    /// <summary>
    /// Upload hardware hashes. Intune registers them asynchronously; the
    /// returned identities carry each one's import state to poll.
    /// </summary>
    public async Task<List<ImportedAutopilotIdentity>> ImportAutopilotHashesAsync(IEnumerable<AutopilotHashEntry> entries)
    {
        if (!await SetAuthorizationAsync())
            throw new InvalidOperationException("Not authenticated to Microsoft Graph.");

        var body = new
        {
            importedWindowsAutopilotDeviceIdentities = entries.Select(e => new
            {
                serialNumber = e.SerialNumber,
                productKey = e.ProductKey ?? "",
                hardwareIdentifier = e.HardwareHash,
                groupTag = e.GroupTag ?? "",
                assignedUserPrincipalName = e.AssignedUser ?? "",
            }).ToList()
        };
        var response = await _client.PostAsJsonAsync("deviceManagement/importedWindowsAutopilotDeviceIdentities/import", body);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ReadErrorBodyAsync(response));
        var result = await response.Content.ReadFromJsonAsync<ImportedAutopilotIdentitiesResponse>(_jsonOptions);
        return result?.Value ?? new();
    }

    /// <summary>Re-read imported identities to follow their import state.</summary>
    public async Task<ImportedAutopilotIdentity?> GetImportedAutopilotIdentityAsync(string id)
    {
        if (!await SetAuthorizationAsync()) return null;
        try
        {
            var response = await _client.GetAsync($"deviceManagement/importedWindowsAutopilotDeviceIdentities/{id}");
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<ImportedAutopilotIdentity>(_jsonOptions)
                : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to read imported Autopilot identity {Id}", id);
            return null;
        }
    }

    private async Task<DeviceActionResult> AutopilotPostAsync(string identityId, string action, object? body)
    {
        if (!await SetAuthorizationAsync())
            return Failed(identityId, action, "Not authenticated");
        try
        {
            var url = $"{AutopilotIdentities}/{identityId}/{action}";
            var response = body == null
                ? await _client.PostAsync(url, null)
                : await _client.PostAsJsonAsync(url, body);
            if (response.IsSuccessStatusCode)
            {
                Log.Information("Autopilot {Action} on {Id}", action, identityId);
                return new DeviceActionResult { Success = true, DeviceId = identityId, Action = action };
            }
            return Failed(identityId, action, await ReadErrorBodyAsync(response));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Autopilot {Action} failed on {Id}", action, identityId);
            return Failed(identityId, action, ex.Message);
        }
    }

    private static DeviceActionResult Failed(string id, string action, string? message) =>
        new() { Success = false, DeviceId = id, Action = action, Message = message };
}
