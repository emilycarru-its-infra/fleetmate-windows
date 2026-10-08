namespace FleetMate.Core.Models.Reporting;

/// <summary>
/// One device from the Reporting tab's ReportMate device list, as global
/// search sees it: the fields worth typing (macOS parity: ReportingDeviceRecord).
/// </summary>
public sealed record ReportingDevice(
    string Serial,
    string Name,
    string? Hostname = null,
    string? User = null,
    string? AssetTag = null,
    string? Platform = null);
