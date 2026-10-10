using System.Text;
using System.Text.Json;
using FleetMate.Core.Services.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// A TeamDynamix session not confirmed as the Windows account's must fail the
/// sign-in and never hand its token on.
/// </summary>
public class TdxSsoIdentityTests
{
    private static string MakeJwt(object payload)
    {
        static string B64(string s) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{B64("{\"alg\":\"HS256\"}")}.{B64(JsonSerializer.Serialize(payload))}.signature";
    }

    [Theory]
    [InlineData("adoe@example.edu", "adoe@example.edu")]
    [InlineData(" ADoe@Example.edu ", "adoe@example.edu")]
    [InlineData("adoe@example.edu", "ADOE@EXAMPLE.EDU")]
    public void Verify_AMatchingAddressSucceeds(string claimed, string expected)
    {
        var token = MakeJwt(new { given_name = "Alex", email = claimed });

        var result = TdxSsoIdentity.Verify(token, expected);

        Assert.True(result.Success);
        Assert.False(result.IdentityRefused);
        Assert.Equal(token, result.Token);
        Assert.Equal("adoe@example.edu", result.UserEmail);
        Assert.Equal("Alex", result.UserName);
    }

    [Theory]
    [InlineData("email")]
    [InlineData("upn")]
    [InlineData("unique_name")]
    public void Verify_ADifferentAddressFailsAndDropsTheToken(string claim)
    {
        var token = MakeJwt(new Dictionary<string, string> { [claim] = "aws-adoe@example.edu" });

        var result = TdxSsoIdentity.Verify(token, "adoe@example.edu");

        Assert.False(result.Success);
        Assert.True(result.IdentityRefused);
        Assert.Null(result.Token);
        Assert.Equal("TDX session belongs to aws-adoe@example.edu; expected adoe@example.edu", result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Alex Doe")]
    public void Verify_AMissingAddressFailsAndDropsTheToken(string? claimed)
    {
        var token = claimed == null
            ? MakeJwt(new { given_name = "Alex" })
            : MakeJwt(new { given_name = "Alex", email = claimed });

        var result = TdxSsoIdentity.Verify(token, "ADoe@example.edu");

        Assert.False(result.Success);
        Assert.True(result.IdentityRefused);
        Assert.Null(result.Token);
        Assert.Null(result.UserEmail);
        Assert.Equal(TdxSsoResult.NoAddressInTokenReason, result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-address")]
    public void Verify_AnUnknownExpectedAddressFailsAndDropsTheToken(string? expected)
    {
        var token = MakeJwt(new { email = "adoe@example.edu" });

        var result = TdxSsoIdentity.Verify(token, expected);

        Assert.False(result.Success);
        Assert.True(result.IdentityRefused);
        Assert.Null(result.Token);
        Assert.Equal(TdxSsoResult.UnknownExpectedAddressReason, result.Error);
    }

    [Fact]
    public void Verify_NeitherAddressKnownFailsOnTheExpectedOne()
    {
        var token = MakeJwt(new { given_name = "Alex" });

        var result = TdxSsoIdentity.Verify(token, null);

        Assert.False(result.Success);
        Assert.True(result.IdentityRefused);
        Assert.Equal(TdxSsoResult.UnknownExpectedAddressReason, result.Error);
    }

    [Fact]
    public void Verify_ReadsTheTokensExpiry()
    {
        var exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        var token = MakeJwt(new { email = "adoe@example.edu", exp });

        var result = TdxSsoIdentity.Verify(token, "adoe@example.edu");

        Assert.True(result.Expiry < DateTime.UtcNow.AddHours(1));
    }
}
