using FleetMate.Core.Models.Devices;
using Xunit;

namespace FleetMate.Tests;

public class AutopilotJoinTests
{
    private static IntuneDevice Intune(string id, string? serial, string os = "Windows", string? entra = null, DateTime? sync = null) =>
        new() { Id = id, DeviceName = $"PC-{id}", SerialNumber = serial, OperatingSystem = os, AzureAdDeviceId = entra, LastSyncDateTime = sync };

    [Fact]
    public void Merge_MapsColumnsLikeTheMac()
    {
        var rows = DeviceListJoin.Merge(
            new[] { Intune("a", "S1"), Intune("b", "S2"), Intune("m", "S3", "macOS") },
            new[]
            {
                new AutopilotDevice { Id = "ap1", SerialNumber = "S1", GroupTag = "Lab", PurchaseOrderIdentifier = "PO-7" },
                new AutopilotDevice { Id = "ap9", SerialNumber = "S9", EnrollmentState = "notContacted" },
            });

        var registered = rows.Single(r => r.Id == "a");
        Assert.Equal("Intune", registered.ServiceText);
        Assert.Equal("Registered", registered.OrgStatusText);
        Assert.Equal("Lab", registered.GroupOrOrderText);
        Assert.Equal("PO-7", registered.PurchaseSourceText);

        var notRegistered = rows.Single(r => r.Id == "b");
        Assert.Equal("Not Registered", notRegistered.OrgStatusText);
        Assert.Equal("Not Registered", notRegistered.Value(DeviceFacet.GroupTag));

        var mac = rows.Single(r => r.Id == "m");
        Assert.Equal(DeviceListRow.Missing, mac.ServiceText);
        Assert.Equal(DeviceListRow.Missing, mac.OrgStatusText);
        Assert.Equal("Not in Autopilot", mac.Value(DeviceFacet.AutopilotEnrollment));

        var unenrolled = rows.Single(r => r.Id == DeviceListRow.AutopilotOnlyPrefix + "ap9");
        Assert.Equal("Registered, Not Enrolled", unenrolled.OrgStatusText);
        Assert.Equal(DeviceListRow.Missing, unenrolled.ServiceText);
        Assert.Equal("Not Contacted", unenrolled.Value(DeviceFacet.AutopilotEnrollment));
        Assert.Equal("No Group Tag", unenrolled.Value(DeviceFacet.GroupTag));
    }

    [Fact]
    public void Merge_MatchesByManagedIdThenEntraThenNewestSerial_EachRecordOnce()
    {
        var old = new DateTime(2026, 1, 1);
        var recent = new DateTime(2026, 9, 1);
        var rows = DeviceListJoin.Merge(
            new[]
            {
                Intune("x", "DUP", sync: old),
                Intune("y", "DUP", sync: recent),
                Intune("z", "Z1", entra: "E-1"),
            },
            new[]
            {
                new AutopilotDevice { Id = "byserial", SerialNumber = "dup " },
                new AutopilotDevice { Id = "byentra", SerialNumber = "nomatch", AzureActiveDirectoryDeviceId = "e-1" },
                new AutopilotDevice { Id = "second", SerialNumber = "DUP" },
            });

        Assert.Equal("byserial", rows.Single(r => r.Id == "y").Autopilot?.Id);
        Assert.Null(rows.Single(r => r.Id == "x").Autopilot);
        Assert.Equal("byentra", rows.Single(r => r.Id == "z").Autopilot?.Id);
        // "second" also matches serial DUP, but that record is taken: it stands alone.
        Assert.Contains(rows, r => r.Id == DeviceListRow.AutopilotOnlyPrefix + "second" && !r.IsEnrolled);
    }

    [Fact]
    public void Merge_WithoutAutopilot_LeavesRegistrationUnknown()
    {
        var row = Assert.Single(DeviceListJoin.Merge(new[] { Intune("a", "S1") }, Array.Empty<AutopilotDevice>()));
        Assert.Null(row.Registration);
        Assert.Equal(DeviceListRow.Missing, row.OrgStatusText);
    }

    [Fact]
    public void FacetOrder_AppleThenAutopilotThenIntune()
    {
        var order = Enum.GetValues<DeviceFacet>().Take(6).Select(f => f.Title());
        Assert.Equal(new[] { "Device Management Service", "Organization Status", "Apple Organization",
            "Group Tag", "Deployment Profile", "Autopilot Enrollment" }, order);
    }

    [Theory]
    [InlineData("assignedInSync", "Assigned")]
    [InlineData("assignedUnkownSyncState", "Assigned")]
    [InlineData("notAssigned", "Not Assigned")]
    [InlineData(null, "Unknown")]
    public void ProfileStatusLabel_ReadsLikeAPerson(string? status, string expected) =>
        Assert.Equal(expected, new AutopilotDevice { DeploymentProfileAssignmentStatus = status }.ProfileStatusLabel());

    [Fact]
    public void Actions_NeedEveryDeviceRegistered_UnassignNeedsAUser()
    {
        var withUser = new AutopilotDevice { Id = "1", UserPrincipalName = "a@example.edu" };
        var noUser = new AutopilotDevice { Id = "2" };

        Assert.True(AutopilotAction.SetGroupTag.IsAvailable(new[] { withUser, noUser }));
        Assert.False(AutopilotAction.Delete.IsAvailable(new AutopilotDevice?[] { withUser, null }));
        Assert.True(AutopilotAction.UnassignUser.IsAvailable(new[] { withUser }));
        Assert.False(AutopilotAction.UnassignUser.IsAvailable(new[] { withUser, noUser }));
        Assert.False(AutopilotAction.Sync.IsAvailable(Array.Empty<AutopilotDevice>()));
    }

    [Fact]
    public void HashCsv_ParsesGetWindowsAutopilotInfoOutput_AndReportsBadRows()
    {
        var csv = AutopilotHashCsv.Parse(
            "Device Serial Number,Windows Product ID,Hardware Hash,Group Tag\r\n" +
            "SER1,,QUJDRA==,Lab\r\n" +
            "\"SER2\",,\"QUJDRA==\",\r\n" +
            "SER3,,not base64!,\r\n" +
            "ser1,,QUJDRA==,\r\n" +
            ",,QUJDRA==,\r\n");

        Assert.Equal(new[] { "SER1", "SER2" }, csv.Entries.Select(e => e.SerialNumber));
        Assert.Equal("Lab", csv.Entries[0].GroupTag);
        Assert.Equal("Override", csv.Entries[0].WithGroupTag("Override").GroupTag);
        Assert.Equal(new[] { 4, 5, 6 }, csv.Issues.Select(i => i.Line));
    }

    [Fact]
    public void HashCsv_ReadsUtf16AndRejectsMissingColumns()
    {
        var utf16 = new byte[] { 0xFF, 0xFE }.Concat(System.Text.Encoding.Unicode.GetBytes("Device Serial Number,Hardware Hash\nS1,QUJDRA==\n")).ToArray();
        Assert.Equal("S1", Assert.Single(AutopilotHashCsv.Parse(utf16).Entries).SerialNumber);
        Assert.Throws<FormatException>(() => AutopilotHashCsv.Parse("Serial Number,Name\nS1,x\n"));
    }
}
