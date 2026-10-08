using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services.Activity;

namespace FleetMate.Core.Services;

/// <summary>Wiping a selection from the Devices list.</summary>
public partial class GraphService
{
    /// <summary>Wipe several devices, each with the body its own platform accepts.</summary>
    public async Task<List<DeviceActionResult>> WipeDevicesAsync(IEnumerable<IntuneDevice> devices, WipeOptions options, bool confirmed = false)
    {
        var list = devices.ToList();
        return await ActivityLog.Shared.RunAsync("Erase devices", "Microsoft Graph",
            list.Select(d => d.SerialNumber).OfType<string>().Where(s => s.Length > 0),
            async () =>
            {
                var results = new List<DeviceActionResult>();
                foreach (var device in list)
                    results.Add(await WipeDeviceAsync(device, options, confirmed));
                return results;
            });
    }
}
