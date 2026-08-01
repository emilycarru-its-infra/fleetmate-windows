using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using FleetMate.Core.Models.Devices;

namespace FleetMate.WinUI.ViewModels;

/// <summary>
/// Row display model for the Devices list. Wraps an <see cref="IntuneDevice"/> and
/// exposes formatted columns + a compliance colour so the list needs no converters.
/// </summary>
public sealed class DeviceRowViewModel
{
    public IntuneDevice Device { get; }

    public DeviceRowViewModel(IntuneDevice device) => Device = device;

    public string Name => string.IsNullOrEmpty(Device.DeviceName) ? "(unnamed)" : Device.DeviceName;
    public string Serial => string.IsNullOrEmpty(Device.SerialNumber) ? "—" : Device.SerialNumber!;
    public string Os => string.Join(" ", new[] { Device.OperatingSystem, Device.OsVersion }
        .Where(s => !string.IsNullOrEmpty(s))).Trim() is { Length: > 0 } os ? os : "—";
    public string User => Device.UserDisplayName ?? Device.UserPrincipalName ?? "—";
    public string LastSync => Device.LastSyncDateTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";

    public string Compliance => string.IsNullOrEmpty(Device.ComplianceState) ? "unknown" : Device.ComplianceState!;

    public Brush ComplianceBrush => new SolidColorBrush(
        Device.IsCompliant ? Colors.Green
        : Device.ComplianceState?.Equals("noncompliant", StringComparison.OrdinalIgnoreCase) == true ? Colors.Red
        : Colors.Gray);

    /// <summary>Free-text match for the search box.</summary>
    public bool Matches(string q) =>
        Name.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Serial.Contains(q, StringComparison.OrdinalIgnoreCase)
        || User.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Os.Contains(q, StringComparison.OrdinalIgnoreCase);
}
