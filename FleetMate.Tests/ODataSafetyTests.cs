using FleetMate.Commands.Devices;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using Xunit;

namespace FleetMate.Tests;

public class ODataFilterTests
{
    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("O'Brien", "'O''Brien'")]
    [InlineData("x' or 1 eq 1 or serialNumber eq 'y", "'x'' or 1 eq 1 or serialNumber eq ''y'")]
    [InlineData("", "''")]
    public void Literal_DoublesQuotesSoTheValueCannotCloseTheString(string value, string expected) =>
        Assert.Equal(expected, ODataFilter.Literal(value));

    [Fact]
    public void Literal_InjectionStaysInsideOneLiteral()
    {
        var filter = $"displayName eq {ODataFilter.Literal("x' or 1 eq 1 or serialNumber eq 'y")}";
        // Strip the escaped quote pairs: what is left must be exactly the two delimiters.
        Assert.Equal(2, filter.Replace("''", "").Count(c => c == '\''));
    }

    [Theory]
    [InlineData("C02XYZ123", "'C02XYZ123'")]
    [InlineData(" PF-1YBB26 ", "'PF-1YBB26'")]
    public void Serial_AcceptsLettersDigitsAndHyphens(string serial, string expected) =>
        Assert.Equal(expected, ODataFilter.Serial(serial));

    [Theory]
    [InlineData("x' or 1 eq 1")]
    [InlineData("ABC 123")]
    [InlineData("ABC/123")]
    [InlineData("")]
    [InlineData("LAB-PC-01'")]
    public void Serial_RejectsAnythingElse(string serial) =>
        Assert.Throws<ODataValidationException>(() => ODataFilter.Serial(serial));

    [Fact]
    public void Guid_NormalisesAndRejectsNonGuids()
    {
        Assert.Equal("'0f1e2d3c-0000-4000-8000-000000000001'", ODataFilter.Guid("{0F1E2D3C-0000-4000-8000-000000000001}"));
        Assert.Throws<ODataValidationException>(() => ODataFilter.Guid("0f1e2d3c' or 1 eq 1"));
    }

    [Theory]
    [InlineData("pat.doe@example.edu", true)]
    [InlineData("o'neil@example.edu", true)]
    [InlineData("no-at-sign", false)]
    [InlineData("two@@example.edu", false)]
    [InlineData("has space@example.edu", false)]
    public void Upn_ChecksShapeAndEscapes(string upn, bool valid)
    {
        if (valid) Assert.Equal(ODataFilter.Literal(upn), ODataFilter.Upn(upn));
        else Assert.Throws<ODataValidationException>(() => ODataFilter.Upn(upn));
    }

    [Fact]
    public void EncodedFilterKeepsReservedCharactersEscapedOnTheElevationPath()
    {
        // The elevation transport hands Uri.ToString() to az rest. The filter is
        // encoded once by the caller; ToString must not decode & # or ' back
        // into query structure.
        var filter = $"displayName eq {ODataFilter.Literal("a & $top=999 #x")}";
        var uri = new Uri($"https://graph.microsoft.com/v1.0/devices?$filter={Uri.EscapeDataString(filter)}");
        var text = uri.ToString();
        Assert.Contains("%26", text);
        Assert.Contains("%23", text);
        Assert.Contains("%27", text);
        Assert.DoesNotContain("&$top", text);
    }
}

public class DestructiveTargetResolverTests
{
    private static IntuneDevice Device(string id, string serial, int daysAgo = 0) =>
        new() { Id = id, DeviceName = $"PC-{id[..4]}", SerialNumber = serial, LastSyncDateTime = DateTime.UtcNow.AddDays(-daysAgo) };

    private const string IdA = "0f1e2d3c-0000-4000-8000-00000000000a";
    private const string IdB = "0f1e2d3c-0000-4000-8000-00000000000b";

    private static Task<TargetResolution> Resolve(string identifier, params IntuneDevice[] fleet) =>
        DestructiveTargetResolver.ResolveSingleAsync(identifier,
            id => Task.FromResult(fleet.FirstOrDefault(d => d.Id == id)),
            serial => Task.FromResult(fleet.Where(d => string.Equals(d.SerialNumber, serial, StringComparison.OrdinalIgnoreCase)).ToList()));

    [Fact]
    public async Task OneMatch_Resolves()
    {
        var r = Assert.IsType<TargetResolution.Resolved>(await Resolve("SER1", Device(IdA, "SER1")));
        Assert.Equal(IdA, r.Device.Id);
    }

    [Fact]
    public async Task ZeroMatches_IsNotFound() =>
        Assert.IsType<TargetResolution.NotFound>(await Resolve("SER9", Device(IdA, "SER1")));

    [Fact]
    public async Task ManyMatches_IsAmbiguousAndListsEveryCandidateNewestFirst()
    {
        var a = Assert.IsType<TargetResolution.Ambiguous>(await Resolve("SER1", Device(IdA, "SER1", daysAgo: 30), Device(IdB, "SER1")));
        Assert.Equal(new[] { IdB, IdA }, a.Candidates.Select(c => c.Id));
    }

    [Fact]
    public async Task UnknownId_IsNotFound_AndNeverFallsBackToSerial() =>
        Assert.IsType<TargetResolution.NotFound>(await Resolve(IdB, Device(IdA, "SER1")));

    [Theory]
    [InlineData("PC-0f1e")]                 // a device name: never resolved by name
    [InlineData("x' or 1 eq 1")]
    [InlineData("LAB PC 01")]
    public async Task NamesAndInjectionAttempts_AreInvalid(string identifier)
    {
        var lookedUp = false;
        var result = await DestructiveTargetResolver.ResolveSingleAsync(identifier,
            _ => { lookedUp = true; return Task.FromResult<IntuneDevice?>(null); },
            _ => { lookedUp = true; return Task.FromResult(new List<IntuneDevice>()); });
        if (identifier == "PC-0f1e")
        {
            // Serial-shaped, so it is looked up as a serial and must match exactly.
            Assert.IsType<TargetResolution.NotFound>(result);
        }
        else
        {
            Assert.IsType<TargetResolution.Invalid>(result);
            Assert.False(lookedUp);
        }
    }

    [Fact]
    public async Task PartialSerialMatchesAreIgnored()
    {
        // A lookup that over-returns (as contains() would) still resolves only exact serials.
        var result = await DestructiveTargetResolver.ResolveSingleAsync("SER1",
            _ => Task.FromResult<IntuneDevice?>(null),
            _ => Task.FromResult(new List<IntuneDevice> { Device(IdA, "SER10"), Device(IdB, "XSER1") }));
        Assert.IsType<TargetResolution.NotFound>(result);
    }

    [Fact]
    public void CliStopsOnEverythingButASingleResolvedTarget()
    {
        var one = new DeviceCandidate(IdA, "PC", "SER1", null);
        Assert.Equal(IdA, IntuneCommand.ReportResolution(new TargetResolution.Resolved(one)));
        Assert.Null(IntuneCommand.ReportResolution(new TargetResolution.NotFound("SER9")));
        Assert.Null(IntuneCommand.ReportResolution(new TargetResolution.Ambiguous("SER1", new[] { one, one with { Id = IdB } })));
        Assert.Null(IntuneCommand.ReportResolution(new TargetResolution.Invalid("x'", "bad")));
    }

    [Fact]
    public void RecordState_RefusesDuplicatesAndNonSerials()
    {
        var clean = new GraphService.DeviceRecordState { Serial = "SER1", IntuneCandidates = { Device(IdA, "SER1") } };
        Assert.Null(clean.TargetRefusal);

        var dup = new GraphService.DeviceRecordState { Serial = "SER1", IntuneCandidates = { Device(IdA, "SER1"), Device(IdB, "SER1") } };
        Assert.Contains("2 Intune records", dup.TargetRefusal);
        Assert.Contains(IdA, dup.TargetRefusal);

        var autopilotDup = new GraphService.DeviceRecordState
        {
            Serial = "SER1",
            AutopilotCandidates = { new AutopilotDevice { Id = "ap1" }, new AutopilotDevice { Id = "ap2" } }
        };
        Assert.Contains("2 Autopilot identities", autopilotDup.TargetRefusal);

        var invalid = new GraphService.DeviceRecordState { Serial = "x' or 1 eq 1", InvalidSerial = true };
        Assert.Contains("not a serial number", invalid.TargetRefusal);
    }
}

public class DestructiveCommandShapeTests
{
    private static System.CommandLine.Command Sub(System.CommandLine.Command parent, string name) =>
        parent.Subcommands.Single(c => c.Name == name);

    [Fact]
    public void EntraDeleteDevice_TakesOneIdAndHasNoAllFlag()
    {
        var entra = FleetMate.Commands.Identity.EntraCommand.Create(null, null);
        var delete = Sub(entra, "delete-device");
        Assert.Single(delete.Arguments);
        Assert.Equal("id", delete.Arguments[0].Name);
        Assert.DoesNotContain(delete.Options, o => o.Name == "all" || o.Aliases.Contains("--all"));
        Assert.Contains(delete.Options, o => o.Aliases.Contains("--confirm"));
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("wipe")]
    [InlineData("retire")]
    [InlineData("delete")]
    [InlineData("autopilot-reset")]
    public void IntuneDestructiveCommands_RequireConfirm(string name)
    {
        var intune = IntuneCommand.Create(null, null);
        Assert.Contains(Sub(intune, name).Options, o => o.Aliases.Contains("--confirm"));
    }
}
