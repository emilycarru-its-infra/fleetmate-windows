using FleetMate.Commands.Devices;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The two lines `fleetmate wipe` prints that an operator actually acts on: the
/// dry-run plan and the closing summary.
///
/// Both used to describe the flags rather than the devices. A batch where no
/// target had an Intune record still announced "Plan: autopilot-reset" and still
/// closed with "Done. N device(s) processed." after doing nothing at all — an
/// AutoPilot Reset is an Intune action, so a device with no Intune record can
/// never receive one. These pin the wording so a no-op cannot read as success.
///
/// Fixtures are hand-authored: no captured device data.
/// </summary>
public class WipeCommandTests
{
    private static GraphService.DeviceRecordState Enrolled(string serial) => new()
    {
        Serial = serial,
        Intune = new IntuneDevice { Id = "00000000-0000-0000-0000-000000000001", DeviceName = serial },
        EntraDevices = { new EntraDevice() },
        Autopilot = new AutopilotDevice(),
    };

    /// <summary>Entra still holds a device object; Intune has no record.</summary>
    private static GraphService.DeviceRecordState Orphan(string serial) => new()
    {
        Serial = serial,
        Intune = null,
        EntraDevices = { new EntraDevice() },
        Autopilot = new AutopilotDevice(),
    };

    [Fact]
    public void PlanDoesNotAnnounceAResetNothingCanReceive()
    {
        var plan = WipeCommand.PlanSummary(
            [Orphan("SERIAL0001")], "autopilot-reset", cleanup: false, recordsOnly: false);

        Assert.Contains("no reset can be sent", plan);
        Assert.DoesNotContain("The AutoPilot identity is always kept", plan);
    }

    [Fact]
    public void PlanSaysCleanupStillRunsForAnOrphanBatch()
    {
        var plan = WipeCommand.PlanSummary(
            [Orphan("SERIAL0001")], "autopilot-reset", cleanup: true, recordsOnly: false);

        Assert.Contains("no reset can be sent", plan);
        Assert.Contains("Stale Intune and Entra records will still be deleted", plan);
    }

    [Fact]
    public void PlanCountsOnlyTheDevicesThatCanBeReset()
    {
        var plan = WipeCommand.PlanSummary(
            [Enrolled("SERIAL0001"), Orphan("SERIAL0002"), Orphan("SERIAL0003")],
            "autopilot-reset", cleanup: false, recordsOnly: false);

        Assert.Contains("for 1 of 3 device(s)", plan);
    }

    [Fact]
    public void RecordsOnlyPlanSendsNoReset()
    {
        var plan = WipeCommand.PlanSummary(
            [Enrolled("SERIAL0001")], "autopilot-reset", cleanup: false, recordsOnly: true);

        Assert.Contains("no reset is sent", plan);
    }

    [Fact]
    public void ARunThatChangedNothingIsNotReportedAsDone()
    {
        var summary = WipeCommand.OutcomeSummary(changed: 0, failures: 0, total: 1);

        Assert.Contains("Nothing to do", summary);
        Assert.DoesNotContain("Done", summary);
    }

    [Fact]
    public void DoneReportsWhatActuallyChanged()
    {
        var summary = WipeCommand.OutcomeSummary(changed: 2, failures: 0, total: 5);

        Assert.Contains("Done", summary);
        Assert.Contains("2 of 5 device(s) changed", summary);
    }

    [Fact]
    public void FailuresOutrankTheChangeCount()
    {
        var summary = WipeCommand.OutcomeSummary(changed: 1, failures: 2, total: 3);

        Assert.Contains("2 failure(s)", summary);
    }
}
