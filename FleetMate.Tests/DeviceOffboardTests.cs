using System.Net;
using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using Xunit;
using Outcome = FleetMate.Core.Models.Devices.OffboardStepResult.StepOutcome;

namespace FleetMate.Tests;

public class WipeOptionsTests
{
    private static readonly WipeOptions Everything = new()
    {
        KeepEnrollmentData = true, KeepUserData = true, UseProtectedWipe = true, PersistEsimDataPlan = true,
        MacOsUnlockCode = "123456", Obliteration = WipeOptions.ObliterationBehavior.DoNotObliterate,
    };

    [Fact]
    public void Windows_GetsProtectedWipe_AndKeepsUserData()
    {
        var body = Everything.RequestBody(DevicePlatform.Windows);
        Assert.Equal(new[] { "keepEnrollmentData", "keepUserData", "useProtectedWipe" }, body.Keys.OrderBy(k => k));
        Assert.Equal(true, body["keepUserData"]);
    }

    [Fact]
    public void MacOS_GetsUnlockCodeAndObliteration_NeverKeepsUserData()
    {
        var body = Everything.RequestBody(DevicePlatform.MacOS);
        Assert.Equal(false, body["keepUserData"]);
        Assert.Equal("123456", body["macOsUnlockCode"]);
        Assert.Equal("doNotObliterate", body["obliterationBehavior"]);
        Assert.False(body.ContainsKey("useProtectedWipe"));
        Assert.False(body.ContainsKey("persistEsimDataPlan"));
    }

    [Fact]
    public void IOS_GetsUnlockCodeAndEsim_NoObliteration()
    {
        var body = Everything.RequestBody(DevicePlatform.IOS);
        Assert.Equal(false, body["keepUserData"]);
        Assert.Equal(true, body["persistEsimDataPlan"]);
        Assert.False(body.ContainsKey("obliterationBehavior"));
    }

    [Fact]
    public void ProtectedWipeOff_IsNotSent()
    {
        Assert.False(new WipeOptions().RequestBody(DevicePlatform.Windows).ContainsKey("useProtectedWipe"));
    }

    [Theory]
    [InlineData("Windows", DevicePlatform.Windows)]
    [InlineData("macOS", DevicePlatform.MacOS)]
    [InlineData("iOS", DevicePlatform.IOS)]
    [InlineData("iPadOS", DevicePlatform.IOS)]
    [InlineData("Android", DevicePlatform.Android)]
    [InlineData(null, DevicePlatform.Other)]
    public void Platform_FromOperatingSystem(string? os, DevicePlatform expected) =>
        Assert.Equal(expected, DevicePlatforms.FromOperatingSystem(os));

    [Fact]
    public async Task GraphWipe_PostsThePlatformBody()
    {
        var handler = new RecordingHandler();
        using var graph = new GraphService(new GraphConfig(), handler);

        var result = await graph.WipeDeviceAsync("dev-1", Everything, DevicePlatform.Windows, confirmed: true);

        Assert.True(result.Success);
        var (method, path, body) = Assert.Single(handler.Requests);
        Assert.Equal("POST", method);
        Assert.EndsWith("deviceManagement/managedDevices/dev-1/wipe", path);
        using var json = JsonDocument.Parse(body!);
        Assert.True(json.RootElement.GetProperty("useProtectedWipe").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("obliterationBehavior", out _));
    }

    [Fact]
    public async Task GraphWipe_RefusesWithoutConfirmation()
    {
        var handler = new RecordingHandler();
        using var graph = new GraphService(new GraphConfig(), handler);
        var result = await graph.WipeDeviceAsync("dev-1", new WipeOptions(), DevicePlatform.Windows);
        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GraphDisableEntra_PatchesAccountEnabledFalse()
    {
        var handler = new RecordingHandler();
        using var graph = new GraphService(new GraphConfig(), handler);
        await graph.SetEntraDeviceEnabledAsync("obj-1", false);
        var (method, path, body) = Assert.Single(handler.Requests);
        Assert.Equal("PATCH", method);
        Assert.EndsWith("devices/obj-1", path);
        Assert.Equal("{\"accountEnabled\":false}", body);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Method, string Path, string? Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath,
                request.Content == null ? null : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}

public class DeviceOffboardTests
{
    private static IntuneDevice Windows() => new()
    {
        Id = "intune-1", DeviceName = "PC-1", SerialNumber = "SER1", OperatingSystem = "Windows",
        AzureAdDeviceId = "aad-1",
    };

    private static IntuneDevice Mac() => new()
    {
        Id = "intune-2", DeviceName = "MAC-1", SerialNumber = "SER2", OperatingSystem = "macOS",
        AzureAdDeviceId = "aad-2",
    };

    private static readonly OffboardPlan Full = new()
    {
        Terminal = OffboardPlan.TerminalAction.Wipe,
        DeleteAutopilotRegistration = true,
        Entra = OffboardPlan.EntraAction.Delete,
        DeleteIntuneRecord = true,
    };

    [Fact]
    public async Task Windows_RunsEveryStepInTheSafeOrder()
    {
        var client = new FakeClient();
        var result = await new DeviceOffboarder(client).OffboardAsync(Windows(), Full);

        Assert.Equal(new[] { "wipe intune-1", "find-autopilot SER1", "delete-autopilot ap-1", "find-entra aad-1",
            "delete-entra obj-1", "delete-intune intune-1" }, client.Calls);
        Assert.Equal(new[] { "Wipe", "Delete Autopilot registration", "Delete Entra device", "Delete Intune record" },
            result.Steps.Select(s => s.Step));
        Assert.True(result.Success);
    }

    [Fact]
    public async Task KnownAutopilotIdentity_SkipsTheSerialLookup()
    {
        var client = new FakeClient();
        await new DeviceOffboarder(client).OffboardAsync(Windows(), Full, new AutopilotDevice { Id = "ap-known" });
        Assert.Contains("delete-autopilot ap-known", client.Calls);
        Assert.DoesNotContain(client.Calls, c => c.StartsWith("find-autopilot"));
    }

    [Fact]
    public async Task Mac_SkipsAutopilotWithTheReason()
    {
        var result = await new DeviceOffboarder(new FakeClient()).OffboardAsync(Mac(), Full);
        var step = result.Steps.Single(s => s.Step == "Delete Autopilot registration");
        Assert.Equal(Outcome.Skipped, step.Outcome);
        Assert.Equal("macOS devices have no Autopilot registration", step.Detail);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task AFailedStep_IsReported_AndTheRestStillRun()
    {
        var client = new FakeClient { FailWipe = true };
        var result = await new DeviceOffboarder(client).OffboardAsync(Windows(), Full);
        Assert.Equal(Outcome.Failed, result.Steps[0].Outcome);
        Assert.Equal("wipe refused", result.Steps[0].Detail);
        Assert.Contains("delete-intune intune-1", client.Calls);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task DisableEntra_PatchesInsteadOfDeleting_AndRetireIsUsed()
    {
        var client = new FakeClient();
        await new DeviceOffboarder(client).OffboardAsync(Windows(),
            new OffboardPlan { Terminal = OffboardPlan.TerminalAction.Retire, Entra = OffboardPlan.EntraAction.Disable });
        Assert.Equal(new[] { "retire intune-1", "find-entra aad-1", "disable-entra obj-1" }, client.Calls);
    }

    [Fact]
    public async Task NotEntraJoined_SkipsEntra()
    {
        var device = Windows();
        device.AzureAdDeviceId = "00000000-0000-0000-0000-000000000000";
        var result = await new DeviceOffboarder(new FakeClient()).OffboardAsync(device, Full);
        Assert.Equal("Device is not Entra-joined or registered", result.Steps.Single(s => s.Step == "Delete Entra device").Detail);
    }

    [Fact]
    public async Task Orphan_SkipsIntuneSteps_AndCleansTheDirectory()
    {
        var client = new FakeClient();
        var offboarder = new DeviceOffboarder(client);
        var autopilot = new AutopilotDevice { Id = "ap-9", SerialNumber = "SER9", AzureActiveDirectoryDeviceId = "aad-9" };
        var entra = await offboarder.FindOrphanEntraAsync(autopilot);

        var result = await offboarder.OffboardOrphanAsync("SER9", autopilot, entra, Full);

        Assert.Equal(new[] { "find-entra aad-9", "delete-autopilot ap-9", "delete-entra obj-1" }, client.Calls);
        Assert.Equal(Outcome.Skipped, result.Steps[0].Outcome);
        Assert.Equal("No Intune record — nothing can receive the command", result.Steps[0].Detail);
        Assert.Equal("Already gone", result.Steps[^1].Detail);
        Assert.Equal(DevicePlatform.Windows, result.Platform);
    }

    [Fact]
    public async Task Preview_WritesNothing_AndShowsTheExactRequests()
    {
        var client = new FakeClient();
        var plan = Full with { WipeOptions = new WipeOptions { UseProtectedWipe = true } };
        var steps = await new DeviceOffboarder(client).PreviewAsync(Windows(), plan);

        Assert.DoesNotContain(client.Calls, c => c.StartsWith("delete") || c.StartsWith("wipe"));
        Assert.Equal("POST managedDevices/intune-1/wipe {\"keepEnrollmentData\":false,\"keepUserData\":false,\"useProtectedWipe\":true}",
            steps[0].Detail);
        Assert.Equal("DELETE windowsAutopilotDeviceIdentities/ap-1", steps[1].Detail);
        Assert.Equal("DELETE devices/obj-1", steps[2].Detail);
        Assert.Equal("DELETE managedDevices/intune-1", steps[3].Detail);
    }

    [Fact]
    public void Summary_NamesEveryStep()
    {
        Assert.Equal("This will factory-reset, delete the Autopilot registration, delete the Entra device object and delete the Intune record for 2 device(s). This cannot be undone.",
            Full.Summary(2));
        Assert.Equal("No offboard steps are selected.",
            new OffboardPlan { Terminal = OffboardPlan.TerminalAction.None }.Summary(1));
        Assert.True(Full.CancelsPendingAction);
    }

    [Fact]
    public async Task GraphOffboard_RefusesWithoutConfirmation()
    {
        using var graph = new GraphService(new GraphConfig());
        var result = await graph.OffboardDeviceAsync(Windows(), Full);
        Assert.False(result.Success);
    }

    private sealed class FakeClient : IDeviceLifecycleClient
    {
        public List<string> Calls { get; } = new();
        public bool FailWipe { get; init; }

        private static GraphService.DeviceActionResult Ok(string id) => new() { Success = true, DeviceId = id };

        public Task<GraphService.DeviceActionResult> WipeAsync(IntuneDevice device, WipeOptions options)
        {
            Calls.Add($"wipe {device.Id}");
            return Task.FromResult(FailWipe
                ? new GraphService.DeviceActionResult { Success = false, DeviceId = device.Id, Message = "wipe refused" }
                : Ok(device.Id));
        }

        public Task<GraphService.DeviceActionResult> RetireAsync(string deviceId) { Calls.Add($"retire {deviceId}"); return Task.FromResult(Ok(deviceId)); }

        public Task<AutopilotDevice?> FindAutopilotBySerialAsync(string serialNumber)
        {
            Calls.Add($"find-autopilot {serialNumber}");
            return Task.FromResult<AutopilotDevice?>(new AutopilotDevice { Id = "ap-1", SerialNumber = serialNumber });
        }

        public Task<GraphService.DeviceActionResult> DeleteAutopilotAsync(string autopilotId) { Calls.Add($"delete-autopilot {autopilotId}"); return Task.FromResult(Ok(autopilotId)); }

        public Task<EntraDevice?> FindEntraDeviceAsync(string deviceId)
        {
            Calls.Add($"find-entra {deviceId}");
            return Task.FromResult<EntraDevice?>(new EntraDevice { Id = "obj-1", DeviceId = deviceId });
        }

        public Task<GraphService.DeviceActionResult> DeleteEntraDeviceAsync(string objectId) { Calls.Add($"delete-entra {objectId}"); return Task.FromResult(Ok(objectId)); }
        public Task<GraphService.DeviceActionResult> SetEntraDeviceEnabledAsync(string objectId, bool enabled) { Calls.Add($"{(enabled ? "enable" : "disable")}-entra {objectId}"); return Task.FromResult(Ok(objectId)); }
        public Task<GraphService.DeviceActionResult> DeleteManagedDeviceAsync(string deviceId) { Calls.Add($"delete-intune {deviceId}"); return Task.FromResult(Ok(deviceId)); }
    }
}
