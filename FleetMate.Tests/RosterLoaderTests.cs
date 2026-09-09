using FleetMate.Core.Models.Manage;
using FleetMate.Core.Services.Manage;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Roster parsing and sectioning against hand-authored rows in the shape of
/// the Windows enrollment CSV (lower-case headers, separate hostname column)
/// and the Mac one (capitalised headers, hostname in allocation).
/// </summary>
public class RosterLoaderTests
{
    private const string WindowsRoster =
        "serial,catalog,area,location,asset,usage,status,allocation,username,platform,fleet,hostname\n" +
        "S001,Curriculum,Studio,R101,A0001,Shared,Active,Studio A 01,,Windows,Studio Lab A,LAB-A-01\n" +
        "S002,Curriculum,Studio,R101,A0002,Shared,Active (Legacy),Studio A 02,,Windows,Studio Lab A,LAB-A-02\n" +
        "S003,Curriculum,Studio,R102,A0003,Shared,Active,Studio B 01,,Windows,Studio Lab B,LAB-B-01\n" +
        "S004,Curriculum,Photo,R201,A0004,Shared,Active,Photo 01,,Windows,,LAB-C-01\n" +
        "S005,Curriculum,Photo,R201,A0005,Shared,Active,Photo 02,,Windows,,\n" +
        "S006,Curriculum,Podium,R101,A0006,Shared,Active,Podium,,Windows,,PODIUM-R101\n" +
        "S007,Kiosk,Signage,R301,A0007,Shared,Active,Lobby sign,,Windows,Signage Displays,SIGN-01\n" +
        "S008,Staff,IT,R301,A0008,Assigned,Active,\"Doe, Jane\",jdoe,Windows,,JDOE\n" +
        "S009,Staff,Library,R302,A0009,Assigned,Active,Sam Roe,sroe,Windows,,SROE\n" +
        "S010,Faculty,Design,R401,A0010,Assigned,Active,Alex Kim,akim,Windows,,AKIM\n" +
        "S011,Provisioning,,,A0011,Shared,Active,,,Windows,,\n" +
        "S012,Curriculum,Studio,R101,A0012,Shared,Returned Lease End,Studio A 12,,Windows,Studio Lab A,LAB-A-12\n" +
        "S013,Staff,IT,R301,A0013,Assigned,Donated,Old Staff,ostaff,Windows,,OSTAFF\n";

    private const string MacRoster =
        "Serial,Catalog,Area,Location,Asset,Usage,Status,Allocation,Username,Platform,Fleet\n" +
        "M001,Curriculum,Studio,R101,A0100,Shared,Active,mac-anim-01,,macOS,\n" +
        "M002,Staff,IT,R301,A0101,Assigned,Active,mac-jdoe,jdoe,macOS,\n";

    [Fact]
    public void Parse_SplitsWindowsRosterIntoSections()
    {
        var roster = new RosterLoader().Parse(WindowsRoster);

        Assert.Equal(new[] { "R101 · Studio Lab A", "R201 · Photo", "R102 · Studio Lab B" },
            roster.Labs.Select(r => r.Name).ToArray());
        Assert.Single(roster.Kiosks);
        Assert.Equal("R301", roster.Kiosks[0].Number);
        Assert.Equal(new[] { "IT", "Library" }, roster.Staff.Select(r => r.Number).ToArray());
        Assert.Single(roster.Faculty);
        Assert.Equal("A", roster.Faculty[0].Number);
    }

    [Fact]
    public void Parse_GroupsLabsByFleetBeforeLocation()
    {
        var roster = new RosterLoader().Parse(WindowsRoster);

        var studio = roster.Labs.Single(r => r.DisplayName == "Studio Lab A");
        Assert.Equal("R101", studio.Number);
        Assert.Equal(new[] { "LAB-A-01", "LAB-A-02" }, studio.Computers.Select(c => c.Hostname).ToArray());

        var photo = roster.Labs.Single(r => r.Number == "R201");
        Assert.Equal("Photo", photo.DisplayName);
        Assert.Equal(2, photo.Count);
    }

    [Fact]
    public void Parse_ExcludesPodiumAndProvisioningFromLabs()
    {
        var roster = new RosterLoader().Parse(WindowsRoster);

        Assert.DoesNotContain(roster.AllSectioned, c => c.Serial == "S006");
        Assert.DoesNotContain(roster.AllSectioned, c => c.Serial == "S011");
        Assert.Contains(roster.Source, c => c.Serial == "S011");
    }

    [Fact]
    public void Parse_DropsRetiredRowsUnlessAsked()
    {
        var strict = new RosterLoader().Parse(WindowsRoster);
        Assert.Equal(2, strict.RetiredCount);
        Assert.DoesNotContain(strict.Source, c => c.Serial is "S012" or "S013");

        var lenient = new RosterLoader { IncludeRetired = true }.Parse(WindowsRoster);
        Assert.Equal(0, lenient.RetiredCount);
        Assert.Contains(lenient.Source, c => c.Serial == "S012");
    }

    [Fact]
    public void Parse_KeepsActiveVariantsInService()
    {
        var roster = new RosterLoader().Parse(WindowsRoster);
        Assert.Contains(roster.Source, c => c.Serial == "S002" && c.IsInService);
    }

    [Fact]
    public void Parse_KeepsMachinesWithoutHostname()
    {
        var roster = new RosterLoader().Parse(WindowsRoster);

        var photo02 = roster.Source.Single(c => c.Serial == "S005");
        Assert.False(photo02.HasHostname);
        Assert.Equal("Photo 02", photo02.DisplayName);
        Assert.Contains(roster.Labs.Single(r => r.Number == "R201").Computers, c => c.Serial == "S005");
    }

    [Fact]
    public void Parse_HandlesQuotedCommaInAllocation()
    {
        var roster = new RosterLoader().Parse(WindowsRoster);
        var jane = roster.Source.Single(c => c.Serial == "S008");
        Assert.Equal("Doe, Jane", jane.Allocation);
        Assert.Equal("JDOE", jane.Hostname);
    }

    [Fact]
    public void Parse_MacRosterUsesAllocationAsHostname()
    {
        var roster = new RosterLoader().Parse(MacRoster);
        Assert.Single(roster.Labs);
        Assert.Equal("mac-anim-01", roster.Labs[0].Computers[0].Hostname);
        Assert.Equal("mac-jdoe", roster.Staff[0].Computers[0].Hostname);
    }

    [Fact]
    public void Parse_EmptyOrHeaderlessInputIsEmpty()
    {
        Assert.Empty(new RosterLoader().Parse("").Source);
        Assert.Empty(new RosterLoader().Parse("\n\n").Source);
        Assert.Empty(new RosterLoader().Parse("a,b,c\n1,2,3\n").Source);
    }

    [Fact]
    public void ParseLine_UnescapesDoubledQuotes()
    {
        var fields = RosterLoader.ParseLine("a,\"b \"\"quoted\"\" c\",d");
        Assert.Equal(new[] { "a", "b \"quoted\" c", "d" }, fields.ToArray());
    }

    [Fact]
    public void Adhoc_ComputerIsAddressableByLabel()
    {
        var byIp = RosterComputer.Adhoc("", "10.0.0.5");
        Assert.True(byIp.IsAdhoc);
        Assert.Equal("10.0.0.5", byIp.DisplayName);

        var byName = RosterComputer.Adhoc("LAB-99", "10.0.0.6");
        Assert.Equal("LAB-99", byName.Hostname);
        Assert.Equal("adhoc-LAB-99", byName.Serial);
    }

    [Fact]
    public void InventoryLine_ContainsWhatATicketNeeds()
    {
        var c = new RosterComputer { Serial = "S1", Hostname = "HOST-1", Asset = "A1", Location = "R301", Status = "Active" };
        Assert.Equal("HOST-1  10.1.1.1  S1  A1  R301  Windows 11 23H2", c.InventoryLine("10.1.1.1", "Windows 11 23H2"));
    }
}
