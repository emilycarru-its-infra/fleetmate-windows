using System.Text.Json;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The directory-record model behind `fleetmate intune autopilot|cleanup`.
///
/// The payloads here are the real Graph responses for MJ0KP6EV ([tracked internally]) — a
/// shared Lenovo that failed AutoPilot at "Securing your hardware", was fixed by
/// hand, and then failed again because the hand fix deleted only the Intune
/// record. That half-cleaned state is what these tests pin.
/// </summary>
public class DeviceRecordTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Verbatim from GET /v1.0/deviceManagement/windowsAutopilotDeviceIdentities
    private const string AutopilotJson = """
    {
      "id": "6349bbfe-c07a-444c-a7e0-eee62553f000",
      "groupTag": "",
      "serialNumber": "MJ0KP6EV",
      "manufacturer": "LENOVO",
      "model": "SERIAL0001",
      "enrollmentState": "notContacted",
      "lastContactedDateTime": "0001-01-01T00:00:00Z",
      "systemFamily": "ThinkStation P3 Tower",
      "azureActiveDirectoryDeviceId": "8d9f1c7a-4883-4bfa-81e9-5d96ab6b41b8",
      "managedDeviceId": "dc12ac91-2f80-4d5c-b6c6-622d43a26c76",
      "displayName": ""
    }
    """;

    // Verbatim from GET /v1.0/devices
    private const string EntraDeviceJson = """
    {
      "id": "839c6139-1d9d-4b8d-9c35-2319c85e24c9",
      "deviceId": "8d9f1c7a-4883-4bfa-81e9-5d96ab6b41b8",
      "displayName": "LAB-WS-01",
      "accountEnabled": true,
      "trustType": "AzureAd",
      "isCompliant": false,
      "isManaged": false,
      "operatingSystem": "Windows",
      "operatingSystemVersion": "10.0.26200.8893",
      "physicalIds": [
        "[USER-HWID]:d31f8f5c-9982-4912-a1f9-f59c21562d6d:6825816683144846",
        "[GID]:g:6896202635369856",
        "[ZTDID]:6349bbfe-c07a-444c-a7e0-eee62553f000",
        "[HWID]:h:6825816683144846"
      ]
    }
    """;

    private static AutopilotDevice Autopilot() =>
        JsonSerializer.Deserialize<AutopilotDevice>(AutopilotJson, Options)!;

    private static EntraDevice EntraDevice() =>
        JsonSerializer.Deserialize<EntraDevice>(EntraDeviceJson, Options)!;

    [Fact]
    public void ReadsAutopilotIdentityFromGraph()
    {
        var ap = Autopilot();

        Assert.Equal("MJ0KP6EV", ap.SerialNumber);
        Assert.Equal("LENOVO", ap.Manufacturer);
        Assert.Equal("notContacted", ap.EnrollmentState);
        Assert.Equal("8d9f1c7a-4883-4bfa-81e9-5d96ab6b41b8", ap.AzureActiveDirectoryDeviceId);
        Assert.Equal("dc12ac91-2f80-4d5c-b6c6-622d43a26c76", ap.ManagedDeviceId);
    }

    [Fact]
    public void ExtractsZtdIdFromPhysicalIds()
    {
        // The ZTDID stamp is how an orphaned Entra object is matched back to the
        // machine that will re-use it at the next OOBE.
        Assert.Equal("6349bbfe-c07a-444c-a7e0-eee62553f000", EntraDevice().ZtdId);
    }

    [Fact]
    public void ZtdIdIsNullWhenTheObjectCarriesNoAutopilotStamp()
    {
        var device = new EntraDevice { PhysicalIds = ["[HWID]:h:6825816683144846"] };

        Assert.Null(device.ZtdId);
    }

    [Fact]
    public void EntraObjectWithoutIntuneRecordIsOrphaned()
    {
        // The exact state MJ0KP6EV was left in: Intune record deleted by hand,
        // Entra object still present and still bound by ZTDID.
        var state = new GraphService.DeviceRecordState
        {
            Serial = "MJ0KP6EV",
            Autopilot = Autopilot(),
            Intune = null,
            EntraDevices = [EntraDevice()]
        };

        Assert.True(state.IsOrphaned);
        Assert.True(state.HasDanglingManagedDeviceId);
    }

    [Fact]
    public void FullyCleanedDeviceIsNotOrphaned()
    {
        // After `intune cleanup`: both records gone, AutoPilot identity retained.
        var state = new GraphService.DeviceRecordState
        {
            Serial = "MJ0KP6EV",
            Autopilot = Autopilot(),
            Intune = null,
            EntraDevices = []
        };

        Assert.False(state.IsOrphaned);
    }

    [Fact]
    public void HealthyEnrolledDeviceIsNotOrphaned()
    {
        var state = new GraphService.DeviceRecordState
        {
            Serial = "MJ0KP6EV",
            Autopilot = Autopilot(),
            Intune = new IntuneDevice { Id = "dc12ac91-2f80-4d5c-b6c6-622d43a26c76", DeviceName = "LAB-WS-01" },
            EntraDevices = [EntraDevice()]
        };

        Assert.False(state.IsOrphaned);
        Assert.False(state.HasDanglingManagedDeviceId);
    }

    [Fact]
    public async Task CleanupRefusesWithoutConfirmation()
    {
        // Defense in depth: the destructive path must refuse before it reads or
        // deletes anything, so an unconfirmed call cannot touch Graph at all.
        using var graph = new GraphService(new Core.Config.GraphConfig());

        var result = await graph.CleanDeviceRecordsAsync("MJ0KP6EV", confirmed: false);

        Assert.False(result.Success);
        Assert.Empty(result.Deleted);
        Assert.Contains(result.Errors, e => e.Contains("Confirmation required"));
    }

    [Fact]
    public async Task DeleteManagedDeviceRefusesWithoutConfirmation()
    {
        using var graph = new GraphService(new Core.Config.GraphConfig());

        var result = await graph.DeleteManagedDeviceAsync("dc12ac91-2f80-4d5c-b6c6-622d43a26c76", confirmed: false);

        Assert.False(result.Success);
        Assert.Equal("deleteManagedDevice", result.Action);
    }

    [Fact]
    public async Task DeleteEntraDeviceRefusesWithoutConfirmation()
    {
        using var graph = new GraphService(new Core.Config.GraphConfig());

        var result = await graph.DeleteEntraDeviceAsync("839c6139-1d9d-4b8d-9c35-2319c85e24c9", confirmed: false);

        Assert.False(result.Success);
        Assert.Equal("deleteEntraDevice", result.Action);
    }
}
