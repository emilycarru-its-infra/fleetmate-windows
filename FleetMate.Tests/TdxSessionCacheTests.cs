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
    [InlineData(new[] { "--inbox-agent" }, true)]
    [InlineData(new[] { "--INBOX-AGENT" }, true)]
    [InlineData(new[] { "ticketsmate://ticket/1" }, false)]
    [InlineData(new string[0], false)]
    public void TheHelperRunsOnlyWithItsArgument(string[] args, bool expected)
    {
        Assert.Equal(expected, InboxAgent.IsRequested(args));
    }
}
