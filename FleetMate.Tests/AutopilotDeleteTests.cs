using FleetMate.Commands.Devices;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The AutoPilot identity is the only record FleetMate can delete that no
/// re-enrollment restores — it holds the hardware hash, and Graph never returns
/// it. These cover the wording that stands in front of that deletion, and the
/// two states where deleting is nearly always a mistake.
/// </summary>
public class AutopilotDeleteTests
{
    private static GraphService.DeviceRecordState State(
        AutopilotDevice? autopilot = null, IntuneDevice? intune = null, params EntraDevice[] entra)
        => new()
        {
            Serial = "SERIAL0001",
            Autopilot = autopilot ?? new AutopilotDevice { Id = "11111111-1111-4111-8111-111111111111", SerialNumber = "SERIAL0001" },
            Intune = intune,
            EntraDevices = entra.ToList(),
        };

    [Fact]
    public void Warning_LeadsWithTheIrreversibleHardwareHash()
    {
        var text = IntuneCommand.AutopilotDeleteWarning(State());

        Assert.Contains("cannot be undone", text);
        Assert.Contains("hardware hash", text);
        Assert.Contains("SERIAL0001", text);
    }

    [Fact]
    public void Warning_CallsOutADeviceThatIsStillEnrolled()
    {
        // A live machine has an Intune record. Deleting its AutoPilot identity
        // strips a registration nothing is asking to be rebuilt.
        var text = IntuneCommand.AutopilotDeleteWarning(
            State(intune: new IntuneDevice { Id = "d1", DeviceName = "TESTHOST-01" }));

        Assert.Contains("still enrolled", text);
        Assert.Contains("TESTHOST-01", text);
        Assert.Contains("wrong device", text);
    }

    [Fact]
    public void Warning_SaysDeletingAutopilotLeavesEntraObjectsBehind()
    {
        var text = IntuneCommand.AutopilotDeleteWarning(
            State(entra: new EntraDevice { Id = "e1", DisplayName = "TESTHOST-01" }));

        Assert.Contains("Entra still holds 1 device object(s)", text);
        Assert.Contains("will not remove those", text);
    }

    [Fact]
    public void Warning_OnACleanOrphanSaysNothingAboutIntuneOrEntra()
    {
        // The case this command exists for: AutoPilot registration whose
        // pre-created Entra object was destroyed. Nothing else should be flagged.
        var text = IntuneCommand.AutopilotDeleteWarning(State());

        Assert.DoesNotContain("still enrolled", text);
        Assert.DoesNotContain("Entra still holds", text);
    }
}
