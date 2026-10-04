using FleetMate.Core.Models.Devices;
using static FleetMate.Core.Models.Devices.OffboardStepResult;

namespace FleetMate.Core.Services.Devices;

/// <summary>The Graph calls a decommission needs; GraphService implements it, tests fake it.</summary>
public interface IDeviceLifecycleClient
{
    Task<GraphService.DeviceActionResult> WipeAsync(IntuneDevice device, WipeOptions options);
    Task<GraphService.DeviceActionResult> RetireAsync(string deviceId);
    Task<AutopilotDevice?> FindAutopilotBySerialAsync(string serialNumber);
    Task<GraphService.DeviceActionResult> DeleteAutopilotAsync(string autopilotId);
    Task<EntraDevice?> FindEntraDeviceAsync(string deviceId);
    Task<GraphService.DeviceActionResult> DeleteEntraDeviceAsync(string objectId);
    Task<GraphService.DeviceActionResult> SetEntraDeviceEnabledAsync(string objectId, bool enabled);
    Task<GraphService.DeviceActionResult> DeleteManagedDeviceAsync(string deviceId);
}

/// <summary>
/// Device decommission across Intune, Autopilot and Entra — the three records
/// that outlive a wipe and keep a machine looking enrolled long after it is
/// gone. Ported from the macOS client's GraphService+DeviceLifecycle.
///
/// Steps run in the order they are safe in: the terminal Intune action first
/// (it needs the record intact), then the Autopilot registration and the Entra
/// object, and the Intune record last because deleting it cancels a wipe the
/// device has not picked up yet. Every step runs and reports on its own; one
/// failing does not stop the rest, so a half-cleaned machine is reported
/// precisely rather than abandoned.
/// </summary>
public sealed class DeviceOffboarder(IDeviceLifecycleClient client)
{
    private const string AutopilotStep = "Delete Autopilot registration";
    private const string IntuneStep = "Delete Intune record";

    public async Task<OffboardResult> OffboardAsync(IntuneDevice device, OffboardPlan plan, AutopilotDevice? knownAutopilot = null)
    {
        var platform = device.Platform();
        var steps = new List<OffboardStepResult>();

        switch (plan.Terminal)
        {
            case OffboardPlan.TerminalAction.Wipe:
                steps.Add(Step("Wipe", await client.WipeAsync(device, plan.WipeOptions)));
                break;
            case OffboardPlan.TerminalAction.Retire:
                steps.Add(Step("Retire", await client.RetireAsync(device.Id)));
                break;
            default:
                steps.Add(new("Wipe or retire", StepOutcome.Skipped, "Not requested"));
                break;
        }

        if (plan.DeleteAutopilotRegistration)
        {
            var (autopilotId, reason) = await AutopilotTargetAsync(device, platform, knownAutopilot);
            steps.Add(autopilotId == null
                ? new(AutopilotStep, StepOutcome.Skipped, reason)
                : Step(AutopilotStep, await client.DeleteAutopilotAsync(autopilotId)));
        }

        if (plan.Entra != OffboardPlan.EntraAction.None)
        {
            var (objectId, reason) = await EntraTargetAsync(device);
            steps.Add(await EntraStepAsync(plan.Entra, objectId, reason));
        }

        if (plan.DeleteIntuneRecord)
            steps.Add(Step(IntuneStep, await client.DeleteManagedDeviceAsync(device.Id)));

        return new OffboardResult(Blank(device.SerialNumber) ?? device.Id, device.DeviceName, platform, steps);
    }

    /// <summary>
    /// Decommission a device whose Intune record is already gone. Every step
    /// that needs a managedDevice is reported skipped with the reason, and
    /// every step that is still possible runs.
    /// </summary>
    public async Task<OffboardResult> OffboardOrphanAsync(string identifier, AutopilotDevice? autopilot, EntraDevice? entra, OffboardPlan plan)
    {
        var steps = new List<OffboardStepResult>
        {
            plan.Terminal == OffboardPlan.TerminalAction.None
                ? new("Wipe or retire", StepOutcome.Skipped, "Not requested")
                : new(plan.Terminal == OffboardPlan.TerminalAction.Wipe ? "Wipe" : "Retire", StepOutcome.Skipped,
                    "No Intune record — nothing can receive the command"),
        };

        if (plan.DeleteAutopilotRegistration)
        {
            steps.Add(Blank(autopilot?.Id) is { } autopilotId
                ? Step(AutopilotStep, await client.DeleteAutopilotAsync(autopilotId))
                : new(AutopilotStep, StepOutcome.Skipped, $"No Autopilot registration for {identifier}"));
        }

        if (plan.Entra != OffboardPlan.EntraAction.None)
            steps.Add(await EntraStepAsync(plan.Entra, Blank(entra?.Id), $"No Entra device object for {identifier}"));

        if (plan.DeleteIntuneRecord)
            steps.Add(new(IntuneStep, StepOutcome.Skipped, "Already gone"));

        return new OffboardResult(Blank(autopilot?.SerialNumber) ?? identifier, entra?.DisplayName,
            autopilot != null ? DevicePlatform.Windows : DevicePlatform.Other, steps);
    }

    /// <summary>
    /// The directory records left for a device with no Intune record. The
    /// Autopilot identity is the way in: it carries the Entra device id.
    /// </summary>
    public async Task<EntraDevice?> FindOrphanEntraAsync(AutopilotDevice? autopilot) =>
        AutopilotLabels.Linked(autopilot?.AzureActiveDirectoryDeviceId) is { } deviceId
            ? await client.FindEntraDeviceAsync(deviceId)
            : null;

    /// <summary>
    /// What an offboard would do, resolved against live data but writing
    /// nothing. Shares every lookup with OffboardAsync, so the ids shown are
    /// the ids that would be deleted.
    /// </summary>
    public async Task<List<OffboardPlannedStep>> PreviewAsync(IntuneDevice device, OffboardPlan plan, AutopilotDevice? knownAutopilot = null)
    {
        var platform = device.Platform();
        var steps = new List<OffboardPlannedStep>();

        steps.Add(plan.Terminal switch
        {
            OffboardPlan.TerminalAction.Wipe => new("Wipe", true,
                $"POST managedDevices/{device.Id}/wipe {Describe(plan.WipeOptions.RequestBody(platform))}"),
            OffboardPlan.TerminalAction.Retire => new("Retire", true, $"POST managedDevices/{device.Id}/retire"),
            _ => new("Wipe or retire", false, "Not requested"),
        });

        if (plan.DeleteAutopilotRegistration)
        {
            var (autopilotId, reason) = await AutopilotTargetAsync(device, platform, knownAutopilot);
            steps.Add(new(AutopilotStep, autopilotId != null,
                autopilotId != null ? $"DELETE windowsAutopilotDeviceIdentities/{autopilotId}" : reason ?? "skipped"));
        }

        if (plan.Entra != OffboardPlan.EntraAction.None)
        {
            var (objectId, reason) = await EntraTargetAsync(device);
            var delete = plan.Entra == OffboardPlan.EntraAction.Delete;
            steps.Add(new(delete ? "Delete Entra device" : "Disable Entra device", objectId != null,
                objectId == null ? reason ?? "skipped"
                    : delete ? $"DELETE devices/{objectId}" : $"PATCH devices/{objectId} {{\"accountEnabled\":false}}"));
        }

        if (plan.DeleteIntuneRecord)
            steps.Add(new(IntuneStep, true, $"DELETE managedDevices/{device.Id}"));

        return steps;
    }

    /// <summary>A request body as the dry run shows it — sorted, so two runs of one plan print identically.</summary>
    public static string Describe(IReadOnlyDictionary<string, object> body) =>
        "{" + string.Join(",", body.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => body[k] switch
        {
            bool b => $"\"{k}\":{(b ? "true" : "false")}",
            string s => $"\"{k}\":\"{s}\"",
            var v => $"\"{k}\":{v}",
        })) + "}";

    private async Task<(string? Id, string? Reason)> AutopilotTargetAsync(IntuneDevice device, DevicePlatform platform, AutopilotDevice? known)
    {
        if (platform != DevicePlatform.Windows)
            return (null, $"{platform.DisplayName()} devices have no Autopilot registration");
        if (Blank(known?.Id) is { } knownId) return (knownId, null);
        if (Blank(device.SerialNumber) is not { } serial)
            return (null, "Device has no serial number to match on");
        var autopilot = await client.FindAutopilotBySerialAsync(serial);
        return Blank(autopilot?.Id) is { } id ? (id, null) : (null, $"No Autopilot registration for {serial}");
    }

    private async Task<(string? Id, string? Reason)> EntraTargetAsync(IntuneDevice device)
    {
        if (AutopilotLabels.Linked(device.AzureAdDeviceId) is not { } deviceId)
            return (null, "Device is not Entra-joined or registered");
        var entra = await client.FindEntraDeviceAsync(deviceId);
        return Blank(entra?.Id) is { } objectId ? (objectId, null) : (null, $"No Entra device object for {deviceId}");
    }

    private async Task<OffboardStepResult> EntraStepAsync(OffboardPlan.EntraAction action, string? objectId, string? skipReason)
    {
        var label = action == OffboardPlan.EntraAction.Delete ? "Delete Entra device" : "Disable Entra device";
        if (objectId == null) return new(label, StepOutcome.Skipped, skipReason);
        return Step(label, action == OffboardPlan.EntraAction.Delete
            ? await client.DeleteEntraDeviceAsync(objectId)
            : await client.SetEntraDeviceEnabledAsync(objectId, false));
    }

    private static OffboardStepResult Step(string label, GraphService.DeviceActionResult? result) =>
        result == null ? new(label, StepOutcome.Failed, "No response — not authenticated?")
        : result.Success ? new(label, StepOutcome.Succeeded)
        : new(label, StepOutcome.Failed, result.Message);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
