namespace FleetMate.Core.Models.Devices;

/// <summary>
/// A row of the Devices list: an Intune record joined to its Windows Autopilot
/// identity and its Apple School or Business Manager record, or a device one of
/// those knows and Intune does not yet — an Autopilot identity ("Registered,
/// Not Enrolled") or an Apple organization device ("Not Enrolled"), so it can
/// still be assigned. Every row answers every column and every filter, with
/// "—" where its sources have no value.
/// </summary>
public sealed class DeviceListRow
{
    public const string Missing = "—";
    public const string NotInOrganization = "Not in Organization";

    /// <summary>Autopilot-only rows carry this prefix, so no Intune action can be sent their ID.</summary>
    public const string AutopilotOnlyPrefix = "autopilot:";
    /// <summary>Apple-organization-only rows carry this prefix, for the same reason.</summary>
    public const string OrgOnlyPrefix = "apple-org:";

    public IntuneDevice? Intune { get; }
    public AutopilotDevice? Autopilot { get; }
    /// <summary>Where a Windows device stands between Autopilot and Intune; null for other platforms.</summary>
    public AutopilotRegistration? Registration { get; }
    /// <summary>The Apple School or Business Manager record of the same serial.</summary>
    public AppleOrgDevice? Apple { get; }
    /// <summary>The name of the device management service the Apple record is assigned to.</summary>
    public string? ServerName { get; }
    /// <summary>The organization holding the device, as <see cref="AppleOrgProfile.Labels"/> names it.</summary>
    public string? OrgName { get; }

    public DeviceListRow(IntuneDevice? intune, AutopilotDevice? autopilot, AutopilotRegistration? registration = null,
        AppleOrgDevice? apple = null, string? serverName = null, string? orgName = null)
    {
        Intune = intune;
        Autopilot = autopilot;
        Registration = registration;
        Apple = apple;
        ServerName = serverName;
        OrgName = orgName;
    }

    /// <summary>The same row carrying an Apple organization record.</summary>
    public DeviceListRow WithApple(AppleOrgDevice? apple, string? serverName, string? orgName) =>
        new(Intune, Autopilot, Registration, apple, serverName, orgName);

    /// <summary>Enrolled rows keep the Intune ID; every MDM action is keyed on it.</summary>
    public string Id => Intune?.Id
        ?? (Apple != null ? OrgOnlyPrefix + Apple.SerialNumber : AutopilotOnlyPrefix + (Autopilot?.Id ?? ""));
    public bool IsEnrolled => Intune != null;
    public string? SerialNumber => Blank(Intune?.SerialNumber) ?? Blank(Apple?.SerialNumber) ?? Blank(Autopilot?.SerialNumber);

    /// <summary>
    /// Intune's operating system; for a row Intune lacks, the platform its
    /// Apple product family implies, or Windows for an Autopilot identity.
    /// </summary>
    public string? PlatformLabel => Blank(Intune?.OperatingSystem)
        ?? AppleOrgLabels.PlatformFromFamily(Apple?.ProductFamily)
        ?? (Autopilot != null ? "Windows" : null);

    public bool IsWindows => PlatformLabel?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true;
    public bool IsApplePlatform
    {
        get
        {
            if (Apple != null) return true;
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

    /// <summary>
    /// The service managing the device: the Apple organization's assigned
    /// service for an Apple device it holds, Intune for an enrolled Windows
    /// device.
    /// </summary>
    public string ServiceText => Apple != null ? ServerName ?? Missing : IsWindows && IsEnrolled ? "Intune" : Missing;
    /// <summary>The Apple organization's status for the device, or a Windows device's Autopilot registration.</summary>
    public string OrgStatusText => Apple?.OrgStatusLabel() ?? Registration?.Label() ?? Missing;
    /// <summary>The device's grouping in its provisioning system: the Apple order number, or the Autopilot group tag.</summary>
    public string GroupOrOrderText => Blank(Apple?.OrderNumber) ?? Blank(Autopilot?.GroupTag) ?? Missing;

    public string ModelText => Blank(Intune?.Model) ?? Blank(Apple?.Model) ?? Blank(Autopilot?.Model) ?? Missing;
    public string ManufacturerText => Blank(Intune?.Manufacturer) ?? (Apple != null ? "Apple" : null) ?? Blank(Autopilot?.Manufacturer) ?? Missing;
    public string OwnershipText => Blank(Intune?.ManagedDeviceOwnerType) is { } o ? Capitalize(o) : Missing;
    public string MigrationText => Apple?.MigrationLabel() ?? Missing;
    /// <summary>Apple's purchase source, or the purchase order an Autopilot registration carries.</summary>
    public string PurchaseSourceText => Apple?.PurchaseSourceLabel() ?? Blank(Autopilot?.PurchaseOrderIdentifier) ?? Missing;
    public string AddedText => Apple?.AddedToOrg is { } added ? added.ToLocalTime().ToString("yyyy-MM-dd") : Missing;
    public string AddedSort => Apple?.AddedToOrg?.ToString("o") ?? "";

    public string EnrollmentLabel => IsEnrolled ? "Enrolled" : "Not Enrolled";

    /// <summary>
    /// The value a Devices filter reads for this row. Every facet answers for
    /// every row, so a filter never drops a row just because one of its
    /// sources lacked the value.
    /// </summary>
    public string Value(DeviceFacet facet) => facet switch
    {
        DeviceFacet.ManagementService => Apple != null ? ServerName ?? "No Service"
            : IsWindows ? (IsEnrolled ? "Intune" : "No Service") : NotInOrganization,
        DeviceFacet.OrgStatus => Apple?.OrgStatusLabel() ?? Registration?.Label() ?? NotInOrganization,
        DeviceFacet.AppleOrganization => Apple == null ? NotInOrganization : OrgName ?? "Apple Organization",
        DeviceFacet.GroupTag => AutopilotValue(a => a.GroupTagLabel()),
        DeviceFacet.DeploymentProfile => AutopilotValue(a => a.ProfileStatusLabel()),
        DeviceFacet.AutopilotEnrollment => AutopilotValue(a => a.EnrollmentStateLabel()),
        DeviceFacet.Platform => PlatformLabel ?? "Unknown",
        DeviceFacet.Compliance => ComplianceText,
        DeviceFacet.Manufacturer => Blank(Intune?.Manufacturer) ?? (Apple != null ? "Apple" : null) ?? Blank(Autopilot?.Manufacturer) ?? "Unknown",
        DeviceFacet.Model => Blank(Intune?.Model) ?? Blank(Apple?.Model) ?? Blank(Autopilot?.Model) ?? "Unknown",
        DeviceFacet.Ownership => Blank(Intune?.ManagedDeviceOwnerType) is { } o ? Capitalize(o) : "Unknown",
        DeviceFacet.Migration => Apple?.MigrationLabel() ?? "None",
        DeviceFacet.Enrollment => EnrollmentLabel,
        _ => "Unknown",
    };

    /// <summary>
    /// Autopilot facet values: the identity's own, "Not Registered" for a
    /// Windows device with none, and "Not in Autopilot" for other platforms.
    /// </summary>
    private string AutopilotValue(Func<AutopilotDevice, string> read) =>
        Autopilot != null ? read(Autopilot) : Registration == null ? "Not in Autopilot" : "Not Registered";

    /// <summary>What the search box matches.</summary>
    public bool Matches(string text) =>
        new[] { NameText, SerialNumber, Intune?.UserPrincipalName, Intune?.UserDisplayName, Autopilot?.GroupTag, Apple?.OrderNumber }
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
    GroupTag,
    DeploymentProfile,
    AutopilotEnrollment,
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
        DeviceFacet.GroupTag => "Group Tag",
        DeviceFacet.DeploymentProfile => "Deployment Profile",
        DeviceFacet.AutopilotEnrollment => "Autopilot Enrollment",
        _ => facet.ToString(),
    };

    /// <summary>Facets with nothing to say until Autopilot identities are read.</summary>
    public static readonly IReadOnlySet<DeviceFacet> AutopilotOnly =
        new HashSet<DeviceFacet> { DeviceFacet.GroupTag, DeviceFacet.DeploymentProfile, DeviceFacet.AutopilotEnrollment };

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
    /// Every Intune record becomes a row. With Autopilot identities read, each
    /// is matched to an Intune record: by the managed device ID Autopilot
    /// links, then the Entra device ID, then serial (preferring the most
    /// recently synced record, since a re-enrolment leaves the old one behind).
    /// Each Intune record matches at most once. Identities nothing matches
    /// follow as their own rows: registered, not enrolled.
    /// </summary>
    public static List<DeviceListRow> Merge(IReadOnlyList<IntuneDevice> intune, IReadOnlyList<AutopilotDevice> autopilot)
    {
        if (autopilot.Count == 0)
            return intune.Select(d => new DeviceListRow(d, null)).ToList();

        var byId = new Dictionary<string, IntuneDevice>();
        var byEntra = new Dictionary<string, IntuneDevice>();
        var bySerial = new Dictionary<string, IntuneDevice>();
        foreach (var record in intune)
        {
            byId[record.Id.ToLowerInvariant()] = record;
            if (AutopilotLabels.Linked(record.AzureAdDeviceId) is { } entra) byEntra[entra] = record;
            if (!string.IsNullOrWhiteSpace(record.SerialNumber))
            {
                var key = Normalize(record.SerialNumber!);
                if (!bySerial.TryGetValue(key, out var existing)
                    || (existing.LastSyncDateTime ?? DateTime.MinValue) < (record.LastSyncDateTime ?? DateTime.MinValue))
                    bySerial[key] = record;
            }
        }

        var matched = new Dictionary<string, AutopilotDevice>();
        var unenrolled = new List<AutopilotDevice>();
        foreach (var identity in autopilot)
        {
            IntuneDevice? candidate = null;
            if (AutopilotLabels.Linked(identity.ManagedDeviceId) is { } managed) byId.TryGetValue(managed, out candidate);
            if (candidate == null && AutopilotLabels.Linked(identity.AzureActiveDirectoryDeviceId) is { } entra)
                byEntra.TryGetValue(entra, out candidate);
            if (candidate == null && !string.IsNullOrWhiteSpace(identity.SerialNumber))
                bySerial.TryGetValue(Normalize(identity.SerialNumber!), out candidate);

            if (candidate != null && matched.TryAdd(candidate.Id.ToLowerInvariant(), identity)) continue;
            unenrolled.Add(identity);
        }

        var rows = intune.Select(record =>
        {
            var identity = matched.GetValueOrDefault(record.Id.ToLowerInvariant());
            var isWindows = record.OperatingSystem?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true;
            AutopilotRegistration? registration = identity != null ? AutopilotRegistration.RegisteredAndEnrolled
                : isWindows ? AutopilotRegistration.EnrolledNotRegistered : null;
            return new DeviceListRow(record, identity, registration);
        }).ToList();
        rows.AddRange(unenrolled.Select(a => new DeviceListRow(null, a, AutopilotRegistration.RegisteredNotEnrolled)));
        return rows;
    }
}

public static class AppleOrgJoin
{
    /// <summary>
    /// Serial → service ID from each service's device listing: the
    /// organization's device list does not carry the relationship.
    /// </summary>
    public static Dictionary<string, string> Assignments(IReadOnlyDictionary<string, List<string>> listings)
    {
        var map = new Dictionary<string, string>();
        foreach (var (serverId, serials) in listings)
            foreach (var serial in serials) map[DeviceListJoin.Normalize(serial)] = serverId;
        return map;
    }

    /// <summary>
    /// Layer the Apple organizations onto the Devices list: every row whose
    /// serial an organization holds carries its record (two Intune records of
    /// one serial both carry it), and devices no row matches follow as their
    /// own "Not Enrolled" rows, so they stay visible and assignable. A device
    /// released from one organization and re-added to another is reported by
    /// both; the one still holding it wins.
    /// </summary>
    public static List<DeviceListRow> Enrich(IReadOnlyList<DeviceListRow> rows, IReadOnlyList<AppleOrgSnapshot> orgs)
    {
        if (orgs.Count == 0 || orgs.All(o => o.Devices.Count == 0)) return rows.ToList();

        var labels = AppleOrgProfile.Labels(orgs.Select(o => o.Profile).ToList());
        var serverNames = new Dictionary<string, string>();
        foreach (var server in orgs.SelectMany(o => o.Servers)) serverNames.TryAdd(server.Id, server.Name);

        var bySerial = new Dictionary<string, AppleOrgDevice>();
        foreach (var device in orgs.SelectMany(o => o.Devices))
        {
            var key = DeviceListJoin.Normalize(device.SerialNumber);
            if (!bySerial.TryGetValue(key, out var existing) || (existing.ReleasedFromOrg != null && device.ReleasedFromOrg == null))
                bySerial[key] = device;
        }

        DeviceListRow Attach(DeviceListRow row, AppleOrgDevice d) => row.WithApple(d,
            d.AssignedServerId != null ? serverNames.GetValueOrDefault(d.AssignedServerId) : null,
            labels.GetValueOrDefault(d.OrgId));

        var matched = new HashSet<string>();
        var output = rows.Select(row =>
        {
            if (row.SerialNumber is not { } serial || !bySerial.TryGetValue(DeviceListJoin.Normalize(serial), out var d)) return row;
            matched.Add(DeviceListJoin.Normalize(serial));
            return Attach(row, d);
        }).ToList();
        output.AddRange(bySerial.Where(kv => !matched.Contains(kv.Key)).Select(kv => Attach(new DeviceListRow(null, null), kv.Value)));
        return output;
    }

    /// <summary>The single organization holding every selected device, or null when they span several or none.</summary>
    public static string? SingleOrg(IReadOnlyCollection<DeviceListRow> rows)
    {
        if (rows.Count == 0 || rows.Any(r => r.Apple == null)) return null;
        var orgs = rows.Select(r => r.Apple!.OrgId).Distinct().ToList();
        return orgs.Count == 1 ? orgs[0] : null;
    }

    /// <summary>Whether an action can reach every selected device in one organization.</summary>
    public static bool IsAvailable(AppleOrgActionKind kind, IReadOnlyCollection<DeviceListRow> rows, AppleOrgProfile? profile)
    {
        if (profile == null || SingleOrg(rows) != profile.Name) return false;
        var devices = rows.Select(r => r.Apple!).ToList();
        return kind switch
        {
            AppleOrgActionKind.Assign => true,
            // Unassign names the service the devices leave: they must share one.
            AppleOrgActionKind.Unassign => devices.All(d => d.AssignedServerId != null)
                                           && devices.Select(d => d.AssignedServerId).Distinct().Count() == 1,
            AppleOrgActionKind.ScheduleMigration => devices.All(d => d.IsMigrationCapable != false && !d.HasActiveMigration),
            AppleOrgActionKind.UpdateMigrationDeadline or AppleOrgActionKind.CancelMigration => devices.All(d => d.HasActiveMigration),
            AppleOrgActionKind.Release => !profile.IsSchool && devices.All(d => d.ReleasedFromOrg == null),
            _ => false,
        };
    }
}
