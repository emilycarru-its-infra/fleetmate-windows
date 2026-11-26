using FleetMate.Core.Config;
using FleetMate.Core.Links;
using FleetMate.Core.Models;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The shell logic the WinUI and WPF apps share from FleetMate.Core: the tab
/// bar (FleetMate for Mac's order), link routing, and the TeamDynamix session
/// coordinator. Synthetic values only; nothing here reaches the network.
/// </summary>
public sealed class WinUIShellTests : IDisposable
{
    private static readonly string[] MacOrder =
        { "Development", "Projects", "Devices", "Reporting", "Manage", "Inventory", "Identity", "Teams", "Tickets" };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fm-shell-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // MARK: - Tabs

    [Fact]
    public void TabsFollowTheMacAppOrder()
    {
        Assert.Equal(MacOrder, AppTabs.Order);
        Assert.Equal(AppTabs.Order, FleetMate.GUI.Views.Shared.MainWindow.TabOrder);
    }

    [Fact]
    public void FleetMateOpensOnDevelopmentAndTicketsMateOnTickets()
    {
        var none = new HashSet<string>();
        Assert.Equal("Development", AppTabs.StartTab(AppEdition.FleetMate, AppTabs.Visible(AppEdition.FleetMate, none)));
        Assert.Equal(new[] { "Tickets" }, AppTabs.Visible(AppEdition.TicketsMate, none));
        Assert.Equal("Tickets", AppTabs.StartTab(AppEdition.TicketsMate, AppTabs.Visible(AppEdition.TicketsMate, none)));
    }

    [Fact]
    public void TicketsMateIgnoresHiddenModules()
    {
        var hidden = new HashSet<string> { "Tickets" };
        Assert.Equal(new[] { "Tickets" }, AppTabs.Visible(AppEdition.TicketsMate, hidden));
    }

    [Fact]
    public void HidingDevelopmentStartsOnTheFirstTabShowing()
    {
        var visible = AppTabs.Visible(AppEdition.FleetMate, new HashSet<string> { "Development" });
        Assert.Equal("Projects", AppTabs.StartTab(AppEdition.FleetMate, visible));
    }

    [Fact]
    public void ShortcutsCountVisibleTabsLikeTheMacApp()
    {
        var visible = AppTabs.Visible(AppEdition.FleetMate, new HashSet<string> { "Projects" });
        Assert.Equal("Development", AppTabs.ForShortcut(visible, 1));
        Assert.Equal("Devices", AppTabs.ForShortcut(visible, 2));
        Assert.Null(AppTabs.ForShortcut(visible, 9));
        Assert.Null(AppTabs.ForShortcut(visible, 0));
    }

    [Fact]
    public void EveryTabHasItsModuleGlyph()
    {
        foreach (var module in AppModules.Tabs) Assert.Equal(module.Glyph, AppTabs.Glyph(module.Tag));
    }

    // MARK: - Links

    [Theory]
    [InlineData("fleetmate://ticket/42", "Tickets")]
    [InlineData("fleetmate://workitem/7", "Projects")]
    [InlineData("fleetmate://asset/3", "Inventory")]
    public void LinksOpenInTheTabThatShowsThem(string link, string tab)
    {
        var parsed = FleetMateLink.TryParse(link, out var error);
        Assert.True(parsed != null, error?.Message);
        Assert.Equal(tab, LinkRoutes.TabFor(parsed!));
    }

    [Fact]
    public void RecordLinksRouteByType()
    {
        Assert.Equal("Devices", LinkRoutes.TabFor(new FleetMateLink.Device("synthetic-id")));
        Assert.Equal("Identity", LinkRoutes.TabFor(new FleetMateLink.User("u")));
        Assert.Equal("Identity", LinkRoutes.TabFor(new FleetMateLink.Group("g")));
        Assert.Equal("Development", LinkRoutes.TabFor(new FleetMateLink.AzureDevOpsRun("Proj", 1)));
    }

    // MARK: - TeamDynamix session

    private TdxSessionCoordinator Coordinator(TdxConfig config, TdxService? service, AuthManager auth,
        TdxSessionCoordinator.SignInChain chain) =>
        new(config, service, auth, chain,
            store: new TdxSessionCache(_dir),
            peers: Array.Empty<TdxSessionCache>(),
            renewalLock: new TdxRenewalLock(Path.Combine(_dir, "renewal.lock")));

    private static FleetMateConfig Config(string? baseUrl) => new() { Tdx = new TdxConfig { BaseUrl = baseUrl } };

    [Fact]
    public async Task WithoutTeamDynamixNothingIsAttemptedOrShown()
    {
        var config = Config(null);
        var calls = 0;
        using var session = Coordinator(config.Tdx!, null, new AuthManager(config), (_, _, _) =>
        {
            calls++;
            return Task.FromResult(TdxSsoResult.Failed("unused"));
        });

        await session.AttemptSilentAsync();

        Assert.False(session.IsEnabled);
        Assert.False(session.ShowsSignIn);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AFailedSilentSignInShowsSignInWithTheReason()
    {
        var config = Config("https://service.example.invalid");
        using var tdx = new TdxService(config.Tdx!);
        using var session = Coordinator(config.Tdx!, tdx, new AuthManager(config),
            (_, _, _) => Task.FromResult(TdxSsoResult.Failed("synthetic failure")));

        Assert.False(session.ShowsSignIn);
        await session.AttemptSilentAsync();

        Assert.True(session.ShowsSignIn);
        Assert.False(session.IsSignedIn);
        Assert.Contains("synthetic failure", session.SignInMessage);
    }

    [Fact]
    public async Task ASuccessfulSignInIsHeldAndReported()
    {
        var config = Config("https://service.example.invalid");
        using var tdx = new TdxService(config.Tdx!);
        var expiry = DateTime.UtcNow.AddHours(8);
        var auth = new AuthManager(config);
        using var session = Coordinator(config.Tdx!, tdx, auth,
            (_, _, _) => Task.FromResult(new TdxSsoResult
            {
                Success = true,
                Token = "synthetic-token-value",
                Expiry = expiry,
                UserEmail = "pat@example.invalid",
                UserName = "Pat Example",
            }));

        await session.AttemptSilentAsync();

        Assert.True(session.IsSignedIn);
        Assert.False(session.ShowsSignIn);
        Assert.Equal("synthetic-token-value", tdx.CurrentSsoToken);
        Assert.Equal(AuthStateKind.Valid, auth.Systems[AuthSystemId.Tdx].State.Kind);
    }

    [Fact]
    public async Task ConcurrentAttemptsShareOneSignIn()
    {
        var config = Config("https://service.example.invalid");
        using var tdx = new TdxService(config.Tdx!);
        var calls = 0;
        var gate = new TaskCompletionSource<TdxSsoResult>();
        using var session = Coordinator(config.Tdx!, tdx, new AuthManager(config), (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return gate.Task;
        });

        var first = session.AttemptSilentAsync();
        var second = session.AttemptSilentAsync();
        Assert.Same(first, second);
        gate.SetResult(TdxSsoResult.Failed("synthetic failure"));
        await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
    }
}
