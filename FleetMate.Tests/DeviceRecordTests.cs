using System.Text.Json;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The directory-record model behind `fleetmate intune autopilot|cleanup`.
///
/// The payloads are hand-authored and describe no real machine. What they pin is
/// a shape, not a device: an Entra object still stamped with a ZTDID whose Intune
/// partner record is gone. That half-cleaned state is what a hand cleanup leaves
/// behind when it deletes only the Intune record, and it is why the next OOBE
/// pass re-uses a stale object Intune has never heard of. 
/// </summary>
public class DeviceRecordTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Shaped like GET /v1.0/deviceManagement/windowsAutopilotDeviceIdentities.
    // enrollmentState notContacted with a managedDeviceId still set is the
    // dangling combination the cleanup path has to recognise.
    private const string AutopilotJson = """
    {
      "id": "11111111-1111-4111-8111-111111111111",
      "groupTag": "",
      "serialNumber": "SERIAL0001",
      "manufacturer": "CONTOSO",
      "model": "MODEL-0001",
      "enrollmentState": "notContacted",
      "lastContactedDateTime": "0001-01-01T00:00:00Z",
      "systemFamily": "Example Workstation",
      "azureActiveDirectoryDeviceId": "22222222-2222-4222-8222-222222222222",
      "managedDeviceId": "33333333-3333-4333-8333-333333333333",
      "displayName": ""
    }
    """;

    // Shaped like GET /v1.0/devices. physicalIds keeps all four stamp forms
    // because ZtdId has to find its entry among the ones it must ignore.
    private const string EntraDeviceJson = """
    {
      "id": "44444444-4444-4444-8444-444444444444",
      "deviceId": "22222222-2222-4222-8222-222222222222",
      "displayName": "TESTHOST-01",
      "accountEnabled": true,
      "trustType": "AzureAd",
      "isCompliant": false,
      "isManaged": false,
      "operatingSystem": "Windows",
      "operatingSystemVersion": "10.0.26100.1000",
      "physicalIds": [
        "[USER-HWID]:55555555-5555-4555-8555-555555555555:1000000000000001",
        "[GID]:g:2000000000000002",
        "[ZTDID]:11111111-1111-4111-8111-111111111111",
        "[HWID]:h:1000000000000001"
      ]
    }
    """;

    private const string AutopilotId = "11111111-1111-4111-8111-111111111111";
    private const string AadDeviceId = "22222222-2222-4222-8222-222222222222";
    private const string ManagedDeviceId = "33333333-3333-4333-8333-333333333333";
    private const string EntraObjectId = "44444444-4444-4444-8444-444444444444";

    private static AutopilotDevice Autopilot() =>
        JsonSerializer.Deserialize<AutopilotDevice>(AutopilotJson, Options)!;

    private static EntraDevice EntraDevice() =>
        JsonSerializer.Deserialize<EntraDevice>(EntraDeviceJson, Options)!;

    [Fact]
    public void ReadsAutopilotIdentityFromGraph()
    {
        var ap = Autopilot();

        Assert.Equal("SERIAL0001", ap.SerialNumber);
        Assert.Equal("CONTOSO", ap.Manufacturer);
        Assert.Equal("notContacted", ap.EnrollmentState);
        Assert.Equal(AadDeviceId, ap.AzureActiveDirectoryDeviceId);
        Assert.Equal(ManagedDeviceId, ap.ManagedDeviceId);
    }

    [Fact]
    public void ExtractsZtdIdFromPhysicalIds()
    {
        // The ZTDID stamp is how an orphaned Entra object is matched back to the
        // machine that will re-use it at the next OOBE.
        Assert.Equal(AutopilotId, EntraDevice().ZtdId);
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
        // What a hand cleanup leaves behind: Intune record deleted, Entra object
        // still present and still bound by ZTDID.
        var state = new GraphService.DeviceRecordState
        {
            Serial = "SERIAL0001",
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
            Serial = "SERIAL0001",
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
            Serial = "SERIAL0001",
            Autopilot = Autopilot(),
            Intune = new IntuneDevice { Id = ManagedDeviceId, DeviceName = "TESTHOST-01" },
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

        var result = await graph.CleanDeviceRecordsAsync("SERIAL0001", confirmed: false);

        Assert.False(result.Success);
        Assert.Empty(result.Deleted);
        Assert.Contains(result.Errors, e => e.Contains("Confirmation required"));
    }

    [Fact]
    public async Task DeleteManagedDeviceRefusesWithoutConfirmation()
    {
        using var graph = new GraphService(new Core.Config.GraphConfig());

        var result = await graph.DeleteManagedDeviceAsync(ManagedDeviceId, confirmed: false);

        Assert.False(result.Success);
        Assert.Equal("deleteManagedDevice", result.Action);
    }

    [Fact]
    public async Task DeleteEntraDeviceRefusesWithoutConfirmation()
    {
        using var graph = new GraphService(new Core.Config.GraphConfig());

        var result = await graph.DeleteEntraDeviceAsync(EntraObjectId, confirmed: false);

        Assert.False(result.Success);
        Assert.Equal("deleteEntraDevice", result.Action);
    }
}
