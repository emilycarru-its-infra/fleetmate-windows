using System.Text;
using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services;
using FleetMate.Core.Services.SignIn;
using FleetMate.Core.Services.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The TeamDynamix browser sign-in both apps share (TdxBrowserSignIn,
/// TdxWindowSignIn) driven through a scripted ISignInBrowser, and the
/// coordinator's sign-in window. Synthetic values only; no network.
/// </summary>
public sealed class SignInBrowserTests : IDisposable
{
    private const string BaseUrl = "https://service.example.invalid";
    private const string Upn = "pat@example.invalid";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fm-signin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static string Jwt(string email)
    {
        static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64Url(Encoding.UTF8.GetBytes("{\"alg\":\"none\"}"));
        var exp = DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeSeconds();
        var body = B64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { email, upn = email, exp })));
        return $"{header}.{body}.sig";
    }

    /// <summary>A browser whose pages are a script: where each address lands and what its body says.</summary>
    private sealed class ScriptedBrowser(Func<string, string> landing, Func<string, string> body) : ISignInBrowser
    {
        public List<string> Visited { get; } = new();
        public string Source { get; private set; } = "";

        public Task<string> ExecuteScriptAsync(string script) =>
            Task.FromResult(script.Contains("innerText") ? JsonSerializer.Serialize(body(Source)) : "null");

        public void Navigate(string uri)
        {
            var starting = new SignInNavigationStarting(uri);
            NavigationStarting?.Invoke(starting);
            if (starting.Cancel) return;
            Visited.Add(uri);
            Source = landing(uri);
            NavigationCompleted?.Invoke(true, "Success");
        }

        public event Action<SignInNavigationStarting>? NavigationStarting;
        public event Action<bool, string>? NavigationCompleted;
        public event Action<string>? WebMessageReceived;

        public void Post(string json) => WebMessageReceived?.Invoke(json);
    }

    private static ScriptedBrowser SignedInSite(string tokenEmail) => new(
        uri => uri.Contains("loginsso", StringComparison.OrdinalIgnoreCase) ? uri : BaseUrl + "/TDClient/Home/",
        source => source.Contains("loginsso", StringComparison.OrdinalIgnoreCase) ? Jwt(tokenEmail) : "");

    [Fact]
    public async Task HeadlessSignInAsksForTheJwtOnceSignedInAndChecksItsAccount()
    {
        var browser = SignedInSite(Upn);
        var result = await TdxBrowserSignIn.HeadlessAsync(browser, BaseUrl, Upn, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains(browser.Visited, v => v.Contains("loginsso", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HeadlessSignInRefusesAnotherAccountsToken()
    {
        var browser = SignedInSite("someone.else@example.invalid");
        var result = await TdxBrowserSignIn.HeadlessAsync(browser, BaseUrl, Upn, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.IdentityRefused);
    }

    [Fact]
    public async Task HeadlessSignInStopsAtThePasskeyPageForTheWindow()
    {
        var browser = new ScriptedBrowser(_ => "https://login.microsoftonline.com/common/fido/get?x=1", _ => "");
        var result = await TdxBrowserSignIn.HeadlessAsync(browser, BaseUrl, Upn, CancellationToken.None);

        Assert.True(result.NeedsInteraction);
        Assert.True(TdxSsoResult.ShouldOfferWindow(result));
    }

    [Fact]
    public void WindowSignInFinishesOnceWithTheCheckedToken()
    {
        var browser = SignedInSite(Upn);
        var flow = new TdxWindowSignIn(browser, BaseUrl, Upn);
        var finished = new List<TdxSsoResult>();
        flow.Finished += finished.Add;

        flow.Start();
        SpinUntil(() => finished.Count > 0);
        flow.Finish(TdxSsoResult.Failed("late"));

        Assert.Single(finished);
        Assert.True(finished[0].Success, finished[0].Error);
        Assert.Equal("Signed in. Finishing…", flow.Status);
    }

    // MARK: - The coordinator's sign-in window

    private TdxSessionCoordinator Coordinator(TdxConfig config, TdxService tdx, AuthManager auth, TdxSsoResult silent) =>
        new(config, tdx, auth, (_, _, _) => Task.FromResult(silent),
            store: new TdxSessionCache(_dir),
            peers: Array.Empty<TdxSessionCache>(),
            renewalLock: new TdxRenewalLock(Path.Combine(_dir, "renewal.lock")));

    private static FleetMateConfig Config() => new() { Tdx = new TdxConfig { BaseUrl = BaseUrl } };

    [Fact]
    public async Task SignInOpensTheWindowWhenEntraWantsASecondFactor()
    {
        var config = Config();
        using var tdx = new TdxService(config.Tdx!);
        using var session = Coordinator(config.Tdx!, tdx, new AuthManager(config), TdxSsoResult.NeedsSignIn(TdxBrowserSignIn.MfaReason));
        var opened = 0;
        session.InteractiveSignIn = _ =>
        {
            opened++;
            return Task.FromResult<TdxSsoResult?>(new TdxSsoResult
            {
                Success = true, Token = "synthetic-token-value", Expiry = DateTime.UtcNow.AddHours(8), UserEmail = Upn,
            });
        };

        Assert.True(await session.RetryAsync());
        Assert.Equal(1, opened);
        Assert.True(session.IsSignedIn);
    }

    [Fact]
    public async Task SignInDoesNotOpenTheWindowForTheWrongAccount()
    {
        var config = Config();
        using var tdx = new TdxService(config.Tdx!);
        using var session = Coordinator(config.Tdx!, tdx, new AuthManager(config), TdxSsoResult.Refused("another account"));
        var opened = 0;
        session.InteractiveSignIn = _ => { opened++; return Task.FromResult<TdxSsoResult?>(null); };

        Assert.False(await session.RetryAsync());
        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task TheInboxHelperNeverOpensTheWindow()
    {
        var config = Config();
        using var tdx = new TdxService(config.Tdx!);
        using var session = Coordinator(config.Tdx!, tdx, new AuthManager(config), TdxSsoResult.NeedsSignIn(TdxBrowserSignIn.MfaReason));
        session.ReportsStatus = false;
        var opened = 0;
        session.InteractiveSignIn = _ => { opened++; return Task.FromResult<TdxSsoResult?>(null); };

        Assert.False(await session.RetryAsync());
        Assert.Equal(0, opened);
    }

    private static void SpinUntil(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < until) Thread.Sleep(10);
    }
}
