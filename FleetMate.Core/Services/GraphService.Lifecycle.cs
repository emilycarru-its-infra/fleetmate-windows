using FleetMate.Core.Models.Devices;

namespace FleetMate.Core.Services;

/// <summary>Wiping a selection from the Devices list.</summary>
public partial class GraphService
{
    /// <summary>Wipe several devices, each with the body its own platform accepts.</summary>
    public async Task<List<DeviceActionResult>> WipeDevicesAsync(IEnumerable<IntuneDevice> devices, WipeOptions options, bool confirmed = false)
    {
        var results = new List<DeviceActionResult>();
        foreach (var device in devices)
            results.Add(await WipeDeviceAsync(device, options, confirmed));
        return results;
    }
}
