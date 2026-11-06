using System.Text;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using FleetMate.GUI;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The saved TeamDynamix session: encrypted at rest, reused only when fresh
/// and only for the account and tenant it was verified for. Synthetic values only.
/// </summary>
public sealed class TdxSessionCacheTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
    private const string Address = "pat@example.invalid";
    private const string BaseUrl = "https://service.example.com";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fm-session-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static TdxSession Session(DateTime? expiry = null) => new()
    {
        Token = "synthetic-token-value",
        ExpiryUtc = expiry ?? Now.AddHours(8),
        UserEmail = Address,
        UserName = "Pat Example",
        VerifiedAddress = Address,
        BaseUrl = BaseUrl,
    };

    [Fact]
    public void RoundTripsEncryptedAndClears()
    {
        var cache = new TdxSessionCache(_dir);
        cache.Save(Session());

        var raw = File.ReadAllBytes(Path.Combine(_dir, "tdx-session.bin"));
        Assert.DoesNotContain("synthetic-token-value", Encoding.UTF8.GetString(raw));
        Assert.Equal("synthetic-token-value", cache.Load()!.Token);

        cache.Clear();
        Assert.Null(cache.Load());
    }

    [Fact]
    public void ALaterSaveNeverReplacesASessionThatLastsLonger()
    {
        var cache = new TdxSessionCache(_dir);
        Assert.True(cache.Save(Session(Now.AddHours(20))));

        var shorter = Session(Now.AddHours(4));
        shorter.Token = "a-shorter-token";
        Assert.False(cache.Save(shorter));
        Assert.Equal("synthetic-token-value", cache.Load()!.Token);

        var longer = Session(Now.AddHours(22));
        longer.Token = "a-longer-token";
        Assert.True(cache.Save(longer));
        Assert.Equal("a-longer-token", cache.Load()!.Token);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void ASaveForAnotherAccountOrTenantAlwaysReplaces()
    {
        var cache = new TdxSessionCache(_dir);
        cache.Save(Session(Now.AddHours(20)));

        var other = Session(Now.AddHours(1));
        other.Token = "another-accounts-token";
        other.VerifiedAddress = "other@example.invalid";
        Assert.True(cache.Save(other));

        var tenant = Session(Now.AddHours(1));
        tenant.Token = "another-tenants-token";
        tenant.BaseUrl = "https://other.example.com";
        Assert.True(cache.Save(tenant));
        Assert.Equal("another-tenants-token", cache.Load()!.Token);
    }

    [Fact]
    public void TheSameSignInCanAlwaysBeSavedAgain()
    {
        var cache = new TdxSessionCache(_dir);
        cache.Save(Session());
        var again = Session();
        again.Me = new TdxPerson { PrimaryEmail = Address };
        Assert.True(cache.Save(again));
        Assert.NotNull(cache.Load()!.Me);
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(1.5, true)]
    [InlineData(0.5, false)]
    [InlineData(-1, false)]
    public void IsFreshOnlyWithMoreThanAnHourLeft(double hoursLeft, bool fresh)
    {
        Assert.Equal(fresh, Session(Now.AddHours(hoursLeft)).IsFresh(Now));
    }

    [Theory]
    [InlineData(" PAT@example.invalid ", "https://SERVICE.example.com/", true)]
    [InlineData("other@example.invalid", BaseUrl, false)]
    [InlineData(null, BaseUrl, false)]
    [InlineData("not-an-address", BaseUrl, false)]
    [InlineData(Address, "https://other.example.com", false)]
    [InlineData(Address, null, false)]
    public void BelongsOnlyToTheVerifiedAccountAndTenant(string? expected, string? baseUrl, bool belongs)
    {
        Assert.Equal(belongs, Session().BelongsTo(expected, baseUrl));
    }

    [Fact]
    public void ASessionWithNoVerifiedAddressNeverMatches()
    {
        var session = Session();
        session.VerifiedAddress = null;
        Assert.False(session.BelongsTo(Address, BaseUrl));
    }

    [Fact]
    public void TheCachedPersonIsRestoredOnlyWhenTheirEmailIsTheVerifiedAddress()
    {
        var session = Session();
        session.Me = new TdxPerson { FullName = "Pat Example", PrimaryEmail = "Pat@Example.invalid" };
        Assert.NotNull(session.VerifiedMe);

        session.Me = new TdxPerson { FullName = "Someone Else", PrimaryEmail = "other@example.invalid" };
        Assert.Null(session.VerifiedMe);
    }

    [Fact]
    public void SaveMeIgnoresAnotherSignInOrAnotherPerson()
    {
        var cache = new TdxSessionCache(_dir);
        cache.Save(Session());

        cache.SaveMe("a-different-token", new TdxPerson { PrimaryEmail = Address });
        Assert.Null(cache.Load()!.Me);

        cache.SaveMe("synthetic-token-value", new TdxPerson { PrimaryEmail = "other@example.invalid" });
        Assert.Null(cache.Load()!.Me);

        cache.SaveMe("synthetic-token-value", new TdxPerson { PrimaryEmail = Address });
        Assert.Equal(Address, cache.Load()!.Me!.PrimaryEmail);
    }

    [Theory]
    [InlineData(new[] { "--inbox-agent-worker" }, true)]
    [InlineData(new[] { "--INBOX-AGENT-WORKER" }, true)]
    [InlineData(new[] { "--inbox-agent" }, false)]
    [InlineData(new[] { "ticketsmate://ticket/1" }, false)]
    [InlineData(new string[0], false)]
    public void TheHelperRunsOnlyWithItsArgument(string[] args, bool expected)
    {
        Assert.Equal(expected, InboxAgent.IsRequested(args));
    }

    [Fact]
    public void TheCommandLineReusesASessionUntilItsLastMinutes()
    {
        Assert.True(Session(Now.AddMinutes(30)).ReusableBy(Address, BaseUrl, Now));
        Assert.False(Session(Now.AddMinutes(4)).ReusableBy(Address, BaseUrl, Now));
        Assert.False(Session(Now.AddMinutes(-1)).ReusableBy(Address, BaseUrl, Now));
    }

    [Fact]
    public void TheCommandLineReusesOnlyThisAccountsAndTenantsSession()
    {
        var session = Session();
        Assert.True(session.ReusableBy("PAT@example.invalid ", BaseUrl + "/", Now));
        Assert.False(session.ReusableBy("someone-else@example.invalid", BaseUrl, Now));
        Assert.False(session.ReusableBy(Address, "https://other.example.com", Now));
        Assert.False(session.ReusableBy(null, BaseUrl, Now));
    }

    [Fact]
    public void AnEmptyTokenIsNeverReused()
    {
        var session = Session();
        session.Token = "";
        Assert.False(session.ReusableBy(Address, BaseUrl, Now));
    }
}
