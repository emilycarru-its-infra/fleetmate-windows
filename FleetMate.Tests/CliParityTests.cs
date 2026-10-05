using System.CommandLine;
using FleetMate.Commands.Devices;
using FleetMate.Commands.Identity;
using FleetMate.Commands.Reporting;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using FleetMate.Core.Services.Reporting;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The CLI commands ported from the macOS client: parsing, gating, targeting and the offboard plan.</summary>
public class CliParityTests
{
    private static RootCommand Root(GraphService? graph = null)
    {
        var root = new RootCommand("FleetMate") { Name = "fleetmate" };
        root.AddCommand(IntuneCommand.Create(graph, null));
        root.AddCommand(AutopilotCommand.Create(graph));
        root.AddCommand(EntraCommand.Create(graph, null));
        return root;
    }

    // ── Command tree and parsing ────────────────────────────────────────

    [Theory]
    [InlineData("intune noncompliant -l 5 -j")]
    [InlineData("intune fresh-start SER-1 --no-keep-user-data --confirm")]
    [InlineData("intune fresh-start SER-1 --dry-run")]
    [InlineData("intune delete-record SER-1 --confirm")]
    [InlineData("intune delete SER-1 --dry-run")]
    [InlineData("intune offboard SER-1 --action retire --entra delete --delete-autopilot --delete-record --dry-run")]
    [InlineData("intune offboard SER-1 --protected --keep-user-data --confirm")]
    [InlineData("intune offboard SER-1 --unlock-code 123456 --obliteration doNotObliterate --dry-run")]
    [InlineData("intune laps SER-1 --json")]
    [InlineData("entra search-groups Lab -l 5 -j")]
    [InlineData("autopilot")]
    [InlineData("autopilot -l 5 --json")]
    [InlineData("autopilot list -f \"groupTag eq 'Lab'\" -l 10")]
    [InlineData("autopilot get SER-1 -j")]
    [InlineData("autopilot delete SER-1 --dry-run")]
    [InlineData("autopilot assign-user SER-1 pat@example.edu --display-name Pat")]
    [InlineData("autopilot unassign-user SER-1")]
    public void MacCommandLines_Parse(string line) => Assert.Empty(Root().Parse(line).Errors);

    [Theory]
    [InlineData("intune offboard SER-1 --action destroy")]
    [InlineData("intune offboard SER-1 --entra purge")]
    [InlineData("intune offboard SER-1 --obliteration sometimes")]
    [InlineData("intune laps")]
    public void BadValues_AreParseErrors(string line) => Assert.NotEmpty(Root().Parse(line).Errors);

    // ── Confirmation gating ─────────────────────────────────────────────

    [Theory]
    [InlineData("intune fresh-start SER-1")]
    [InlineData("intune delete-record SER-1")]
    [InlineData("intune offboard SER-1")]
    [InlineData("autopilot delete SER-1")]
    public async Task DestructiveCommands_RefuseWithoutConfirmOrDryRun(string line)
    {
        // A real (unauthenticated) service: the refusal must come before any
        // Graph call, not from the service being missing.
        using var graph = new GraphService(new GraphConfig());
        Assert.Equal(1, await Root(graph).InvokeAsync(line));
    }

    [Fact]
    public void OffboardFlags_BuildThePlan()
    {
        var plan = IntuneLifecycleCommands.BuildPlan("retire", true, true, "123456", "always", true, "disable", true);
        Assert.Equal(OffboardTerminalAction.Retire, plan.TerminalAction);
        Assert.Equal(OffboardEntraAction.Disable, plan.EntraAction);
        Assert.True(plan.DeleteAutopilotRegistration);
        Assert.True(plan.DeleteIntuneRecord);
        Assert.True(plan.WipeOptions.UseProtectedWipe);
        Assert.Equal("always", plan.WipeOptions.ObliterationBehavior);
    }

    // ── Targeting rules ─────────────────────────────────────────────────

    [Theory]
    [InlineData("C02XYZ123", true)]
    [InlineData("SER-1", true)]
    [InlineData("C02 XYZ", false)]
    [InlineData("C02' or 1 eq 1", false)]
    [InlineData("", false)]
    [InlineData("a*", false)]
    public void Serials_AreLettersDigitsAndHyphensOnly(string serial, bool ok)
    {
        Assert.Equal(ok, CliTargets.IsSerial(serial));
        Assert.Equal(ok, CliTargets.SerialFilter(serial) != null);
    }

    [Fact]
    public void SerialFilter_IsAnExactEq()
    {
        Assert.Equal("serialNumber eq 'SER-1'", CliTargets.SerialFilter(" SER-1 "));
        Assert.Equal("'it''s'", CliTargets.Literal("it's"));
    }

    [Theory]
    [InlineData("3f2b6c1e-8a51-4b1d-9f7a-0c2e5d4b6a10", true)]
    [InlineData("{3f2b6c1e-8a51-4b1d-9f7a-0c2e5d4b6a10}", false)]
    [InlineData("3f2b6c1e8a514b1d9f7a0c2e5d4b6a10", false)]
    public void Guids_OnlyInTheirCanonicalForm(string value, bool ok) => Assert.Equal(ok, CliTargets.IsGuid(value));

    // ── Wipe options ────────────────────────────────────────────────────

    [Fact]
    public void WipeBody_SendsOnlyWhatThePlatformAccepts()
    {
        var options = new WipeOptions { KeepUserData = true, UseProtectedWipe = true, MacOsUnlockCode = "123456", ObliterationBehavior = "always" };

        Assert.Equal("{\"keepEnrollmentData\":false,\"keepUserData\":true,\"useProtectedWipe\":true}",
            WipeOptions.Describe(options.RequestBody(DevicePlatform.Windows)));
        Assert.Equal("{\"keepEnrollmentData\":false,\"keepUserData\":false,\"macOsUnlockCode\":\"123456\",\"obliterationBehavior\":\"always\"}",
            WipeOptions.Describe(options.RequestBody(DevicePlatform.MacOS)));
        Assert.Equal(new[] { "--keep-user-data (not supported on macOS)", "--protected (Windows only)" },
            IntuneLifecycleCommands.DroppedWipeOptions(options, DevicePlatform.MacOS));
    }

    [Fact]
    public void LapsTimestamp_ReadsTheRevealDetail()
    {
        Assert.Equal("2026-10-01T10:00:00Z", IntuneLifecycleCommands.LapsTimestamp("Last rotated 2026-10-01T10:00:00Z"));
        Assert.Equal("2026-09-30", IntuneLifecycleCommands.LapsTimestamp("Account admin · backed up 2026-09-30"));
        Assert.Null(IntuneLifecycleCommands.LapsTimestamp(null));
    }

    // ── Offboard ────────────────────────────────────────────────────────

    private static IntuneDevice Windows() => new()
    {
        Id = "intune-1", DeviceName = "PC-1", SerialNumber = "SER-1", OperatingSystem = "Windows",
        AzureAdDeviceId = "entra-device-1",
    };

    private static OffboardPlan Everything() => new()
    {
        TerminalAction = OffboardTerminalAction.Wipe,
        DeleteAutopilotRegistration = true,
        EntraAction = OffboardEntraAction.Delete,
        DeleteIntuneRecord = true,
    };

    [Fact]
    public async Task Offboard_RunsInTheSafeOrder_IntuneRecordLast()
    {
        var graph = new FakeLifecycle();
        var result = await new DeviceOffboarder(graph).OffboardAsync(Windows(), Everything());

        Assert.True(result.Success);
        Assert.Equal(new[] { "wipe intune-1", "delete-autopilot ap-1", "delete-entra obj-1", "delete-record intune-1" }, graph.Calls);
        Assert.All(graph.Confirmed, Assert.True);
    }

    [Fact]
    public async Task Offboard_SkipsAutopilotOnAMac()
    {
        var mac = Windows();
        mac.OperatingSystem = "macOS";
        var graph = new FakeLifecycle();
        var result = await new DeviceOffboarder(graph).OffboardAsync(mac, Everything());

        var autopilot = Assert.Single(result.Steps, s => s.Step == DeviceOffboarder.AutopilotStep);
        Assert.Equal(OffboardOutcome.Skipped, autopilot.Outcome);
        Assert.DoesNotContain(graph.Calls, c => c.StartsWith("delete-autopilot"));
    }

    [Fact]
    public async Task Offboard_DisableEntra_PatchesInsteadOfDeleting()
    {
        var graph = new FakeLifecycle();
        await new DeviceOffboarder(graph).OffboardAsync(Windows(), Everything() with { EntraAction = OffboardEntraAction.Disable });
        Assert.Contains("disable-entra obj-1", graph.Calls);
    }

    [Fact]
    public async Task Preview_WritesNothing()
    {
        var graph = new FakeLifecycle();
        var steps = await new DeviceOffboarder(graph).PreviewAsync(Windows(), Everything());

        Assert.Empty(graph.Calls);
        Assert.Equal("DELETE managedDevices/intune-1", steps.Last().Detail);
        Assert.Contains(steps, s => s.Detail == "DELETE windowsAutopilotDeviceIdentities/ap-1");
    }

    [Fact]
    public async Task Orphan_CleansDirectoryRecords_AndSkipsWhatNeededIntune()
    {
        var graph = new FakeLifecycle();
        var offboarder = new DeviceOffboarder(graph);
        var records = await offboarder.ResolveOrphanRecordsAsync("SER-1");
        var result = await offboarder.OffboardOrphanAsync("SER-1", records, Everything());

        Assert.Equal(new[] { "delete-autopilot ap-1", "delete-entra obj-1" }, graph.Calls);
        Assert.Equal(OffboardOutcome.Skipped, result.Steps.First().Outcome);
        Assert.Equal(OffboardOutcome.Skipped, result.Steps.Last().Outcome);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Offboard_AFailedStepFailsTheRun()
    {
        var graph = new FakeLifecycle { FailEntra = true };
        var result = await new DeviceOffboarder(graph).OffboardAsync(Windows(), Everything());
        Assert.False(result.Success);
        Assert.Contains("delete-record intune-1", graph.Calls);
    }

    private sealed class FakeLifecycle : IDeviceLifecycleGraph
    {
        public List<string> Calls { get; } = new();
        public List<bool> Confirmed { get; } = new();
        public bool FailEntra { get; init; }

        private GraphService.DeviceActionResult Ok(string call, bool confirmed, bool success = true)
        {
            Calls.Add(call);
            Confirmed.Add(confirmed);
            return new GraphService.DeviceActionResult { Success = success, Message = success ? null : "boom" };
        }

        public Task<DeviceResolution> ResolveManagedDeviceAsync(string identifier) => Task.FromResult(DeviceResolution.None());
        public Task<AutopilotDevice?> FindAutopilotRegistrationAsync(string serial) =>
            Task.FromResult<AutopilotDevice?>(new AutopilotDevice { Id = "ap-1", SerialNumber = serial, AzureActiveDirectoryDeviceId = "entra-device-1" });
        public Task<EntraDevice?> GetEntraDeviceByDeviceIdAsync(string deviceId) =>
            Task.FromResult<EntraDevice?>(new EntraDevice { Id = "obj-1", DeviceId = deviceId, DisplayName = "PC-1" });
        public Task<GraphService.DeviceActionResult> WipeDeviceAsync(IntuneDevice device, WipeOptions options, bool confirmed) =>
            Task.FromResult(Ok($"wipe {device.Id}", confirmed));
        public Task<GraphService.DeviceActionResult> RetireDeviceAsync(string deviceId, bool confirmed) =>
            Task.FromResult(Ok($"retire {deviceId}", confirmed));
        public Task<GraphService.DeviceActionResult> DeleteAutopilotRegistrationAsync(string autopilotId, bool confirmed) =>
            Task.FromResult(Ok($"delete-autopilot {autopilotId}", confirmed));
        public Task<GraphService.DeviceActionResult> SetEntraDeviceEnabledAsync(string objectId, bool enabled, bool confirmed) =>
            Task.FromResult(Ok($"{(enabled ? "enable" : "disable")}-entra {objectId}", confirmed, !FailEntra));
        public Task<GraphService.DeviceActionResult> DeleteEntraDeviceAsync(string objectId, bool confirmed) =>
            Task.FromResult(Ok($"delete-entra {objectId}", confirmed, !FailEntra));
        public Task<GraphService.DeviceActionResult> DeleteManagedDeviceAsync(string deviceId, bool confirmed) =>
            Task.FromResult(Ok($"delete-record {deviceId}", confirmed));
    }
}
