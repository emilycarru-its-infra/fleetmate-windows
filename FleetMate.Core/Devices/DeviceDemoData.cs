using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Identity;
using FleetMate.Core.Models.Reporting;

namespace FleetMate.Core.Devices;

/// <summary>
/// Fictional devices for demo mode and the UI QA capture: made-up names,
/// people, serials and services, with addresses on example.* domains. Nothing
/// here describes a real fleet; every name and serial starts with DEMO so it
/// cannot be mistaken for one. Enough variety to show every column, filter,
/// action card and detail section: Windows with and without Autopilot, Macs
/// and an iPad in a fictional Apple organization, a registered-only identity,
/// an organization-only device, and compliant, non-compliant and stale rows.
/// </summary>
public static class DeviceDemoData
{
    private const string Org = "demo-org";

    private static readonly (string Name, string Serial, string Os, string Version, string Model, string Maker, string User, string Compliance, int SyncDaysAgo)[] Fleet =
    {
        ("DEMO-LAPTOP-01", "DEMO-SN-0001", "Windows", "10.0.26100.2314", "Surface Laptop 7", "Microsoft", "Alex Rivera", "compliant", 0),
        ("DEMO-LAPTOP-02", "DEMO-SN-0002", "Windows", "10.0.26100.2314", "Surface Laptop 7", "Microsoft", "Sam Okafor", "noncompliant", 2),
        ("DEMO-LAB-14", "DEMO-SN-0014", "Windows", "10.0.22631.4460", "OptiPlex 7020", "Dell Inc.", "Jordan Lee", "compliant", 1),
        ("DEMO-LAB-15", "DEMO-SN-0015", "Windows", "10.0.22631.4460", "OptiPlex 7020", "Dell Inc.", "", "compliant", 45),
        ("DEMO-DESK-03", "DEMO-SN-0103", "Windows", "10.0.26100.2314", "ThinkCentre M90q", "Lenovo", "Casey Morgan", "inGracePeriod", 3),
        ("DEMO-MAC-21", "DEMO-SN-M021", "macOS", "15.1.1", "MacBook Pro (14-inch, 2024)", "Apple", "Riley Chen", "compliant", 0),
        ("DEMO-MAC-22", "DEMO-SN-M022", "macOS", "15.1.1", "MacBook Pro (14-inch, 2024)", "Apple", "Taylor Brooks", "noncompliant", 6),
        ("DEMO-MINI-07", "DEMO-SN-M007", "macOS", "14.7.1", "Mac mini (2023)", "Apple", "Morgan Patel", "compliant", 38),
        ("DEMO-IPAD-04", "DEMO-SN-P004", "iPadOS", "18.1", "iPad Air (M2)", "Apple", "Avery Kim", "compliant", 4),
        ("DEMO-PHONE-02", "DEMO-SN-A002", "Android", "15", "Pixel 9", "Google", "Quinn Diaz", "compliant", 9),
    };

    private static string Upn(string user) => $"{user.ToLowerInvariant().Replace(' ', '.')}@example.edu";

    /// <summary>Intune devices, their check-ins measured back from <paramref name="nowUtc"/>.</summary>
    public static List<IntuneDevice> Devices(DateTime nowUtc) => Fleet.Select((f, i) => new IntuneDevice
    {
        Id = $"00000000-0000-4000-8000-{i + 1:D12}",
        DeviceName = f.Name,
        ManagedDeviceName = f.User.Length == 0 ? f.Name : $"{f.User.Split(' ')[0]}_{f.Os}",
        SerialNumber = f.Serial,
        OperatingSystem = f.Os,
        OsVersion = f.Version,
        Model = f.Model,
        Manufacturer = f.Maker,
        UserDisplayName = f.User.Length == 0 ? null : f.User,
        UserPrincipalName = f.User.Length == 0 ? null : Upn(f.User),
        ComplianceState = f.Compliance,
        ManagementState = "managed",
        LastSyncDateTime = nowUtc.AddDays(-f.SyncDaysAgo).AddHours(-i),
        EnrolledDateTime = nowUtc.AddDays(-200 - i * 11),
        AzureAdDeviceId = $"10000000-0000-4000-8000-{i + 1:D12}",
        DeviceEnrollmentType = f.Os == "Windows" ? "windowsAzureADJoin" : f.Maker == "Apple" ? "appleBulkWithUser" : "androidEnterpriseFullyManaged",
        EnrollmentProfileName = f.Os == "Windows" ? "Demo Autopilot Profile" : f.Maker == "Apple" ? "Demo Automated Enrollment" : null,
        ManagedDeviceOwnerType = "company",
        IsEncrypted = f.Compliance != "noncompliant",
        IsSupervised = f.Maker == "Apple" ? true : null,
        JailBroken = f.Maker == "Apple" || f.Os == "Android" ? "False" : null,
        ManagementAgent = "mdm",
        JoinType = f.Os == "Windows" ? "azureADJoined" : null,
        DeviceRegistrationState = "registered",
        DeviceCategoryDisplayName = f.Os == "Windows" ? "Teaching Lab" : "General",
        TotalStorageSpaceInBytes = 512L * 1024 * 1024 * 1024,
        FreeStorageSpaceInBytes = (512L - 60 - i * 41) * 1024 * 1024 * 1024,
        PhysicalMemoryInBytes = (f.Os == "iPadOS" || f.Os == "Android" ? 8L : 16L) * 1024 * 1024 * 1024,
        WiFiMacAddress = $"02:00:5E:10:00:{i + 1:X2}",
        EthernetMacAddress = f.Os is "Windows" or "macOS" ? $"02:00:5E:20:00:{i + 1:X2}" : null,
        SkuFamily = f.Os == "Windows" ? "Enterprise" : null,
        AzureAdRegistered = true,
        SubscriberCarrier = f.Os == "Android" ? "Example Mobile" : null,
    }).ToList();

    /// <summary>
    /// Autopilot identities for the Windows devices, plus one registered and
    /// never enrolled, so the list shows a registered-only row.
    /// </summary>
    public static List<AutopilotDevice> Autopilot(DateTime nowUtc)
    {
        var identities = Fleet.Select((f, i) => (f, i)).Where(x => x.f.Os == "Windows").Select(x => new AutopilotDevice
        {
            Id = $"20000000-0000-4000-8000-{x.i + 1:D12}",
            SerialNumber = x.f.Serial,
            Manufacturer = x.f.Maker,
            Model = x.f.Model,
            GroupTag = x.f.Name.StartsWith("DEMO-LAB", StringComparison.Ordinal) ? "Lab" : "Staff",
            SystemFamily = x.f.Model,
            EnrollmentState = "enrolled",
            LastContactedDateTime = nowUtc.AddDays(-x.f.SyncDaysAgo),
            AzureActiveDirectoryDeviceId = $"10000000-0000-4000-8000-{x.i + 1:D12}",
            ManagedDeviceId = $"00000000-0000-4000-8000-{x.i + 1:D12}",
            UserPrincipalName = x.f.User.Length == 0 ? null : Upn(x.f.User),
            DisplayName = x.f.Name,
            PurchaseOrderIdentifier = "PO-DEMO-1042",
            DeploymentProfileAssignmentStatus = "assignedInSync",
        }).ToList();
        identities.Add(new AutopilotDevice
        {
            Id = "20000000-0000-4000-8000-000000000099",
            SerialNumber = "DEMO-SN-0099",
            Manufacturer = "Dell Inc.",
            Model = "Latitude 7450",
            GroupTag = "Staff",
            EnrollmentState = "notContacted",
            LastContactedDateTime = DateTime.MinValue,
            PurchaseOrderIdentifier = "PO-DEMO-1107",
            DeploymentProfileAssignmentStatus = "assignedInSync",
        });
        return identities;
    }

    /// <summary>A fictional Apple organization holding the Macs and the iPad, and one Mac nobody has enrolled yet.</summary>
    public static List<AppleOrgSnapshot> AppleOrgs(DateTime nowUtc)
    {
        var profile = new AppleOrgProfile(Org, "BUSINESSAPI.demo");
        var servers = new List<AppleOrgServer>
        {
            new("srv-demo-1", Org, "Example MDM", "MDM", 4),
            new("srv-demo-2", Org, "Example Staging MDM", "MDM", 1),
        };
        var devices = Fleet.Where(f => f.Maker == "Apple").Select((f, i) => new AppleOrgDevice
        {
            SerialNumber = f.Serial,
            OrgId = Org,
            Model = f.Model,
            ProductFamily = f.Os == "iPadOS" ? "iPad" : "Mac",
            Status = "ASSIGNED",
            AssignedServerId = i == 2 ? "srv-demo-2" : "srv-demo-1",
            OrderNumber = $"DEMO-ORDER-{310 + i}",
            PurchaseSource = "APPLE",
            AddedToOrg = new DateTimeOffset(nowUtc.AddDays(-300 + i * 20)),
            OrderDate = new DateTimeOffset(nowUtc.AddDays(-310 + i * 20)),
            IsMigrationCapable = f.Os == "macOS",
            WifiMacAddresses = new[] { $"02:00:5E:30:00:{i + 1:X2}" },
        }).ToList();
        devices.Add(new AppleOrgDevice
        {
            SerialNumber = "DEMO-SN-M009",
            OrgId = Org,
            Model = "MacBook Air (13-inch, M3)",
            ProductFamily = "Mac",
            Status = "UNASSIGNED",
            OrderNumber = "DEMO-ORDER-355",
            PurchaseSource = "RESELLER",
            AddedToOrg = new DateTimeOffset(nowUtc.AddDays(-12)),
            OrderDate = new DateTimeOffset(nowUtc.AddDays(-20)),
            IsMigrationCapable = true,
        });
        return new List<AppleOrgSnapshot> { new(profile, devices, servers) };
    }

    /// <summary>Install errors for the Errors by Category card.</summary>
    public static DeviceErrorFigures Errors() => new(42, new List<ErrorSummary>
    {
        new() { ItemName = "Demo Editor", DeviceCount = 6, Category = ErrorCategory.HashMismatch },
        new() { ItemName = "Demo Viewer", DeviceCount = 4, Category = ErrorCategory.DownloadFailed },
        new() { ItemName = "Demo Suite", DeviceCount = 3, Category = ErrorCategory.MsiFailure },
        new() { ItemName = "Demo Fonts", DeviceCount = 2, Category = ErrorCategory.NotFound },
        new() { ItemName = "Demo Plugin", DeviceCount = 1, Category = ErrorCategory.InstallVerificationFailed },
    }, Loading: false);

    /// <summary>The detail panel's and lightbox's reads, from fixtures.</summary>
    public static IDeviceDetailSource DetailSource { get; } = new DemoDetailSource();

    private sealed class DemoDetailSource : IDeviceDetailSource
    {
        public Task<List<EntraGroup>> GroupsAsync(string azureAdDeviceId) => Task.FromResult(new List<EntraGroup>
        {
            new() { Id = "30000000-0000-4000-8000-000000000001", DisplayName = "Demo Devices - All Managed", Description = "Every managed device" },
            new() { Id = "30000000-0000-4000-8000-000000000002", DisplayName = "Demo Devices - Teaching Labs", Description = "Shared lab machines" },
            new() { Id = "30000000-0000-4000-8000-000000000003", DisplayName = "Demo Update Ring - Broad" },
        });

        public Task<List<DeviceCompliancePolicyState>> CompliancePoliciesAsync(string intuneDeviceId) => Task.FromResult(new List<DeviceCompliancePolicyState>
        {
            new() { Id = "policy-demo-baseline", DisplayName = "Demo Baseline Compliance", State = "nonCompliant", SettingCount = 4, Version = 3,
                PlatformType = "windows10AndLater", LastReportedDateTime = DateTime.UtcNow.AddHours(-3) },
            new() { Id = "policy-demo-encryption", DisplayName = "Demo Disk Encryption", State = "compliant", SettingCount = 1, Version = 1,
                PlatformType = "windows10AndLater", LastReportedDateTime = DateTime.UtcNow.AddHours(-3) },
            new() { Id = "policy-demo-default", DisplayName = "Default Device Compliance Policy", State = "compliant", Version = 1 },
        });

        public Task<List<DetectedApp>> DetectedAppsAsync(string intuneDeviceId) => Task.FromResult(new List<DetectedApp>
        {
            new() { Id = "app-1", DisplayName = "Demo Browser", Version = "131.0.2903.51" },
            new() { Id = "app-2", DisplayName = "Demo Editor", Version = "2026.11.02.0915" },
            new() { Id = "app-3", DisplayName = "Demo Notes", Version = "8.4.1" },
            new() { Id = "app-4", DisplayName = "Demo Office Suite", Version = "16.0.18129" },
            new() { Id = "app-5", DisplayName = "Demo Runtime", Version = "10.0.0" },
        });

        public Task<List<ComplianceSettingState>> SettingStatesAsync(string intuneDeviceId, string policyId) => Task.FromResult(new List<ComplianceSettingState>
        {
            new() { Id = "s1", SettingName = "Minimum OS version", State = "nonCompliant", CurrentValue = "10.0.22631",
                ErrorCode = 0x87D1FDE8, ErrorDescription = "The device is below the required version",
                Sources = new() { new() { Id = "policy-demo-baseline", DisplayName = "Demo Baseline Compliance" } } },
            new() { Id = "s2", SettingName = "Firewall", State = "compliant", CurrentValue = "On" },
            new() { Id = "s3", SettingName = "Antivirus", State = "compliant", CurrentValue = "On" },
            new() { Id = "s4", SettingName = "Secure Boot", State = "compliant", CurrentValue = "Enabled", UserPrincipalName = "alex.rivera@example.edu" },
        });

        public Task<CompliancePolicyDefinition?> PolicyDefinitionAsync(string policyId) => Task.FromResult<CompliancePolicyDefinition?>(new()
        {
            DisplayName = "Demo Baseline Compliance",
            Description = "A fictional baseline: current Windows, firewall and antivirus on, Secure Boot enabled.",
            PlatformType = "windows10AndLater",
            Version = 3,
            Requirements = new()
            {
                new("Minimum OS version", "10.0.26100"),
                new("Firewall", "Required"),
                new("Antivirus", "Required"),
                new("Secure Boot", "Required"),
            },
            ScheduledActions = new() { "Mark device noncompliant: immediately", "Send email to end user: after 1 day" },
            AssignedGroupIds = new() { "30000000-0000-4000-8000-000000000001" },
        });

        public Task<string> GroupNameAsync(string groupId) => Task.FromResult("Demo Devices - All Managed");
    }
}
