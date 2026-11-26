using System.Text.RegularExpressions;
using FleetMate.Core.Models.Devices;

namespace FleetMate.Core.Devices;

/// <summary>How a state reads at a glance: the badge, dot or bar colour each app maps to its own theme.</summary>
public enum DeviceTone { Good, Caution, Attention, Neutral }

/// <summary>One label and value in a detail section; <see cref="Mono"/> sets identifiers in a fixed-width face.</summary>
public sealed record DeviceDetailRow(string Label, string Value, bool Mono = false);

/// <summary>
/// One titled section of the device detail panel and its rows. The async
/// sections (groups, policies, apps, AppleCare) are reserved with
/// <see cref="Placeholder"/> so they land in order, not at the bottom as they arrive.
/// </summary>
public sealed record DeviceDetailSection(string Title, IReadOnlyList<DeviceDetailRow> Rows)
{
    /// <summary>What a reserved section says until it loads (or a note such as "Not enrolled in Intune.").</summary>
    public string? Placeholder { get; init; }
    /// <summary>Which async content fills the section, if any.</summary>
    public DeviceDetailSlot Slot { get; init; } = DeviceDetailSlot.None;
    /// <summary>The compliance badge at the top of the Compliance section.</summary>
    public (DeviceTone Tone, string Text)? Badge { get; init; }
    /// <summary>The storage bar under Hardware: percent used.</summary>
    public double? StorageUsedPercent { get; init; }
}

/// <summary>The detail panel's async sections.</summary>
public enum DeviceDetailSlot { None, AppleCare, ActivationLock, Recovery, Groups, Compliance, DetectedApps }

/// <summary>
/// The device detail panel's content — the macOS DeviceDetailView, in its
/// order: Summary, the provisioning record (Apple organization or Windows
/// Autopilot), Device Management Service, Recovery, Hardware, Group
/// Membership, Compliance &amp; Conditional Access and Detected Apps. Both
/// Windows apps draw these sections; neither builds its own.
/// </summary>
public static class DeviceDetailSections
{
    /// <summary>How many detected apps are listed before "… and N more".</summary>
    public const int AppLimit = 50;

    /// <summary>Every section for an Intune device, with its Autopilot identity or Apple organization record when it has one.</summary>
    public static List<DeviceDetailSection> ForDevice(IntuneDevice d, AutopilotDevice? autopilot, AppleOrgView? apple)
    {
        var sections = new List<DeviceDetailSection> { Summary(d) };
        if (apple != null) sections.AddRange(AppleOrg(apple));
        if (autopilot != null) sections.Add(Autopilot(autopilot));
        sections.Add(Enrollment(d));
        if (RecoverySecretKinds.Available(d.OperatingSystem).Count > 0)
            sections.Add(new DeviceDetailSection("Recovery", Array.Empty<DeviceDetailRow>()) { Slot = DeviceDetailSlot.Recovery });
        sections.Add(Hardware(d));
        sections.Add(new DeviceDetailSection("Group Membership", Array.Empty<DeviceDetailRow>())
            { Slot = DeviceDetailSlot.Groups, Placeholder = "Loading groups..." });
        sections.Add(Compliance(d));
        sections.Add(new DeviceDetailSection("Detected Apps", Array.Empty<DeviceDetailRow>())
            { Slot = DeviceDetailSlot.DetectedApps, Placeholder = "Loading apps..." });
        return sections;
    }

    /// <summary>A device an Apple organization holds and Intune doesn't: only its organization record.</summary>
    public static List<DeviceDetailSection> ForAppleOnly(AppleOrgView apple)
    {
        var sections = AppleOrg(apple).ToList();
        sections.Add(NotEnrolled());
        return sections;
    }

    /// <summary>A device Autopilot knows and Intune doesn't: only its Autopilot record.</summary>
    public static List<DeviceDetailSection> ForAutopilotOnly(AutopilotDevice identity) => new() { Autopilot(identity), NotEnrolled() };

    /// <summary>The panel's title and the line under it.</summary>
    public static (string Title, string Subtitle) Header(IntuneDevice d) => (d.DeviceName, d.SerialNumber ?? d.Id);

    public static (string Title, string Subtitle) Header(AppleOrgDevice d) =>
        (string.IsNullOrWhiteSpace(d.Model) ? "Not Enrolled" : d.Model, d.SerialNumber);

    public static (string Title, string Subtitle) Header(AutopilotDevice a) =>
        (string.IsNullOrWhiteSpace(a.DisplayName) ? "Not Enrolled" : a.DisplayName!, a.SerialNumber ?? a.Id);

    private static DeviceDetailSection NotEnrolled() =>
        new("Device Management Service", Array.Empty<DeviceDetailRow>()) { Placeholder = "Not enrolled in Intune." };

    public static DeviceDetailSection Summary(IntuneDevice d) => new("Summary", Rows(
        ("Device Name", d.DeviceName, false),
        ("Management Name", d.ManagedDeviceName, false),
        ("Ownership", FormatOwnership(d.ManagedDeviceOwnerType), false),
        ("Manufacturer", d.Manufacturer, false),
        ("Model", d.Model, false),
        ("OS", FormatOs(d), false),
        ("Last Check-in", d.LastSyncDateTime?.ToString("yyyy-MM-dd HH:mm"), false),
        ("Primary User", d.UserDisplayName ?? d.UserPrincipalName, false)));

    /// <summary>The section Intune's own record fills, titled for what Intune is here: the device management service.</summary>
    public static DeviceDetailSection Enrollment(IntuneDevice d) => new("Device Management Service", Rows(
        ("Intune Device ID", d.Id, true),
        ("Entra Device ID", d.AzureAdDeviceId, true),
        ("Enrollment Type", FormatEnrollmentType(d.DeviceEnrollmentType), false),
        ("Enrollment Profile", d.EnrollmentProfileName, false),
        ("Supervised", YesNo(d.IsSupervised), false),
        ("Enrolled", d.EnrolledDateTime?.ToString("yyyy-MM-dd HH:mm"), false),
        ("Registration State", d.DeviceRegistrationState, false),
        ("Management Agent", d.ManagementAgent, false),
        ("Join Type", d.JoinType, false)));

    public static DeviceDetailSection Hardware(IntuneDevice d)
    {
        var rows = new List<(string, string?, bool)>
        {
            ("Serial Number", d.SerialNumber, true),
            ("SKU Family", d.SkuFamily, false),
            ("Total Storage", d.TotalStorageSpaceInBytes is > 0 ? FormatBytes(d.TotalStorageSpaceInBytes.Value) : null, false),
            ("Free Storage", d.FreeStorageSpaceInBytes is > 0 ? FormatBytes(d.FreeStorageSpaceInBytes.Value) : null, false),
            ("Physical Memory", d.PhysicalMemoryInBytes is > 0 ? FormatBytes(d.PhysicalMemoryInBytes.Value) : null, false),
            ("Wi-Fi MAC", d.WiFiMacAddress, true),
            ("Ethernet MAC", d.EthernetMacAddress, true),
        };
        if (!string.IsNullOrEmpty(d.Imei) || !string.IsNullOrEmpty(d.Meid))
        {
            rows.Add(("IMEI", d.Imei, true));
            rows.Add(("MEID", d.Meid, true));
            rows.Add(("Phone Number", d.PhoneNumber, false));
            rows.Add(("Carrier", d.SubscriberCarrier, false));
        }
        return new DeviceDetailSection("Hardware", Rows(rows.ToArray())) { StorageUsedPercent = d.StorageUsedPercent };
    }

    public static DeviceDetailSection Compliance(IntuneDevice d) => new("Compliance & Conditional Access", Rows(
        ("Management State", d.ManagementState, false),
        ("Azure AD Registered", YesNo(d.AzureAdRegistered), false),
        ("Device Category", d.DeviceCategoryDisplayName, false),
        ("Encrypted", YesNo(d.IsEncrypted), false),
        ("Jailbroken", d.JailBroken, false)))
    {
        Slot = DeviceDetailSlot.Compliance,
        Badge = ComplianceBadge(d.ComplianceState),
    };

    public static DeviceDetailSection Autopilot(AutopilotDevice a) => new("Windows Autopilot", Rows(
        ("Group Tag", a.GroupTag, false),
        ("Enrollment State", a.EnrollmentStateLabel(), false),
        ("Deployment Profile", a.ProfileStatusLabel(), false),
        ("Purchase Order", a.PurchaseOrderIdentifier, false),
        ("Manufacturer", a.Manufacturer, false),
        ("Model", a.Model, false),
        ("System Family", a.SystemFamily, false),
        // Graph reports a device that never checked in as 0001-01-01.
        ("Last Contacted", a.LastContactedDateTime is { Year: > 1 } contacted ? contacted.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : null, false),
        ("Assigned User", a.UserPrincipalName, false),
        ("Autopilot ID", a.Id, true)));

    /// <summary>
    /// The organization's section, titled for its service: status, assigned
    /// service, order and purchase, dates, MAC addresses, migration and the
    /// Activation Lock row; then AppleCare, read when the device is selected.
    /// </summary>
    public static IEnumerable<DeviceDetailSection> AppleOrg(AppleOrgView apple)
    {
        var d = apple.Device;
        var source = d.PurchaseSourceLabel();
        yield return new DeviceDetailSection(apple.ServiceName, Rows(
            ("Status", d.OrgStatusLabel(), false),
            ("Management Service", apple.ServerName ?? (d.AssignedServerId == null ? "Not assigned" : d.AssignedServerId), false),
            ("Model", d.Model, false),
            ("Product Family", d.ProductFamily, false),
            ("Order Number", d.OrderNumber, false),
            ("Purchase Source", source != DeviceListRow.Missing ? source : null, false),
            ("Order Date", d.OrderDate?.ToLocalTime().ToString("yyyy-MM-dd"), false),
            ("Added", d.AddedToOrg?.ToLocalTime().ToString("yyyy-MM-dd"), false),
            ("Released", d.ReleasedFromOrg?.ToLocalTime().ToString("yyyy-MM-dd"), false),
            ("Wi-Fi MAC", d.WifiMacAddresses.Count > 0 ? string.Join(", ", d.WifiMacAddresses) : null, true),
            ("Ethernet MAC", d.EthernetMacAddresses.Count > 0 ? string.Join(", ", d.EthernetMacAddresses) : null, true),
            ("Migration", d.MigrationStatus == null ? (d.IsMigrationCapable == true ? "Eligible" : null) : d.MigrationLabel(), false),
            ("Migration Deadline", d.MigrationDeadline?.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), false)))
        {
            Slot = apple.ReadsActivationLock ? DeviceDetailSlot.ActivationLock : DeviceDetailSlot.None,
        };
        if (apple.ReadsAppleCare)
            yield return new DeviceDetailSection("AppleCare", Array.Empty<DeviceDetailRow>())
                { Slot = DeviceDetailSlot.AppleCare, Placeholder = "Loading coverage..." };
    }

    /// <summary>One AppleCare agreement as a row: "Active · 2025-01-01 – 2028-01-01".</summary>
    public static DeviceDetailRow AppleCareRow(AppleCareAgreement a)
    {
        var span = string.Join(" – ", new[] { a.Start, a.End }.Where(x => x != null).Select(x => x!.Value.ToLocalTime().ToString("yyyy-MM-dd")));
        return new DeviceDetailRow(a.Description,
            string.Join(" · ", new[] { a.IsCanceled ? "Canceled" : a.Status, span }.Where(x => !string.IsNullOrEmpty(x))));
    }

    // MARK: - Tones

    /// <summary>The Compliance section's badge: Compliant, Non-Compliant, In Grace Period, or the state as Intune names it.</summary>
    public static (DeviceTone Tone, string Text) ComplianceBadge(string? state) => state?.ToLowerInvariant() switch
    {
        "compliant" => (DeviceTone.Good, "Compliant"),
        "noncompliant" => (DeviceTone.Attention, "Non-Compliant"),
        "ingrace" or "ingraceperiod" => (DeviceTone.Caution, "In Grace Period"),
        _ => (DeviceTone.Neutral, state ?? "Unknown"),
    };

    /// <summary>A compliance policy row's dot: compliant, non-compliant or error, conflict, other.</summary>
    public static DeviceTone PolicyTone(string? state) => state?.ToLowerInvariant() switch
    {
        "compliant" => DeviceTone.Good,
        "noncompliant" or "error" => DeviceTone.Attention,
        "conflict" => DeviceTone.Caution,
        _ => DeviceTone.Neutral,
    };

    /// <summary>The storage bar: over 90% used needs attention, over 70% caution.</summary>
    public static DeviceTone StorageTone(double usedPercent) =>
        usedPercent > 90 ? DeviceTone.Attention : usedPercent > 70 ? DeviceTone.Caution : DeviceTone.Good;

    /// <summary>A list row's compliance dot, from <see cref="DeviceListRow.ComplianceTone"/>.</summary>
    public static DeviceTone RowTone(string? listTone) => listTone switch
    {
        "good" => DeviceTone.Good,
        "attention" => DeviceTone.Attention,
        _ => DeviceTone.Neutral,
    };

    // MARK: - Format

    public static string FormatOs(IntuneDevice d)
    {
        if (d.OperatingSystem == null) return "-";
        return d.OsVersion != null ? $"{d.OperatingSystem} {d.OsVersion}" : d.OperatingSystem;
    }

    public static string? FormatOwnership(string? ownership) => ownership?.ToLowerInvariant() switch
    {
        null or "" => null,
        "company" => "Corporate",
        "personal" => "Personal",
        _ => char.ToUpperInvariant(ownership[0]) + ownership[1..],
    };

    /// <summary>Intune's camelCase enrollment type with spaces: "windowsAzureADJoin" reads "windows Azure ADJoin".</summary>
    public static string? FormatEnrollmentType(string? type) =>
        string.IsNullOrEmpty(type) ? null : Regex.Replace(type, "([a-z])([A-Z])", "$1 $2");

    public static string FormatBytes(long bytes)
    {
        var gb = (double)bytes / 1_073_741_824;
        return gb >= 1 ? $"{gb:F1} GB" : $"{(double)bytes / 1_048_576:F0} MB";
    }

    /// <summary>"12 apps detected", and "… and 30 more" past <see cref="AppLimit"/>.</summary>
    public static string AppsHeadline(int count) => $"{count} apps detected";

    public static string AppsMore(int count) => $"... and {count - AppLimit} more";

    private static string? YesNo(bool? value) => value == true ? "Yes" : value == false ? "No" : null;

    /// <summary>The rows that have a value; an empty value leaves its row out, as on the Mac.</summary>
    private static IReadOnlyList<DeviceDetailRow> Rows(params (string Label, string? Value, bool Mono)[] rows) =>
        rows.Where(r => !string.IsNullOrEmpty(r.Value)).Select(r => new DeviceDetailRow(r.Label, r.Value!, r.Mono)).ToList();
}

/// <summary>
/// An Apple organization record to show beside a device: the record, the
/// organization's service name ("Apple School Manager"), the device
/// management service it is assigned to, and whether AppleCare and the
/// Activation Lock state are read when the device is selected.
/// </summary>
public sealed record AppleOrgView(AppleOrgDevice Device, string ServiceName, string? ServerName,
    bool ReadsAppleCare, bool ReadsActivationLock);
