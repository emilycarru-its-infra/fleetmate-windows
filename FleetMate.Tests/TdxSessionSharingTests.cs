using System.Net;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// A window holding a TeamDynamix token takes a fresher one another process
/// saved, and a 401 is recovered from once. Every token, address and URL is
/// synthetic, and nothing here reaches a server.
/// </summary>
public sealed class TdxSessionSharingTests : IDisposable
{
    private const string Address = "pat@example.invalid";
    private const string BaseUrl = "https://service.example.com";
    private readonly string _ownDir = Path.Combine(Path.GetTempPath(), "fm-share-own-" + Guid.NewGuid().ToString("N"));
    private readonly string _peerDir = Path.Combine(Path.GetTempPath(), "fm-share-peer-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var dir in new[] { _ownDir, _peerDir })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private static string Jwt(string email, string nonce)
    {
        static string B64(string s) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"HS256\"}")}.{B64(JsonSerializer.Serialize(new { email, nonce }))}.signature";
    }

    private static TdxSession Saved(string token, DateTime expiry, string address = Address) => new()
    {
        Token = token,
        ExpiryUtc = expiry,
        UserEmail = address,
        UserName = "Pat Example",
        VerifiedAddress = address,
        BaseUrl = BaseUrl,
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string?, HttpStatusCode> _status;
        public List<string?> Tokens { get; } = new();

        public StubHandler(Func<string?, HttpStatusCode> status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var token = request.Headers.Authorization?.Parameter;
            Tokens.Add(token);
            return Task.FromResult(new HttpResponseMessage(_status(token))
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            });
        }
    }

    private TdxService Service(StubHandler handler)
    {
        var service = new TdxService(new TdxConfig { BaseUrl = BaseUrl, AppId = 7 }, handler)
        {
            SharedSession = new TdxSessionCache(_ownDir),
            PeerSessions = new[] { new TdxSessionCache(_peerDir) },
            ExpectedAddressResolver = _ => Task.FromResult<string?>(Address),
            // Never the network: a missing token is reported, not fetched.
            Renewer = _ => Task.FromResult(false),
        };
        return service;
    }

    private static Task Search(TdxService service) =>
        service.SearchTicketsOrThrowAsync(new TicketSearchRequest { MaxResults = 1 });

    // MARK: - Saved sessions

    [Fact]
    public async Task AFresherSavedSessionIsUsedBeforeTheHeldToken()
    {
        var held = Jwt(Address, "held");
        var fresh = Jwt(Address, "fresh");
        var handler = new StubHandler(_ => HttpStatusCode.OK);
        var service = Service(handler);
        service.SetSsoToken(held, DateTime.UtcNow.AddHours(3), Address, "Pat Example");
        TdxSession? adopted = null;
        service.SharedSessionAdopted += s => adopted = s;

        new TdxSessionCache(_ownDir).Save(Saved(fresh, DateTime.UtcNow.AddHours(20)));
        await Search(service);

        Assert.Equal(fresh, handler.Tokens.Single());
        Assert.Equal(fresh, adopted?.Token);
        Assert.Equal(fresh, service.CurrentSsoToken);
    }

    [Fact]
    public async Task TheOtherEditionsSessionIsAdoptedToo()
    {
        var fresh = Jwt(Address, "peer");
        var handler = new StubHandler(_ => HttpStatusCode.OK);
        var service = Service(handler);

        new TdxSessionCache(_peerDir).Save(Saved(fresh, DateTime.UtcNow.AddHours(20)));
        await Search(service);

        Assert.Equal(fresh, handler.Tokens.Single());
    }

    [Fact]
    public async Task AnOlderSavedSessionDoesNotReplaceTheHeldToken()
    {
        var held = Jwt(Address, "held");
        var handler = new StubHandler(_ => HttpStatusCode.OK);
        var service = Service(handler);
        service.SetSsoToken(held, DateTime.UtcNow.AddHours(20), Address, "Pat Example");

        new TdxSessionCache(_ownDir).Save(Saved(Jwt(Address, "older"), DateTime.UtcNow.AddHours(4)));
        await Search(service);

        Assert.Equal(held, handler.Tokens.Single());
    }

    [Fact]
    public async Task AnotherAccountsSavedSessionIsNeverAdoptedAndIsRemovedFromThisEditionsFile()
    {
        var held = Jwt(Address, "held");
        var other = Jwt("other@example.invalid", "other");
        var handler = new StubHandler(_ => HttpStatusCode.OK);
        var service = Service(handler);
        service.SetSsoToken(held, DateTime.UtcNow.AddHours(3), Address, "Pat Example");

        var own = new TdxSessionCache(_ownDir);
        var peer = new TdxSessionCache(_peerDir);
        own.Save(Saved(other, DateTime.UtcNow.AddHours(20), "other@example.invalid"));
        peer.Save(Saved(other, DateTime.UtcNow.AddHours(20), "other@example.invalid"));
        await Search(service);

        Assert.Equal(held, handler.Tokens.Single());
        Assert.Null(own.Load());
        // The other edition's file is only ever read.
        Assert.NotNull(peer.Load());
    }

    [Fact]
    public async Task ASavedTokenThatDoesNotCarryTheExpectedAddressIsRefused()
    {
        var handler = new StubHandler(_ => HttpStatusCode.OK);
        var service = Service(handler);
        // Labelled as this account's, but the token itself names someone else.
        new TdxSessionCache(_peerDir).Save(Saved(Jwt("other@example.invalid", "x"), DateTime.UtcNow.AddHours(20)));

        await Assert.ThrowsAsync<TdxRequestException>(() => Search(service));
        Assert.Empty(handler.Tokens);
    }

    [Fact]
    public async Task AnUnchangedFileIsNotReadAgain()
    {
        var handler = new StubHandler(_ => HttpStatusCode.OK);
        var service = Service(handler);
        var resolved = 0;
        service.ExpectedAddressResolver = _ => { resolved++; return Task.FromResult<string?>(Address); };
        new TdxSessionCache(_ownDir).Save(Saved(Jwt(Address, "a"), DateTime.UtcNow.AddHours(20)));

        await Search(service);
        await Search(service);
        await Search(service);

        Assert.Equal(1, resolved);
        Assert.Equal(3, handler.Tokens.Count);
    }

    // MARK: - 401

    [Fact]
    public async Task A401IsRecoveredOnceAndTheRequestSentAgain()
    {
        var held = Jwt(Address, "held");
        var fresh = Jwt(Address, "fresh");
        var handler = new StubHandler(t => t == held ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
        var service = Service(handler);
        service.SetSsoToken(held, DateTime.UtcNow.AddHours(3), Address, "Pat Example");
        var urgent = new List<bool>();
        service.Renewer = u =>
        {
            urgent.Add(u);
            service.SetSsoToken(fresh, DateTime.UtcNow.AddHours(20), Address, "Pat Example");
            return Task.FromResult(true);
        };
        var rejected = 0;
        service.TokenRejected += () => rejected++;

        await Search(service);

        Assert.Equal(new[] { held, fresh }, handler.Tokens);
        Assert.Equal(new[] { true }, urgent);
        Assert.Equal(1, rejected);
    }

    [Fact]
    public async Task A401WithNoRecoveryDropsTheTokenAndStands()
    {
        var held = Jwt(Address, "held");
        var handler = new StubHandler(_ => HttpStatusCode.Unauthorized);
        var service = Service(handler);
        service.SetSsoToken(held, DateTime.UtcNow.AddHours(3), Address, "Pat Example");
        new TdxSessionCache(_ownDir).Save(Saved(held, DateTime.UtcNow.AddHours(3)));
        var renewals = 0;
        service.Renewer = _ => { renewals++; return Task.FromResult(false); };

        var ex = await Assert.ThrowsAsync<TdxRequestException>(() => Search(service));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Single(handler.Tokens);
        Assert.Equal(1, renewals);
        Assert.False(service.IsSsoAuthenticated);
        // The refused token is not left in the saved session for the next launch.
        Assert.Null(new TdxSessionCache(_ownDir).Load());
    }

    [Fact]
    public async Task ARefusedTokenIsNotAdoptedBackFromAnotherProcessesFile()
    {
        var held = Jwt(Address, "held");
        var handler = new StubHandler(_ => HttpStatusCode.Unauthorized);
        var service = Service(handler);
        service.SetSsoToken(held, DateTime.UtcNow.AddHours(3), Address, "Pat Example");
        new TdxSessionCache(_peerDir).Save(Saved(held, DateTime.UtcNow.AddHours(3)));

        await Assert.ThrowsAsync<TdxRequestException>(() => Search(service));
        await Assert.ThrowsAsync<TdxRequestException>(() => Search(service));

        // Sent once; the second search finds no token it may use.
        Assert.Single(handler.Tokens);
    }

    [Fact]
    public async Task A401InsideASignInDoesNotStartAnother()
    {
        var held = Jwt(Address, "held");
        var handler = new StubHandler(_ => HttpStatusCode.Unauthorized);
        var service = Service(handler);
        service.SetSsoToken(held, DateTime.UtcNow.AddHours(3), Address, "Pat Example");
        var renewals = 0;
        service.Renewer = _ => { renewals++; return Task.FromResult(false); };

        using (TdxService.BeginSignIn())
            await Assert.ThrowsAsync<TdxRequestException>(() => Search(service));

        Assert.Equal(0, renewals);
        Assert.False(service.IsSsoAuthenticated);
    }

    [Fact]
    public async Task ANoRetryRequestReportsTheRefusalButIsNeverSentAgain()
    {
        var sent = 0;
        var calls = new List<(string Token, bool Retry)>();
        var handler = new TdxReauthHandler((token, retry, _) =>
        {
            calls.Add((token, retry));
            return Task.FromResult<string?>("another-token");
        })
        {
            InnerHandler = new StubHandler(_ => { sent++; return HttpStatusCode.Unauthorized; }),
        };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/x");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "captured");
        request.Options.Set(TdxReauthHandler.NoRetry, true);

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, sent);
        Assert.Equal(new[] { ("captured", false) }, calls);
    }

    [Fact]
    public void OnlyBodiesHeldInMemoryAreSentAgain()
    {
        Assert.True(TdxReauthHandler.CanResend(null));
        Assert.True(TdxReauthHandler.CanResend(new StringContent("{}")));
        Assert.True(TdxReauthHandler.CanResend(new MultipartFormDataContent { new ByteArrayContent(new byte[] { 1 }) }));
        Assert.False(TdxReauthHandler.CanResend(new StreamContent(new MemoryStream(new byte[] { 1 }))));
        Assert.False(TdxReauthHandler.CanResend(new MultipartFormDataContent { new StreamContent(new MemoryStream()) }));
    }
}

/// <summary>The sign-in window fills in the Windows account on Entra's request.</summary>
public class EntraLoginHintTests
{
    private const string Upn = "pat@example.invalid";

    [Fact]
    public void ASamlRedirectRequestGetsTheAccount()
    {
        var url = EntraWebSignIn.WithLoginHint(
            "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000/saml2?SAMLRequest=abc%3D&RelayState=xyz", Upn);

        Assert.NotNull(url);
        Assert.Contains("SAMLRequest=abc%3D", url);
        Assert.EndsWith("&login_hint=pat%40example.invalid", url);
    }

    [Fact]
    public void AnAuthorizeRequestGetsTheAccount()
    {
        var url = EntraWebSignIn.WithLoginHint(
            "https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize?client_id=abc&response_type=code", Upn);

        Assert.EndsWith("&login_hint=pat%40example.invalid", url);
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/t/saml2?SAMLRequest=abc&login_hint=x%40example.invalid")]
    [InlineData("https://login.microsoftonline.com/t/oauth2/v2.0/authorize?client_id=abc&username=x%40example.invalid")]
    [InlineData("https://login.microsoftonline.com/t/saml2")]
    [InlineData("https://login.microsoftonline.com/common/login")]
    [InlineData("https://service.example.com/saml2?SAMLRequest=abc")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void AnythingElseIsLeftAlone(string? url)
    {
        Assert.Null(EntraWebSignIn.WithLoginHint(url, Upn));
    }

    [Fact]
    public void NoAccountMeansNoRewrite()
    {
        Assert.Null(EntraWebSignIn.WithLoginHint("https://login.microsoftonline.com/t/saml2?SAMLRequest=abc", " "));
    }
}
