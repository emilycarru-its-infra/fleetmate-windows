using FleetMate.Core.Models.Devices;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// What the Devices offboard and wipe cards add on top of the shared offboard
/// model. CliParityTests covers the step order, skips and the dry run.
/// </summary>
public class DeviceOffboardTests
{
    private static readonly OffboardPlan Full = new()
    {
        TerminalAction = OffboardTerminalAction.Wipe,
        DeleteAutopilotRegistration = true,
        EntraAction = OffboardEntraAction.Delete,
        DeleteIntuneRecord = true,
    };

    [Fact]
    public void Summary_NamesEveryStepAndTheCount()
    {
        Assert.Equal("This will factory-reset, delete the Autopilot registration, delete the Entra device object and delete the Intune record for 2 device(s). This cannot be undone.",
            Full.Summary(2));
        Assert.Equal("This will retire for 1 device(s). This cannot be undone.",
            new OffboardPlan { TerminalAction = OffboardTerminalAction.Retire }.Summary(1));
        Assert.Equal("No offboard steps are selected.",
            new OffboardPlan { TerminalAction = OffboardTerminalAction.None }.Summary(1));
    }

    [Fact]
    public void CancelsPendingAction_OnlyWhenTheRecordGoesAfterAnAction()
    {
        Assert.True(Full.CancelsPendingAction);
        Assert.False((Full with { DeleteIntuneRecord = false }).CancelsPendingAction);
        Assert.False((Full with { TerminalAction = OffboardTerminalAction.None }).CancelsPendingAction);
    }

    [Fact]
    public void EveryChoiceHasACardLabel()
    {
        Assert.All(WipeOptions.ObliterationBehaviors, b => Assert.False(string.IsNullOrWhiteSpace(WipeOptions.ObliterationDisplayName(b))));
        Assert.Equal("EACS only (fail if unavailable)", WipeOptions.ObliterationDisplayName("doNotObliterate"));
        Assert.Equal("Wipe (factory reset)", OffboardPlan.DisplayName(OffboardTerminalAction.Wipe));
        Assert.Equal("Disable the Entra device object", OffboardPlan.DisplayName(OffboardEntraAction.Disable));
    }

    [Fact]
    public void StepDisplay_AddsTheReasonWhenThereIsOne()
    {
        Assert.Equal("Delete Intune record", new OffboardStepResult("Delete Intune record", OffboardOutcome.Succeeded).Display);
        Assert.Equal("Delete Autopilot registration — macOS devices have no Autopilot registration",
            new OffboardStepResult("Delete Autopilot registration", OffboardOutcome.Skipped,
                "macOS devices have no Autopilot registration").Display);
    }

    [Fact]
    public void IOSWipe_KeepsTheEsimPlanAndPin_NoObliteration()
    {
        var body = new WipeOptions
        {
            KeepUserData = true, PersistEsimDataPlan = true, MacOsUnlockCode = "123456", ObliterationBehavior = "always",
        }.RequestBody(DevicePlatform.IOS);
        Assert.Equal("{\"keepEnrollmentData\":false,\"keepUserData\":false,\"macOsUnlockCode\":\"123456\",\"persistEsimDataPlan\":true}",
            WipeOptions.Describe(body));
    }
}
