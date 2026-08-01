using System.CommandLine;
using System.Text;
using System.Text.Json;
using FleetMate.Commands.Shared;
using FleetMate.Core.Config;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Covers the parts of <c>fleetmate login</c> that do not need a live tenant:
/// claim parsing, redaction, and command wiring. The broker round-trip itself
/// can only be exercised on an Entra-joined device.
/// </summary>
public class LoginIdentityTests
{
    private static string MakeToken(object payload)
    {
        static string B64(string s) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{B64("{\"alg\":\"RS256\"}")}.{B64(JsonSerializer.Serialize(payload))}.sig";
    }

    [Fact]
    public void ReadsTheUpnAndTenantFromAToken()
    {
        var token = MakeToken(new { upn = "ada@example.edu", tid = "8f1e2d3c-0000-0000-0000-abcdef123456" });

        var (upn, tenant) = LoginCommand.ReadIdentity(token);

        Assert.Equal("ada@example.edu", upn);
        Assert.Equal("8f1e2d3c-0000-0000-0000-abcdef123456", tenant);
    }

    [Theory]
    // Entra is inconsistent about which claim carries the sign-in name, so the
    // fallback chain matters more than any single claim.
    [InlineData("upn")]
    [InlineData("preferred_username")]
    [InlineData("unique_name")]
    [InlineData("email")]
    public void FallsBackAcrossTheUsernameClaims(string claim)
    {
        var payload = new Dictionary<string, object> { [claim] = "ada@example.edu" };
        var (upn, _) = LoginCommand.ReadIdentity(MakeToken(payload));

        Assert.Equal("ada@example.edu", upn);
    }

    [Fact]
    public void ReturnsNothingRatherThanThrowingOnAnOpaqueToken()
    {
        // A token FleetMate cannot parse is still a token that worked — the
        // command must report the sign-in as good, just without a name.
        var (upn, tenant) = LoginCommand.ReadIdentity("not-a-jwt");

        Assert.Null(upn);
        Assert.Null(tenant);
    }

    [Fact]
    public void SurvivesAMalformedPayloadSegment()
    {
        var (upn, _) = LoginCommand.ReadIdentity("eyJhbGciOiJSUzI1NiJ9.!!!not-base64!!!.sig");
        Assert.Null(upn);
    }

    [Fact]
    public void SurvivesAnEmptyToken()
    {
        var (upn, tenant) = LoginCommand.ReadIdentity(string.Empty);
        Assert.Null(upn);
        Assert.Null(tenant);
    }

    [Theory]
    [InlineData("8f1e2d3c-1111-2222-3333-444455556666", "8f1e2d3c…")]
    [InlineData("short", "short")]
    [InlineData("", "")]
    public void ShortensTenantIdsForDisplay(string input, string expected)
    {
        Assert.Equal(expected, LoginCommand.ShortId(input));
    }
}

public class LoginCommandWiringTests
{
    [Fact]
    public void RegistersTheExpectedOptions()
    {
        var command = LoginCommand.Create(new FleetMateConfig());

        var names = command.Options.SelectMany(o => o.Aliases).ToList();

        Assert.Equal("login", command.Name);
        Assert.Contains("--check", names);
        Assert.Contains("--json", names);
    }

    [Fact]
    public void ParsesCheckAndJsonWithoutError()
    {
        var command = LoginCommand.Create(new FleetMateConfig());

        var result = command.Parse("--check --json");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void RejectsAnUnknownOption()
    {
        var command = LoginCommand.Create(new FleetMateConfig());

        var result = command.Parse("--definitely-not-an-option");

        Assert.NotEmpty(result.Errors);
    }
}
