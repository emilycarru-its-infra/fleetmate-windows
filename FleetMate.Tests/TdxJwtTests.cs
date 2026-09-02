using System.Text;
using System.Text.Json;
using FleetMate.Core.Services.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>Verifies TDX JWT claim extraction (name/email) and malformed-token handling.</summary>
public class TdxJwtClaimTests
{
    private static string MakeJwt(object payload)
    {
        static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64Url(Encoding.UTF8.GetBytes("{\"alg\":\"none\"}"));
        var body = B64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        return $"{header}.{body}.sig";
    }

    [Fact]
    public void Extracts_GivenName_And_Email()
    {
        var jwt = MakeJwt(new { given_name = "Alex", email = "alex@example.edu" });
        var (name, email) = TdxJwt.ExtractUserInfo(jwt);
        Assert.Equal("Nigel", name);
        Assert.Equal("alex@example.edu", email);
    }

    [Fact]
    public void FallsBack_To_UniqueName_ForBoth()
    {
        var jwt = MakeJwt(new { unique_name = "user@example.edu" });
        var (name, email) = TdxJwt.ExtractUserInfo(jwt);
        Assert.Equal("user@example.edu", name);
        Assert.Equal("user@example.edu", email);
    }

    [Fact]
    public void Prefers_Name_Over_UniqueName()
    {
        var jwt = MakeJwt(new { name = "Real Name", unique_name = "u@x.ca" });
        var (name, _) = TdxJwt.ExtractUserInfo(jwt);
        Assert.Equal("Real Name", name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    public void Malformed_ReturnsNulls(string? token)
    {
        var (name, email) = TdxJwt.ExtractUserInfo(token);
        Assert.Null(name);
        Assert.Null(email);
    }
}
