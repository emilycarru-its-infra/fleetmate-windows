using System.Net;
using System.Text;
using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// When the Windows broker has no token for an API (as in a disconnected remote
/// session, where it will not surface the operating-system account), the token
/// source tries the hidden browser before giving up, never a window, and a
/// failure carries what was tried.
/// </summary>
public class EntraSilentFallbackTests
{
    private const string Tenant = "00000000-0000-0000-0000-000000000000";

    private static Task<(string, DateTimeOffset)> BrokerRefuses(string scope, CancellationToken _) =>
        throw new EntraTokenException(scope, "no silent credential from the Windows sign-in broker (operating-system account: failed_to_acquire_token_silently_from_broker)");

    [Fact]
    public async Task TheHiddenBrowserServesWhenTheBrokerCannot()
    {
        EntraHeadlessRequest? asked = null;
        var source = new EntraTokenSource(Tenant)
        {
            AcquireOverride = BrokerRefuses,
            UpnOverride = () => "Operator@Example.EDU",
            HeadlessOverride = (request, _) =>
            {
                asked = request;
                return Task.FromResult(("web-token", DateTimeOffset.UtcNow.AddHours(1)));
            },
        };

        Assert.Equal("web-token", await source.GetTokenAsync("00000000-0000-0000-0000-00000000aaaa"));
        Assert.NotNull(asked);
        Assert.Equal("00000000-0000-0000-0000-00000000aaaa/.default", asked!.Scope);
        Assert.Equal(EntraTokenSource.AzureCliClientId, asked.ClientId);
        Assert.Equal(Tenant, asked.Tenant);
        Assert.Equal("operator@example.edu", asked.LoginHint);
    }

    [Fact]
    public async Task AFailureNamesBothPaths()
    {
        var source = new EntraTokenSource(Tenant)
        {
            AcquireOverride = BrokerRefuses,
            HeadlessOverride = (_, _) => throw new TimeoutException("stopped at login.microsoftonline.com/common/login after 95s"),
        };

        var ex = await Assert.ThrowsAsync<EntraTokenException>(() => source.GetTokenAsync("api://snipe"));
        Assert.Contains("failed_to_acquire_token_silently_from_broker", ex.Message);
        Assert.Contains("hidden browser", ex.Message);
        Assert.Contains("stopped at", ex.Message);
    }

    [Fact]
    public async Task WithNoHiddenBrowserTheBrokersReasonStands()
    {
        var source = new EntraTokenSource(Tenant) { AcquireOverride = BrokerRefuses };
        var ex = await Assert.ThrowsAsync<EntraTokenException>(() => source.GetTokenAsync("api://snipe"));
        Assert.Contains("failed_to_acquire_token_silently_from_broker", ex.Message);
    }

    // A paged list or a row of widgets must not run the whole silent chain,
    // hidden browser and all, once per request.
    [Fact]
    public async Task AFailureIsHeldBrieflyThenRetriedAfterInvalidate()
    {
        var attempts = 0;
        var source = new EntraTokenSource(Tenant)
        {
            AcquireOverride = (scope, ct) =>
            {
                attempts++;
                return BrokerRefuses(scope, ct);
            },
        };

        await Assert.ThrowsAsync<EntraTokenException>(() => source.GetTokenAsync("api://snipe"));
        await Assert.ThrowsAsync<EntraTokenException>(() => source.GetTokenAsync("api://snipe"));
        Assert.Equal(1, attempts);

        source.Invalidate();
        await Assert.ThrowsAsync<EntraTokenException>(() => source.GetTokenAsync("api://snipe"));
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData("Operator@Example.EDU", "operator@example.edu")]
    [InlineData(@"DOMAIN\operator", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheWindowsAccountIsAnAddressOrNothing(string? raw, string? expected)
    {
        var source = new EntraTokenSource(Tenant) { UpnOverride = () => raw };
        Assert.Equal(expected, source.WindowsUpn());
    }

    [Fact]
    public void TheAuthorizeAddressCarriesScopeHintAndChallenge()
    {
        var flow = new EntraAuthCodeFlow(new EntraHeadlessRequest("api://snipe/.default", "client-id", Tenant, "operator@example.edu"));
        var url = flow.AuthorizeUrl.AbsoluteUri;

        Assert.StartsWith($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize?", url);
        Assert.Contains("client_id=client-id", url);
        Assert.Contains("scope=api%3A%2F%2Fsnipe%2F.default", url);
        Assert.Contains("login_hint=operator%40example.edu", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost", url);
        Assert.DoesNotContain("prompt=", url);
    }

    [Theory]
    [InlineData("http://localhost/?code=abc", true)]
    [InlineData("http://localhost?error=access_denied", true)]
    [InlineData("https://localhost/?code=abc", false)]
    [InlineData("http://localhost/other?code=abc", false)]
    [InlineData("https://login.microsoftonline.com/common/oauth2/v2.0/authorize", false)]
    public void OnlyTheLoopbackRedirectIsTaken(string url, bool expected)
    {
        Assert.Equal(expected, EntraAuthCodeFlow.IsRedirect(new Uri(url)));
    }

    private sealed class TokenEndpoint : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public string? Posted { get; private set; }

        public TokenEndpoint(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Posted = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task TheCodeIsRedeemedWithItsVerifier()
    {
        var endpoint = new TokenEndpoint(HttpStatusCode.OK, """{"access_token":"api-token","expires_in":3599}""");
        var flow = new EntraAuthCodeFlow(new EntraHeadlessRequest("api://snipe/.default", "client-id", Tenant, null), endpoint);

        var (token, expires) = await flow.RedeemAsync(new Uri("http://localhost/?code=the-code"));

        Assert.Equal("api-token", token);
        Assert.True(expires > DateTimeOffset.UtcNow.AddMinutes(50));
        Assert.Contains("grant_type=authorization_code", endpoint.Posted);
        Assert.Contains("code=the-code", endpoint.Posted);
        Assert.Contains("code_verifier=", endpoint.Posted);
    }

    [Fact]
    public async Task ARefusedExchangeSaysWhy()
    {
        var endpoint = new TokenEndpoint(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"The user has not consented.\r\nTrace ID: x"}""");
        var flow = new EntraAuthCodeFlow(new EntraHeadlessRequest("api://snipe/.default", "client-id", Tenant, null), endpoint);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => flow.RedeemAsync(new Uri("http://localhost/?code=c")));
        Assert.Contains("not consented", ex.Message);
        Assert.DoesNotContain("Trace ID", ex.Message);
    }

    [Fact]
    public async Task AnErrorRedirectIsAFailure()
    {
        var flow = new EntraAuthCodeFlow(new EntraHeadlessRequest("api://snipe/.default", "client-id", Tenant, null));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            flow.RedeemAsync(new Uri("http://localhost/?error=interaction_required&error_description=Sign-in+needed")));
        Assert.Contains("Sign-in needed", ex.Message);
    }
}
