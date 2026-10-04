using FleetMate.Core.Models.Devices;
using Xunit;

namespace FleetMate.Tests;

public class DeviceListRowTests
{
    private static IntuneDevice Intune(string id, string? serial, string os = "Windows", string? compliance = "compliant") =>
        new() { Id = id, DeviceName = $"PC-{id}", SerialNumber = serial, OperatingSystem = os, ComplianceState = compliance };

    [Fact]
    public void Merge_JoinsByManagedDeviceIdThenSerial_AndKeepsUnenrolledIdentities()
    {
        var rows = DeviceListJoin.Merge(
            new[] { Intune("a", "SER-1"), Intune("b", " ser-2 "), Intune("c", "SER-3", "macOS") },
            new[]
            {
                new AutopilotDevice { Id = "ap1", SerialNumber = "OTHER", ManagedDeviceId = "a", GroupTag = "Lab" },
                new AutopilotDevice { Id = "ap2", SerialNumber = "SER-2", GroupTag = "Staff" },
                new AutopilotDevice { Id = "ap3", SerialNumber = "SER-9", GroupTag = "Spare" },
            });

        Assert.Equal(4, rows.Count);
        Assert.Equal("Lab", rows[0].GroupOrOrderText);
        Assert.Equal("Staff", rows[1].GroupOrOrderText);
        Assert.Equal(DeviceListRow.Missing, rows[2].GroupOrOrderText);

        var unenrolled = rows[3];
        Assert.False(unenrolled.IsEnrolled);
        Assert.StartsWith(DeviceListRow.AutopilotOnlyPrefix, unenrolled.Id);
        Assert.Equal("Not Enrolled", unenrolled.ComplianceText);
        Assert.Equal("Registered, Not Enrolled", unenrolled.OrgStatusText);
        Assert.Equal("Windows", unenrolled.PlatformText);
        Assert.Equal(DeviceListRow.Missing, unenrolled.OsText);
    }

    [Fact]
    public void EveryRowAnswersEveryColumn_WithDashForMissing()
    {
        var row = new DeviceListRow(new IntuneDevice { Id = "x", DeviceName = "" }, null);
        Assert.Equal(DeviceListRow.Missing, row.NameText);
        Assert.Equal(DeviceListRow.Missing, row.SerialText);
        Assert.Equal(DeviceListRow.Missing, row.ServiceText); // not Windows: no OS set
        Assert.Equal(DeviceListRow.Missing, row.OrgStatusText);
        Assert.Equal(DeviceListRow.Missing, row.LastSyncText);
        Assert.Equal("Unknown", row.ComplianceText);
    }

    [Fact]
    public void Facets_CountEveryValue_AndAppleFacetsReadNotInOrganization()
    {
        var rows = DeviceListJoin.Merge(new[] { Intune("a", "1"), Intune("b", "2", compliance: "noncompliant"), Intune("c", "3", "macOS") },
            Array.Empty<AutopilotDevice>());

        Assert.Equal(new[] { (DeviceListRow.NotInOrganization, 3) }, DeviceFacets.Counts(rows, DeviceFacet.AppleOrganization));
        Assert.Equal(new[] { ("Windows", 2), ("macOS", 1) }, DeviceFacets.Counts(rows, DeviceFacet.Platform));
        Assert.Equal(DeviceFacet.ManagementService, Enum.GetValues<DeviceFacet>()[0]);
        Assert.Equal("Device Management Service", DeviceFacet.ManagementService.Title());
    }

    [Fact]
    public void Apply_OrsWithinAFacetAndAndsAcrossFacets()
    {
        var rows = DeviceListJoin.Merge(new[] { Intune("a", "1"), Intune("b", "2", compliance: "noncompliant"), Intune("c", "3", "macOS") },
            Array.Empty<AutopilotDevice>());
        var selection = Enum.GetValues<DeviceFacet>().ToDictionary(f => f, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        selection[DeviceFacet.Platform].UnionWith(new[] { "Windows", "macOS" });
        Assert.Equal(3, DeviceFacets.Apply(rows, selection).Count());

        selection[DeviceFacet.Compliance].Add("Noncompliant");
        Assert.Equal("b", Assert.Single(DeviceFacets.Apply(rows, selection)).Id);
    }

    [Fact]
    public void ComplianceTone_NeverRed_NoncompliantIsAttention()
    {
        Assert.Equal("attention", new DeviceListRow(Intune("a", "1", compliance: "noncompliant"), null).ComplianceTone);
        Assert.Equal("good", new DeviceListRow(Intune("a", "1"), null).ComplianceTone);
    }
}
