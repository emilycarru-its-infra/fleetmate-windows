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
            [Orphan("SERIAL0001")], "autopilot-reset", twinsOnly: true, recordsOnly: false);

        Assert.Contains("no reset can be sent", plan);
        Assert.DoesNotContain("The AutoPilot identity is always kept", plan);
    }

    [Fact]
    public void PlanSaysCleanupStillRunsForAnOrphanBatch()
    {
        var plan = WipeCommand.PlanSummary(
            [Orphan("SERIAL0001")], "autopilot-reset", twinsOnly: true, recordsOnly: false);

        Assert.Contains("no reset can be sent", plan);
        Assert.Contains("Stale Intune and Entra records will still be deleted", plan);
    }

    [Fact]
    public void PlanCountsOnlyTheDevicesThatCanBeReset()
    {
        var plan = WipeCommand.PlanSummary(
            [Enrolled("SERIAL0001"), Orphan("SERIAL0002"), Orphan("SERIAL0003")],
            "autopilot-reset", twinsOnly: true, recordsOnly: false);

        Assert.Contains("for 1 of 3 device(s)", plan);
    }

    [Fact]
    public void RecordsOnlyPlanSendsNoReset()
    {
        var plan = WipeCommand.PlanSummary(
            [Enrolled("SERIAL0001")], "autopilot-reset", twinsOnly: true, recordsOnly: true);

        Assert.Contains("no reset is sent", plan);
    }

    [Fact]
    public void CleanupIsAlwaysPartOfTheResetPlan()
    {
        var reset = WipeCommand.PlanSummary([Enrolled("SERIAL0001")], "autopilot-reset", twinsOnly: true, recordsOnly: false);
        var factory = WipeCommand.PlanSummary([Enrolled("SERIAL0001")], "factory", twinsOnly: false, recordsOnly: false);

        Assert.Contains("delete stale Entra twins", reset);
        Assert.Contains("enrollment it returns to is kept", reset);
        Assert.Contains("delete stale Intune and Entra records", factory);
    }

    private const string AutopilotBound = "11111111-1111-1111-1111-111111111111";
    private const string IntuneBound = "22222222-2222-2222-2222-222222222222";
    private const string StaleHybrid = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public void StaleTwinsExcludeEveryBoundObject()
    {
        var state = new GraphService.DeviceRecordState
        {
            Serial = "SERIAL0001",
            Autopilot = new AutopilotDevice { AzureActiveDirectoryDeviceId = AutopilotBound },
            Intune = new IntuneDevice { Id = "00000000-0000-0000-0000-000000000009", AzureAdDeviceId = IntuneBound },
            EntraDevices =
            {
                new EntraDevice { Id = "obj-1", DeviceId = AutopilotBound, TrustType = "AzureAd" },
                new EntraDevice { Id = "obj-2", DeviceId = IntuneBound, TrustType = "AzureAd" },
                new EntraDevice { Id = "obj-3", DeviceId = StaleHybrid, TrustType = "ServerAd" },
            },
        };

        var twins = GraphService.StaleEntraTwins(state);

        Assert.Single(twins);
        Assert.Equal("obj-3", twins[0].Id);
    }

    [Fact]
    public void NoTwinsWhenNothingIsBound()
    {
        // Without an AutoPilot identity or an Intune binding there is no way to
        // tell the live object from a stale one, so nothing may be treated as a twin.
        var state = new GraphService.DeviceRecordState
        {
            Serial = "SERIAL0001",
            EntraDevices =
            {
                new EntraDevice { Id = "obj-1", DeviceId = AutopilotBound, TrustType = "AzureAd" },
                new EntraDevice { Id = "obj-2", DeviceId = StaleHybrid, TrustType = "ServerAd" },
            },
        };

        Assert.Empty(GraphService.StaleEntraTwins(state));
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

    [Fact]
    public void ASingleDashFlagIsRejectedAsASerial()
    {
        var error = WipeCommand.FlagLikeSerialError(["SERIAL0001", "-confirm"]);

        Assert.NotNull(error);
        Assert.Contains("--confirm", error);
    }

    [Fact]
    public void AMisspelledSingleDashFlagStillPointsAtTheRealOne()
    {
        var error = WipeCommand.FlagLikeSerialError(["SERIAL0001", "-record-only"]);

        Assert.NotNull(error);
        Assert.Contains("--records-only", error);
    }

    [Fact]
    public void PlainSerialsAreNotFlagged()
    {
        Assert.Null(WipeCommand.FlagLikeSerialError(["SERIAL0001", "SERIAL0002"]));
        Assert.Null(WipeCommand.FlagLikeSerialError([]));
    }
}
