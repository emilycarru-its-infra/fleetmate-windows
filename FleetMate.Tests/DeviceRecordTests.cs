using System.Text.Json;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The directory-record model behind `fleetmate intune autopilot|cleanup`.
///
/// The payloads here are the real Graph responses for SERIAL0101 ([tracked internally]) — a
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
      "id": "1a2b3c4d-4444-4aaa-8bbb-000000000104",
      "groupTag": "",
      "serialNumber": "SERIAL0101",
      "manufacturer": "LENOVO",
      "model": "SERIAL0001",
      "enrollmentState": "notContacted",
      "lastContactedDateTime": "0001-01-01T00:00:00Z",
      "systemFamily": "ThinkStation P3 Tower",
      "azureActiveDirectoryDeviceId": "1a2b3c4d-2222-4aaa-8bbb-000000000102",
      "managedDeviceId": "1a2b3c4d-1111-4aaa-8bbb-000000000101",
      "displayName": ""
    }
    """;

    // Verbatim from GET /v1.0/devices
    private const string EntraDeviceJson = """
    {
      "id": "1a2b3c4d-3333-4aaa-8bbb-000000000103",
      "deviceId": "1a2b3c4d-2222-4aaa-8bbb-000000000102",
      "displayName": "LAB-WS-01",
      "accountEnabled": true,
      "trustType": "AzureAd",
      "isCompliant": false,
      "isManaged": false,
      "operatingSystem": "Windows",
      "operatingSystemVersion": "10.0.26200.8893",
      "physicalIds": [
        "[USER-HWID]:1a2b3c4d-5555-4aaa-8bbb-000000000105:1000000000000001",
        "[GID]:g:6896202635369856",
        "[ZTDID]:1a2b3c4d-4444-4aaa-8bbb-000000000104",
        "[HWID]:h:1000000000000001"
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

        Assert.Equal("SERIAL0101", ap.SerialNumber);
        Assert.Equal("LENOVO", ap.Manufacturer);
        Assert.Equal("notContacted", ap.EnrollmentState);
        Assert.Equal("1a2b3c4d-2222-4aaa-8bbb-000000000102", ap.AzureActiveDirectoryDeviceId);
        Assert.Equal("1a2b3c4d-1111-4aaa-8bbb-000000000101", ap.ManagedDeviceId);
    }

    [Fact]
    public void ExtractsZtdIdFromPhysicalIds()
    {
        // The ZTDID stamp is how an orphaned Entra object is matched back to the
        // machine that will re-use it at the next OOBE.
        Assert.Equal("1a2b3c4d-4444-4aaa-8bbb-000000000104", EntraDevice().ZtdId);
    }

    [Fact]
    public void ZtdIdIsNullWhenTheObjectCarriesNoAutopilotStamp()
    {
        var device = new EntraDevice { PhysicalIds = ["[HWID]:h:1000000000000001"] };

        Assert.Null(device.ZtdId);
    }

    [Fact]
    public void EntraObjectWithoutIntuneRecordIsOrphaned()
    {
        // The exact state SERIAL0101 was left in: Intune record deleted by hand,
        // Entra object still present and still bound by ZTDID.
        var state = new GraphService.DeviceRecordState
        {
            Serial = "SERIAL0101",
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
            Serial = "SERIAL0101",
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
            Serial = "SERIAL0101",
            Autopilot = Autopilot(),
            Intune = new IntuneDevice { Id = "1a2b3c4d-1111-4aaa-8bbb-000000000101", DeviceName = "LAB-WS-01" },
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

        var result = await graph.CleanDeviceRecordsAsync("SERIAL0101", confirmed: false);

        Assert.False(result.Success);
        Assert.Empty(result.Deleted);
        Assert.Contains(result.Errors, e => e.Contains("Confirmation required"));
    }

    [Fact]
    public async Task DeleteManagedDeviceRefusesWithoutConfirmation()
    {
        using var graph = new GraphService(new Core.Config.GraphConfig());

        var result = await graph.DeleteManagedDeviceAsync("1a2b3c4d-1111-4aaa-8bbb-000000000101", confirmed: false);

        Assert.False(result.Success);
        Assert.Equal("deleteManagedDevice", result.Action);
    }

    [Fact]
    public async Task DeleteEntraDeviceRefusesWithoutConfirmation()
    {
        using var graph = new GraphService(new Core.Config.GraphConfig());

        var result = await graph.DeleteEntraDeviceAsync("1a2b3c4d-3333-4aaa-8bbb-000000000103", confirmed: false);

        Assert.False(result.Success);
        Assert.Equal("deleteEntraDevice", result.Action);
    }
}
