using System.Text;
using System.Text.Json;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Covers the auth plumbing that has no server to talk to in a test: URL shapes,
/// token validation, and audience-to-scope translation. These are the parts that
/// failed silently in production — a wrong URL and a malformed token both surface
/// as "auth failed", which is why they are pinned here.
/// </summary>
public class TdxSsoUrlTests
{
    [Theory]
    // Configured with the API path already on it — the documented form.
    [InlineData("https://td.example.edu/TDWebApi", "https://td.example.edu/TDWebApi/api/auth/loginsso")]
    // Configured without it. This is the case that used to 404: the old code
    // computed a root URL and then appended to the *base* instead.
    [InlineData("https://td.example.edu", "https://td.example.edu/TDWebApi/api/auth/loginsso")]
    // Trailing slashes must not double up.
    [InlineData("https://td.example.edu/TDWebApi/", "https://td.example.edu/TDWebApi/api/auth/loginsso")]
    [InlineData("https://td.example.edu/", "https://td.example.edu/TDWebApi/api/auth/loginsso")]
    [InlineData("td.example.edu", "https://td.example.edu/TDWebApi/api/auth/loginsso")]
    // Casing varies between TDX instances.
    [InlineData("https://td.example.edu/tdwebapi", "https://td.example.edu/TDWebApi/api/auth/loginsso")]
    public void BuildLoginSsoUrl_IsIdempotent_AcrossBaseUrlShapes(string baseUrl, string expected)
    {
        Assert.Equal(expected, TdxSsoService.BuildLoginSsoUrl(baseUrl));
    }

    [Theory]
    [InlineData("https://td.example.edu/TDWebApi", "https://td.example.edu/TDWorkManagement/")]
    [InlineData("https://td.example.edu", "https://td.example.edu/TDWorkManagement/")]
    [InlineData("https://td.example.edu/TDWebApi/", "https://td.example.edu/TDWorkManagement/")]
    [InlineData("td.example.edu", "https://td.example.edu/TDWorkManagement/")]
    public void BuildEntryUrl_StripsTheApiSegment(string baseUrl, string expected)
    {
        Assert.Equal(expected, TdxSsoService.BuildEntryUrl(baseUrl));
    }
}

public class TdxApiUrlTests
{
    [Theory]
    [InlineData("servicedesk.example.edu", "https://servicedesk.example.edu/TDWebApi/api/115/tickets/search")]
    [InlineData("https://servicedesk.example.edu/TDWebApi", "https://servicedesk.example.edu/TDWebApi/api/115/tickets/search")]
    public void TicketUrls_NormalizeHostAndApiSegment(string baseUrl, string expected)
    {
        var config = new FleetMate.Core.Models.Tickets.TdxConfig { BaseUrl = baseUrl, AppId = 116, TicketingAppId = 115 };
        Assert.Equal(expected, config.GetTicketsUrl("search"));
    }
}

public class TdxJwtTests
{
    private static string MakeJwt(object payload)
    {
        static string B64(string s) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{B64("{\"alg\":\"HS256\"}")}.{B64(JsonSerializer.Serialize(payload))}.signature";
    }

    [Fact]
    public void LooksLikeJwt_AcceptsAThreeSegmentToken()
    {
        Assert.True(TdxSsoService.LooksLikeJwt(MakeJwt(new { name = "Ada" })));
    }

    [Theory]
    // An HTML error page that happens to start with the right prefix is not a
    // credential. The old check was prefix + length only, so this got through.
    [InlineData("eyJ this is not really a token at all, just prose")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.onlytwosegments")]
    [InlineData("eyJ")]
    [InlineData("")]
    [InlineData("<html><body>Access denied</body></html>")]
    public void LooksLikeJwt_RejectsNonTokens(string candidate)
    {
        Assert.False(TdxSsoService.LooksLikeJwt(candidate));
    }

    [Fact]
    public void ReadExpiry_UsesTheExpClaim()
    {
        var exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        var actual = TdxSsoService.ReadExpiry(MakeJwt(new { exp }));

        // Expiry is pulled back five minutes so a token never dies mid-flight.
        var expected = DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime.AddMinutes(-5);
        Assert.Equal(expected, actual, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ReadExpiry_DoesNotInventAHealthyTokenFromAShortLivedOne()
    {
        // A 30-minute token must not be reported as good for 23 hours — that was
        // the bug: HasValidToken stayed true while every call came back 401.
        var exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        Assert.True(TdxSsoService.ReadExpiry(MakeJwt(new { exp })) < DateTime.UtcNow.AddHours(1));
    }

    [Fact]
    public void ReadExpiry_FallsBackWhenTheClaimIsMissing()
    {
        var actual = TdxSsoService.ReadExpiry(MakeJwt(new { name = "Ada" }));
        Assert.True(actual > DateTime.UtcNow.AddHours(22));
    }

    [Fact]
    public void ExtractUserInfoFromJwt_ReadsNameAndEmail()
    {
        var token = MakeJwt(new { given_name = "Ada", email = "ada@example.edu" });
        var (name, email) = TdxSsoService.ExtractUserInfoFromJwt(token);

        Assert.Equal("Ada", name);
        Assert.Equal("ada@example.edu", email);
    }

    [Fact]
    public void ExtractUserInfoFromJwt_SurvivesGarbage()
    {
        var (name, email) = TdxSsoService.ExtractUserInfoFromJwt("not-a-token");
        Assert.Null(name);
        Assert.Null(email);
    }
}

public class EntraTokenSourceTests
{
    [Theory]
    // A bare app-id GUID is the common config form.
    [InlineData("4d6abdd9-5380-40a5-8f8e-fe41f317a29f", "4d6abdd9-5380-40a5-8f8e-fe41f317a29f/.default")]
    // An identifier URI works too.
    [InlineData("api://reportmate", "api://reportmate/.default")]
    [InlineData("https://graph.microsoft.com", "https://graph.microsoft.com/.default")]
    // Trailing slash must not produce a doubled separator.
    [InlineData("https://graph.microsoft.com/", "https://graph.microsoft.com/.default")]
    public void ToScope_QualifiesAnAudience(string audience, string expected)
    {
        Assert.Equal(expected, EntraTokenSource.ToScope(audience));
    }

    [Fact]
    public void ToScope_LeavesAnAlreadyQualifiedScopeAlone()
    {
        const string scope = "api://reportmate/.default";
        Assert.Equal(scope, EntraTokenSource.ToScope(scope));
    }
}

public class SecretlessConfigTests
{
    [Fact]
    public void SsoIsTheDefault_WhenNothingIsConfigured()
    {
        // The secretless path has to be what you get by not choosing, or it
        // never becomes the norm.
        var config = FleetMate.Core.Config.FleetMateConfig.Load();

        Assert.True(config.SnipeUsesOidc);
        Assert.True(config.ReportMateUsesOidc);
    }

    [Fact]
    public void GraphConfig_ExposesNoSecret()
    {
        // Guards the deprecation: if someone reintroduces a secret-bearing
        // property on GraphConfig, this fails rather than quietly shipping.
        var properties = typeof(FleetMate.Core.Config.GraphConfig)
            .GetProperties()
            .Select(p => p.Name)
            .ToList();

        Assert.DoesNotContain("ClientSecret", properties);
        Assert.DoesNotContain("DevicesClientSecret", properties);
        Assert.DoesNotContain("SystemsClientSecret", properties);
    }
}
