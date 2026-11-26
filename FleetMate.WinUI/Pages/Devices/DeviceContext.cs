using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services.Terminal;

namespace FleetMate.WinUI.Pages.Devices;

/// <summary>
/// The Terminal hook point: the selected devices, written to the
/// FLEETMATE_CONTEXT file every terminal session reads, as FleetMate.GUI's
/// <c>ContextPublisher.Devices</c> does. <c>App.Services.AgentContext</c> stays
/// null until the Terminal tab is ported; until then this does nothing.
/// </summary>
public static class DeviceContext
{
    public static void Devices(IEnumerable<DeviceListRow> rows) =>
        App.Services?.AgentContext?.SetSelection("device", rows.Where(r => r.Intune != null).Select(r => r.Intune!)
            .Select(d => new ContextSelection("device", d.Id, new Dictionary<string, string?>
            {
                ["name"] = d.DeviceName,
                ["serial"] = d.SerialNumber,
                ["user"] = d.UserPrincipalName,
                ["os"] = d.OperatingSystem,
                ["compliance"] = d.ComplianceState,
            })).ToList());
}
