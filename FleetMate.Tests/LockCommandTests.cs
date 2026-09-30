using FleetMate.Commands.Devices;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// `fleetmate lock` changes one group membership, so what matters is that it
/// picks the right directory object and refuses everything else. Fixtures are
/// hand-authored: no captured device data.
/// </summary>
public class LockCommandTests
{
    private const string AadId = "00000000-0000-0000-0000-0000000000aa";

    private static GraphService.DeviceRecordState Windows(params EntraDevice[] entra)
    {
        var state = new GraphService.DeviceRecordState
        {
            Serial = "SERIAL0001",
            Intune = new IntuneDevice
            {
                Id = "00000000-0000-0000-0000-000000000001",
                DeviceName = "PC-0001",
                OperatingSystem = "Windows",
                AzureAdDeviceId = AadId,
            },
        };
        state.EntraDevices.AddRange(entra);
        return state;
    }

    [Fact]
    public void TargetsTheEntraObjectIntuneNames()
    {
        var stale = new EntraDevice { Id = "stale", DeviceId = "00000000-0000-0000-0000-0000000000bb" };
        var bound = new EntraDevice { Id = "bound", DeviceId = AadId };

        var target = LockCommand.TargetEntraDevice(Windows(stale, bound), out var refusal);

        Assert.Equal("bound", target?.Id);
        Assert.Null(refusal);
    }

    [Fact]
    public void RefusesWithoutAnIntuneRecord()
    {
        var state = new GraphService.DeviceRecordState { Serial = "SERIAL0001" };

        Assert.Null(LockCommand.TargetEntraDevice(state, out var refusal));
        Assert.Contains("no Intune record", refusal);
    }

    [Fact]
    public void RefusesANonWindowsDevice()
    {
        var state = Windows(new EntraDevice { Id = "bound", DeviceId = AadId });
        state.Intune!.OperatingSystem = "macOS";

        Assert.Null(LockCommand.TargetEntraDevice(state, out var refusal));
        Assert.Contains("Windows only", refusal);
    }

    [Fact]
    public void RefusesWhenNoEntraObjectMatchesIntune()
    {
        var state = Windows(new EntraDevice { Id = "stale", DeviceId = "00000000-0000-0000-0000-0000000000bb" });

        Assert.Null(LockCommand.TargetEntraDevice(state, out var refusal));
        Assert.Contains("no Entra device object", refusal);
    }

    [Theory]
    [InlineData("lock", true, true)]
    [InlineData("lock", false, false)]
    [InlineData("unlock", false, true)]
    [InlineData("unlock", true, false)]
    public void NoOpWhenAlreadyInTheRequestedState(string verb, bool inGroup, bool noOp)
    {
        var plan = new LockCommand.LockPlan(verb, "S", "PC", "Windows", null, null, "Devices-Lock", inGroup, "1");
        Assert.Equal(noOp, LockCommand.IsNoOp(plan));
    }

    [Fact]
    public void NoteCarriesTicketReasonAndOperator()
    {
        var when = new DateTimeOffset(2026, 1, 2, 3, 4, 0, TimeSpan.Zero);
        var line = LockCommand.NoteLine("lock", "12345", "not returned", "operator", when);

        Assert.Equal("2026-01-02 03:04 +00:00 fleetmate lock by operator - ticket 12345 - not returned", line);
    }
}
