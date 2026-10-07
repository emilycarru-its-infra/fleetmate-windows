using FleetMate.Core.Models.Devices;
using Xunit;

namespace FleetMate.Tests;

public class DeviceListLookupTests
{
    private static IntuneDevice Intune(string id, string? serial, string os = "Windows") =>
        new() { Id = id, DeviceName = $"D-{id}", SerialNumber = serial, OperatingSystem = os };

    private static AppleOrgDevice Apple(string serial, string? server = "srv-1") =>
        new() { SerialNumber = serial, OrgId = "school", ProductFamily = "Mac", Status = "ASSIGNED", AssignedServerId = server };

    private static AppleOrgSnapshot Org(params AppleOrgDevice[] devices) =>
        new(new AppleOrgProfile("school", "SCHOOLAPI.x"), devices, new[]
        {
            new AppleOrgServer("srv-1", "school", "Main Service", "MDM"),
            new AppleOrgServer("srv-2", "school", "Old Service", "MDM"),
        });

    // ── Serial list parsing ──────────────────────────────────────────────

    [Fact]
    public void Parse_SplitsOnAnySeparator_NormalizesAndDedupes()
    {
        var serials = SerialListParser.Parse(" c02abc, C02ABC\nPF1XYZ;\tMXL123\r\n\n\"Q9-77\"");
        Assert.Equal(new[] { "C02ABC", "PF1XYZ", "MXL123", "Q9-77" }, serials);
    }

    [Fact]
    public void Parse_CsvWithSerialHeader_ReadsOnlyThatColumn()
    {
        const string csv = "Asset Tag,Serial Number,Name\n1001,C02AAA,\"Lab, Room 2\"\n1002,PF2BBB,Desk\n";
        Assert.Equal(new[] { "C02AAA", "PF2BBB" }, SerialListParser.Parse(csv));
    }

    [Fact]
    public void Parse_PlainColumnWithHeaderWord_DropsTheHeader()
    {
        Assert.Equal(new[] { "C02AAA", "PF2BBB" }, SerialListParser.Parse("Serial\nC02AAA\nPF2BBB"));
        Assert.Empty(SerialListParser.Parse("  \n "));
        Assert.Empty(SerialListParser.Parse("a, !!, x"));
    }

    // ── Lookup ───────────────────────────────────────────────────────────

    [Fact]
    public void Lookup_KeepsListedDevices_AndAddsNotFoundRows()
    {
        var rows = DeviceListJoin.Merge(new[] { Intune("a", "SER1"), Intune("b", "SER2") }, Array.Empty<AutopilotDevice>());
        var result = SerialLookup.Apply(rows, new[] { "ser1", "NOPE9" });

        Assert.Equal(1, result.Matched);
        Assert.Equal(new[] { "NOPE9" }, result.Unknown);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("a", result.Rows[0].Id);

        var unknown = result.Rows[1];
        Assert.True(unknown.IsUnknown);
        Assert.False(unknown.IsEnrolled);
        Assert.Equal(DeviceListRow.UnknownPrefix + "NOPE9", unknown.Id);
        Assert.Equal("NOPE9", unknown.SerialText);
        Assert.Equal(DeviceListRow.NotFound, unknown.NameText);
        Assert.Equal(DeviceListRow.Missing, unknown.ComplianceText);
        Assert.Equal(new[] { DeviceDiscrepancies.Unknown }, unknown.Discrepancies);
    }

    // ── Discrepancies ────────────────────────────────────────────────────

    [Fact]
    public void Discrepancies_FindEachMismatch_OnceItsSourcesAreRead()
    {
        var intune = new[]
        {
            Intune("m1", "MAC1", "macOS"),   // in org, home service
            Intune("m2", "MAC2", "macOS"),   // in org, home service
            Intune("m3", "MAC3", "macOS"),   // in org, other service
            Intune("m4", "MAC4", "macOS"),   // not in org
            Intune("w1", "WIN1"),            // not in Autopilot
            Intune("w2", "WIN2"),            // in Autopilot
        };
        var autopilot = new[]
        {
            new AutopilotDevice { Id = "ap2", SerialNumber = "WIN2" },
            new AutopilotDevice { Id = "ap9", SerialNumber = "WIN9" },
        };
        var rows = AppleOrgJoin.Enrich(DeviceListJoin.Merge(intune, autopilot),
            new[] { Org(Apple("MAC1"), Apple("MAC2"), Apple("MAC3", "srv-2"), Apple("MAC8")) });

        DeviceDiscrepancies.Apply(rows, new DeviceDiscrepancies.Sources(true, true,
            new HashSet<string> { "MAC1", "MAC2", "MAC3", "MAC4", "WIN1", "WIN2", "WIN9" }));

        IReadOnlyList<string> Of(string serial) => rows.Single(r => r.SerialNumber == serial).Discrepancies;

        Assert.Empty(Of("MAC1"));
        Assert.Equal(new[] { DeviceDiscrepancies.OtherService }, Of("MAC3"));
        Assert.Equal(new[] { DeviceDiscrepancies.EnrolledUnregistered }, Of("MAC4"));
        Assert.Equal(new[] { DeviceDiscrepancies.EnrolledUnregistered }, Of("WIN1"));
        Assert.Empty(Of("WIN2"));
        Assert.Equal(new[] { DeviceDiscrepancies.AutopilotNotEnrolled }, Of("WIN9"));
        Assert.Equal(new[] { DeviceDiscrepancies.OrgNotEnrolled, DeviceDiscrepancies.NotInInventory }, Of("MAC8"));

        var counts = DeviceFacets.Counts(rows, DeviceFacet.Discrepancy);
        Assert.Contains((DeviceDiscrepancies.None, 3), counts);
        Assert.Contains((DeviceDiscrepancies.EnrolledUnregistered, 2), counts);
        Assert.DoesNotContain(counts, c => c.Value == DeviceDiscrepancies.NoService);

        var selection = Enum.GetValues<DeviceFacet>().ToDictionary(f => f, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        selection[DeviceFacet.Discrepancy].Add(DeviceDiscrepancies.NotInInventory);
        Assert.Equal("MAC8", Assert.Single(DeviceFacets.Apply(rows, selection)).SerialNumber);
    }

    [Fact]
    public void Discrepancies_StaySilent_ForSourcesNotYetRead()
    {
        var rows = DeviceListJoin.Merge(new[] { Intune("w1", "WIN1"), Intune("m1", "MAC1", "macOS") }, Array.Empty<AutopilotDevice>());
        DeviceDiscrepancies.Apply(rows, new DeviceDiscrepancies.Sources(false, false, null));
        Assert.All(rows, r => Assert.Empty(r.Discrepancies));
        Assert.All(rows, r => Assert.Equal(DeviceDiscrepancies.None, r.Value(DeviceFacet.Discrepancy)));
    }

    // ── Column layout ────────────────────────────────────────────────────

    [Fact]
    public void ColumnLayout_ReadsTheEarlierFormat_AndPlacesNewColumns()
    {
        var old = DeviceColumnLayout.Parse("[\"Model\",\"Added\"]");
        Assert.Equal(new[] { "Model", "Added" }, old.Shown);
        Assert.Empty(old.Order);

        var defaults = new[] { "Name", "Serial", "Platform", "Model" };
        Assert.Equal(defaults, old.Arrange(defaults));

        var saved = DeviceColumnLayout.Parse(new DeviceColumnLayout
        {
            Shown = new() { "Model" },
            Order = new() { "Serial", "Name", "Gone" },
        }.Serialize());
        Assert.Equal(new[] { "Serial", "Platform", "Model", "Name" }, saved.Arrange(defaults));
    }
}
