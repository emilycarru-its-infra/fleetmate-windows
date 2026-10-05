using FleetMate.Core.Models.Devices;
using static FleetMate.Core.Services.GraphService;

namespace FleetMate.Core.Services.Devices;

/// <summary>The Graph calls an offboard makes, so the plan can be tested without a tenant.</summary>
public interface IDeviceLifecycleGraph
{
    /// <summary>Exact eq resolution; zero or several matches are refused.</summary>
    Task<DeviceResolution> ResolveManagedDeviceAsync(string identifier);
    /// <summary>The Autopilot registration whose serial is exactly <paramref name="serial"/>, or null.</summary>
    Task<AutopilotDevice?> FindAutopilotRegistrationAsync(string serial);
    Task<EntraDevice?> GetEntraDeviceByDeviceIdAsync(string deviceId);
    Task<DeviceActionResult> WipeDeviceAsync(IntuneDevice device, WipeOptions options, bool confirmed);
    Task<DeviceActionResult> RetireDeviceAsync(string deviceId, bool confirmed);
    Task<DeviceActionResult> DeleteAutopilotRegistrationAsync(string autopilotId, bool confirmed);
    Task<DeviceActionResult> SetEntraDeviceEnabledAsync(string objectId, bool enabled, bool confirmed);
    Task<DeviceActionResult> DeleteEntraDeviceAsync(string objectId, bool confirmed);
    Task<DeviceActionResult> DeleteManagedDeviceAsync(string deviceId, bool confirmed);
}

/// <summary>
/// Decommissions a device across Intune, Autopilot and Entra (macOS parity).
/// Steps run in the order that is safe: the terminal Intune action first (it
/// needs the record), then the Autopilot registration and the Entra object,
/// and the Intune record last, because deleting it cancels a wipe the device
/// hasn't picked up yet. A missing Intune record is a state, not a failure:
/// the directory records that outlive it are cleaned up instead.
/// </summary>
public sealed class DeviceOffboarder(IDeviceLifecycleGraph graph)
{
    public const string WipeStep = "Wipe";
    public const string RetireStep = "Retire";
    public const string TerminalStep = "Wipe or retire";
    public const string AutopilotStep = "Delete Autopilot registration";
    public const string IntuneRecordStep = "Delete Intune record";

    public Task<DeviceResolution> ResolveAsync(string identifier) => graph.ResolveManagedDeviceAsync(identifier);

    /// <summary>
    /// The directory records for an identifier with no Intune record, found
    /// through the Autopilot registration, which outlives every wipe and
    /// carries the Entra device id.
    /// </summary>
    public async Task<OrphanDeviceRecords> ResolveOrphanRecordsAsync(string identifier)
    {
        var autopilot = await graph.FindAutopilotRegistrationAsync(identifier);
        var entraId = Linked(autopilot?.AzureActiveDirectoryDeviceId);
        var entra = entraId == null ? null : await graph.GetEntraDeviceByDeviceIdAsync(entraId);
        return new OrphanDeviceRecords(autopilot, entra);
    }

    public async Task<OffboardResult> OffboardAsync(IntuneDevice device, OffboardPlan plan)
    {
        var platform = DevicePlatforms.From(device.OperatingSystem);
        var steps = new List<OffboardStepResult>();

        switch (plan.TerminalAction)
        {
            case OffboardTerminalAction.Wipe:
                steps.Add(Step(WipeStep, await graph.WipeDeviceAsync(device, plan.WipeOptions, confirmed: true)));
                break;
            case OffboardTerminalAction.Retire:
                steps.Add(Step(RetireStep, await graph.RetireDeviceAsync(device.Id, confirmed: true)));
                break;
            default:
                steps.Add(new(TerminalStep, OffboardOutcome.Skipped, "Not requested"));
                break;
        }

        if (plan.DeleteAutopilotRegistration)
        {
            var (id, reason) = await AutopilotTargetAsync(device, platform);
            steps.Add(id == null
                ? new(AutopilotStep, OffboardOutcome.Skipped, reason)
                : Step(AutopilotStep, await graph.DeleteAutopilotRegistrationAsync(id, confirmed: true)));
        }

        if (plan.EntraAction != OffboardEntraAction.None)
        {
            var (id, reason) = await EntraTargetAsync(device);
            steps.Add(await EntraStepAsync(plan.EntraAction, id, reason));
        }

        if (plan.DeleteIntuneRecord)
            steps.Add(Step(IntuneRecordStep, await graph.DeleteManagedDeviceAsync(device.Id, confirmed: true)));

        return new OffboardResult(device.SerialNumber ?? device.Id, device.DeviceName, platform, steps);
    }

    /// <summary>What an offboard would do, resolved against live data but writing nothing.</summary>
    public async Task<List<OffboardPlannedStep>> PreviewAsync(IntuneDevice device, OffboardPlan plan)
    {
        var platform = DevicePlatforms.From(device.OperatingSystem);
        var steps = new List<OffboardPlannedStep>();

        switch (plan.TerminalAction)
        {
            case OffboardTerminalAction.Wipe:
                steps.Add(new(WipeStep, true,
                    $"POST managedDevices/{device.Id}/wipe {WipeOptions.Describe(plan.WipeOptions.RequestBody(platform))}"));
                break;
            case OffboardTerminalAction.Retire:
                steps.Add(new(RetireStep, true, $"POST managedDevices/{device.Id}/retire"));
                break;
            default:
                steps.Add(new(TerminalStep, false, "Not requested"));
                break;
        }

        if (plan.DeleteAutopilotRegistration)
        {
            var (id, reason) = await AutopilotTargetAsync(device, platform);
            steps.Add(new(AutopilotStep, id != null,
                id != null ? $"DELETE windowsAutopilotDeviceIdentities/{id}" : reason ?? "skipped"));
        }

        if (plan.EntraAction != OffboardEntraAction.None)
        {
            var (id, reason) = await EntraTargetAsync(device);
            steps.Add(new(EntraLabel(plan.EntraAction), id != null,
                id != null ? EntraRequest(plan.EntraAction, id) : reason ?? "skipped"));
        }

        if (plan.DeleteIntuneRecord)
            steps.Add(new(IntuneRecordStep, true, $"DELETE managedDevices/{device.Id}"));

        return steps;
    }

    /// <summary>Clean up a device whose Intune record is already gone; every step that needed it is skipped with the reason.</summary>
    public async Task<OffboardResult> OffboardOrphanAsync(string identifier, OrphanDeviceRecords records, OffboardPlan plan)
    {
        var steps = new List<OffboardStepResult> { OrphanTerminal(plan) };

        if (plan.DeleteAutopilotRegistration)
        {
            steps.Add(records.Autopilot?.Id is { Length: > 0 } id
                ? Step(AutopilotStep, await graph.DeleteAutopilotRegistrationAsync(id, confirmed: true))
                : new(AutopilotStep, OffboardOutcome.Skipped, $"No Autopilot registration for {identifier}"));
        }

        if (plan.EntraAction != OffboardEntraAction.None)
            steps.Add(await EntraStepAsync(plan.EntraAction, NonEmpty(records.Entra?.Id),
                $"No Entra device object for {identifier}"));

        if (plan.DeleteIntuneRecord)
            steps.Add(new(IntuneRecordStep, OffboardOutcome.Skipped, "Already gone"));

        return new OffboardResult(records.Autopilot?.SerialNumber ?? identifier, records.Entra?.DisplayName,
            records.Autopilot != null ? DevicePlatform.Windows : DevicePlatform.Other, steps);
    }

    public static List<OffboardPlannedStep> PreviewOrphan(string identifier, OrphanDeviceRecords records, OffboardPlan plan)
    {
        var terminal = OrphanTerminal(plan);
        var steps = new List<OffboardPlannedStep> { new(terminal.Step, false, terminal.Detail ?? "") };

        if (plan.DeleteAutopilotRegistration)
        {
            var id = NonEmpty(records.Autopilot?.Id);
            steps.Add(new(AutopilotStep, id != null,
                id != null ? $"DELETE windowsAutopilotDeviceIdentities/{id}" : $"No Autopilot registration for {identifier}"));
        }

        if (plan.EntraAction != OffboardEntraAction.None)
        {
            var id = NonEmpty(records.Entra?.Id);
            steps.Add(new(EntraLabel(plan.EntraAction), id != null,
                id != null ? EntraRequest(plan.EntraAction, id) : $"No Entra device object for {identifier}"));
        }

        if (plan.DeleteIntuneRecord)
            steps.Add(new(IntuneRecordStep, false, "Already gone"));

        return steps;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static OffboardStepResult OrphanTerminal(OffboardPlan plan) => plan.TerminalAction switch
    {
        OffboardTerminalAction.Wipe => new(WipeStep, OffboardOutcome.Skipped, "No Intune record — nothing can receive the command"),
        OffboardTerminalAction.Retire => new(RetireStep, OffboardOutcome.Skipped, "No Intune record — nothing can receive the command"),
        _ => new(TerminalStep, OffboardOutcome.Skipped, "Not requested"),
    };

    private async Task<(string? Id, string? Reason)> AutopilotTargetAsync(IntuneDevice device, DevicePlatform platform)
    {
        if (platform != DevicePlatform.Windows)
            return (null, $"{platform.DisplayName()} devices have no Autopilot registration");
        if (string.IsNullOrWhiteSpace(device.SerialNumber))
            return (null, "Device has no serial number to match on");
        var autopilot = await graph.FindAutopilotRegistrationAsync(device.SerialNumber!);
        return NonEmpty(autopilot?.Id) is { } id ? (id, null) : (null, $"No Autopilot registration for {device.SerialNumber}");
    }

    private async Task<(string? Id, string? Reason)> EntraTargetAsync(IntuneDevice device)
    {
        if (Linked(device.AzureAdDeviceId) is not { } deviceId)
            return (null, "Device is not Entra-joined or registered");
        var entra = await graph.GetEntraDeviceByDeviceIdAsync(deviceId);
        return NonEmpty(entra?.Id) is { } id ? (id, null) : (null, $"No Entra device object for {deviceId}");
    }

    private async Task<OffboardStepResult> EntraStepAsync(OffboardEntraAction action, string? objectId, string? reason)
    {
        var label = EntraLabel(action);
        if (objectId == null) return new(label, OffboardOutcome.Skipped, reason);
        return Step(label, action == OffboardEntraAction.Delete
            ? await graph.DeleteEntraDeviceAsync(objectId, confirmed: true)
            : await graph.SetEntraDeviceEnabledAsync(objectId, enabled: false, confirmed: true));
    }

    private static string EntraLabel(OffboardEntraAction action) =>
        action == OffboardEntraAction.Delete ? "Delete Entra device" : "Disable Entra device";

    private static string EntraRequest(OffboardEntraAction action, string objectId) =>
        action == OffboardEntraAction.Delete ? $"DELETE devices/{objectId}" : $"PATCH devices/{objectId} {{\"accountEnabled\":false}}";

    private static OffboardStepResult Step(string label, DeviceActionResult result) =>
        result.Success
            ? new(label, OffboardOutcome.Succeeded)
            : new(label, OffboardOutcome.Failed, result.Message ?? "unknown error");

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Graph reports an all-zero GUID when nothing is linked.</summary>
    private static string? Linked(string? id) =>
        string.IsNullOrWhiteSpace(id) || id == "00000000-0000-0000-0000-000000000000" ? null : id;
}
