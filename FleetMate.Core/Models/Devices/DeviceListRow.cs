namespace FleetMate.Core.Models.Devices;

/// <summary>
/// A row of the Devices list: an Intune record joined to its Windows Autopilot
/// identity, or an Autopilot identity Intune has no record of yet ("Not
/// Enrolled"). Every row answers every column and every filter, with "—"
/// where its sources have no value. The macOS client also joins each row to
/// its Apple School or Business Manager record; Windows has no client for
/// those services yet, so the Apple columns read "—" and the Apple filters
/// read "Not in Organization" for every row.
/// </summary>
public sealed class DeviceListRow
{
    public const string Missing = "—";
    public const string NotInOrganization = "Not in Organization";

    /// <summary>Autopilot-only rows carry this prefix, so no Intune action can be sent their ID.</summary>
    public const string AutopilotOnlyPrefix = "autopilot:";

    public IntuneDevice? Intune { get; }
    public AutopilotDevice? Autopilot { get; }

    public DeviceListRow(IntuneDevice? intune, AutopilotDevice? autopilot)
    {
        Intune = intune;
        Autopilot = autopilot;
    }

    /// <summary>Enrolled rows keep the Intune ID; every MDM action is keyed on it.</summary>
    public string Id => Intune?.Id ?? AutopilotOnlyPrefix + (Autopilot?.Id ?? "");
    public bool IsEnrolled => Intune != null;
    public string? SerialNumber => Blank(Intune?.SerialNumber) ?? Blank(Autopilot?.SerialNumber);

    /// <summary>Intune's operating system; an Autopilot-only row is Windows.</summary>
    public string? PlatformLabel => Blank(Intune?.OperatingSystem) ?? (Autopilot != null ? "Windows" : null);

    public bool IsWindows => PlatformLabel?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true;
    public bool IsApplePlatform
    {
        get
        {
            var p = (PlatformLabel ?? "").ToLowerInvariant();
            return p.Contains("mac") || p.Contains("ios") || p.Contains("ipad") || p.Contains("tvos") || p.Contains("visionos");
        }
    }

    // ── Column values ────────────────────────────────────────────────────

    public string NameText => Blank(Intune?.DeviceName) ?? Blank(Autopilot?.DisplayName) ?? Missing;
    public string SerialText => SerialNumber ?? Missing;
    public string PlatformText => PlatformLabel ?? Missing;
    public string OsText
    {
        get
        {
            var parts = new[] { Intune?.OperatingSystem, Intune?.OsVersion }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            return parts.Count == 0 ? Missing : string.Join(" ", parts);
        }
    }
    public string UserText => Blank(Intune?.UserDisplayName) ?? Blank(Intune?.UserPrincipalName) ?? Missing;
    public string ComplianceText => Intune == null ? "Not Enrolled" : Capitalize(Blank(Intune.ComplianceState) ?? "unknown");
    /// <summary>Drives the compliance dot: compliant, attention (orange) or neutral.</summary>
    public string ComplianceTone => Intune?.ComplianceState?.ToLowerInvariant() switch
    {
        "compliant" => "good",
        "noncompliant" or "ingraceperiod" or "error" or "conflict" => "attention",
        _ => "neutral",
    };
    public string LastSyncText => Intune?.LastSyncDateTime is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : Missing;
    public DateTime LastSyncSort => Intune?.LastSyncDateTime ?? DateTime.MinValue;

    /// <summary>The Apple organization's device management service; no Windows source yet.</summary>
    public string ServiceText => Missing;
    /// <summary>The Apple organization's status for the device; no Windows source yet.</summary>
    public string OrgStatusText => Missing;
    /// <summary>The device's grouping in its provisioning system: the Autopilot group tag here.</summary>
    public string GroupOrOrderText => Blank(Autopilot?.GroupTag) ?? Missing;

    public string ModelText => Blank(Intune?.Model) ?? Blank(Autopilot?.Model) ?? Missing;
    public string ManufacturerText => Blank(Intune?.Manufacturer) ?? Blank(Autopilot?.Manufacturer) ?? Missing;
    public string OwnershipText => Blank(Intune?.ManagedDeviceOwnerType) is { } o ? Capitalize(o) : Missing;
    public string MigrationText => Missing;
    public string PurchaseSourceText => Missing;
    public string AddedText => Missing;

    public string EnrollmentLabel => IsEnrolled ? "Enrolled" : "Not Enrolled";

    /// <summary>
    /// The value a Devices filter reads for this row. Every facet answers for
    /// every row, so a filter never drops a row just because one of its
    /// sources lacked the value.
    /// </summary>
    public string Value(DeviceFacet facet) => facet switch
    {
        DeviceFacet.ManagementService => NotInOrganization,
        DeviceFacet.OrgStatus => NotInOrganization,
        DeviceFacet.AppleOrganization => NotInOrganization,
        DeviceFacet.Platform => PlatformLabel ?? "Unknown",
        DeviceFacet.Compliance => ComplianceText,
        DeviceFacet.Manufacturer => Blank(Intune?.Manufacturer) ?? Blank(Autopilot?.Manufacturer) ?? "Unknown",
        DeviceFacet.Model => Blank(Intune?.Model) ?? Blank(Autopilot?.Model) ?? "Unknown",
        DeviceFacet.Ownership => Blank(Intune?.ManagedDeviceOwnerType) is { } o ? Capitalize(o) : "Unknown",
        DeviceFacet.Migration => "None",
        DeviceFacet.Enrollment => EnrollmentLabel,
        _ => "Unknown",
    };

    /// <summary>What the search box matches.</summary>
    public bool Matches(string text) =>
        new[] { NameText, SerialNumber, Intune?.UserPrincipalName, Intune?.UserDisplayName, Autopilot?.GroupTag }
            .Any(v => v?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>
/// The Devices filter categories, in the order the Filters panel lists them:
/// the Apple organization's first (macOS parity), then Intune's.
/// </summary>
public enum DeviceFacet
{
    ManagementService,
    OrgStatus,
    AppleOrganization,
    Platform,
    Compliance,
    Manufacturer,
    Model,
    Ownership,
    Migration,
    Enrollment,
}

public static class DeviceFacets
{
    public static string Title(this DeviceFacet facet) => facet switch
    {
        DeviceFacet.ManagementService => "Device Management Service",
        DeviceFacet.OrgStatus => "Organization Status",
        DeviceFacet.AppleOrganization => "Apple Organization",
        _ => facet.ToString(),
    };

    /// <summary>Each value of a facet across the rows, with how many rows carry it, most common first.</summary>
    public static List<(string Value, int Count)> Counts(IEnumerable<DeviceListRow> rows, DeviceFacet facet) =>
        rows.GroupBy(r => r.Value(facet), StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(v => v.Item2).ThenBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Rows passing every facet that has a selection: values within a facet
    /// are alternatives, facets combine.
    /// </summary>
    public static IEnumerable<DeviceListRow> Apply(IEnumerable<DeviceListRow> rows,
        IReadOnlyDictionary<DeviceFacet, HashSet<string>> selected) =>
        rows.Where(r => selected.All(s => s.Value.Count == 0 || s.Value.Contains(r.Value(s.Key))));
}

public static class DeviceListJoin
{
    /// <summary>Serials compare trimmed and uppercased; hand-entered ones carry whitespace.</summary>
    public static string Normalize(string serial) => serial.Trim().ToUpperInvariant();

    /// <summary>
    /// Every Intune record becomes a row, carrying its Autopilot identity:
    /// matched by the managed-device ID Autopilot records, else by serial.
    /// Autopilot identities no Intune record matches follow as their own
    /// "Not Enrolled" rows.
    /// </summary>
    public static List<DeviceListRow> Merge(IReadOnlyList<IntuneDevice> intune, IReadOnlyList<AutopilotDevice> autopilot)
    {
        var byManagedId = autopilot.Where(a => !string.IsNullOrEmpty(a.ManagedDeviceId)
                                               && a.ManagedDeviceId != "00000000-0000-0000-0000-000000000000")
            .GroupBy(a => a.ManagedDeviceId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var bySerial = autopilot.Where(a => !string.IsNullOrWhiteSpace(a.SerialNumber))
            .GroupBy(a => Normalize(a.SerialNumber!))
            .ToDictionary(g => g.Key, g => g.First());

        var matched = new HashSet<string>();
        var rows = new List<DeviceListRow>(intune.Count + autopilot.Count);
        foreach (var record in intune)
        {
            var identity = byManagedId.GetValueOrDefault(record.Id)
                ?? (string.IsNullOrWhiteSpace(record.SerialNumber) ? null : bySerial.GetValueOrDefault(Normalize(record.SerialNumber!)));
            if (identity != null) matched.Add(identity.Id);
            rows.Add(new DeviceListRow(record, identity));
        }
        rows.AddRange(autopilot.Where(a => !matched.Contains(a.Id)).Select(a => new DeviceListRow(null, a)));
        return rows;
    }
}
