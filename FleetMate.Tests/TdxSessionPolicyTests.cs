using FleetMate.Core.Models;
using FleetMate.Core.Services.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// When the window and the inbox helper renew their TeamDynamix sign-in, how
/// they back off, which saved sessions they adopt, and what the window shows.
/// Nothing here opens the sign-in window; only a Sign In click does. The clock is a field the tests move. Synthetic values only.
/// </summary>
public class TdxSessionPolicyTests
{
    private static readonly DateTime Start = new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
    private const string Address = "pat@example.invalid";
    private const string BaseUrl = "https://service.example.com";

    private DateTime _now = Start;
    private TdxSessionPolicy Policy() => new(() => _now);

    private void Advance(TimeSpan by) => _now += by;

    private static TdxSession Saved(DateTime expiry, string token = "saved-token", string address = Address,
        string baseUrl = BaseUrl) => new()
    {
        Token = token,
        ExpiryUtc = expiry,
        UserEmail = address,
        VerifiedAddress = address,
        BaseUrl = baseUrl,
    };

    // MARK: - Renewal timing

    [Fact]
    public void AHealthyTokenIsRenewedTwoHoursBeforeItExpires()
    {
        var policy = Policy();
        var expiry = _now.AddHours(23);

        Assert.Equal(expiry.AddHours(-2), policy.NextAttemptUtc(expiry));
        Assert.False(policy.ShouldAttempt(expiry, kicked: false));

        _now = expiry.AddHours(-2).AddSeconds(-1);
        Assert.False(policy.ShouldAttempt(expiry, kicked: false));
        _now = expiry.AddHours(-2);
        Assert.True(policy.ShouldAttempt(expiry, kicked: false));
    }

    [Fact]
    public void AMissingOrNearlyExpiredTokenIsRenewedNow()
    {
        var policy = Policy();

        Assert.True(policy.ShouldAttempt(null, kicked: false));
        Assert.True(policy.ShouldAttempt(_now.AddMinutes(4), kicked: false));
        Assert.True(policy.ShouldAttempt(_now.AddMinutes(-1), kicked: false));
        Assert.False(policy.IsUsable(_now.AddMinutes(5)));
        Assert.True(policy.IsUsable(_now.AddMinutes(6)));
    }

    [Fact]
    public void AShortLivedTokenIsNotRenewedInALoop()
    {
        var policy = Policy();
        policy.RecordSuccess();
        var expiry = _now.AddMinutes(90);

        // Inside its renewal window at once, but the last success was just now.
        Assert.Equal(_now.AddMinutes(15), policy.NextAttemptUtc(expiry));
        Assert.False(policy.ShouldAttempt(expiry, kicked: false));
        Advance(TimeSpan.FromMinutes(15));
        Assert.True(policy.ShouldAttempt(expiry, kicked: false));
    }

    // MARK: - Backoff

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 15)]
    [InlineData(40, 15)]
    public void RetriesBackOffOneFiveFifteenThenEveryFifteenMinutes(int failures, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), TdxSessionPolicy.RetryDelay(failures));
    }

    [Fact]
    public void AFailureWaitsForTheBackoffEvenWithNoToken()
    {
        var policy = Policy();
        policy.RecordFailure();

        Assert.Equal(1, policy.Failures);
        Assert.False(policy.ShouldAttempt(null, kicked: false));
        Advance(TimeSpan.FromSeconds(59));
        Assert.False(policy.ShouldAttempt(null, kicked: false));
        Advance(TimeSpan.FromSeconds(1));
        Assert.True(policy.ShouldAttempt(null, kicked: false));

        policy.RecordFailure();
        Assert.Equal(_now.AddMinutes(5), policy.NextAttemptUtc(null));
        policy.RecordFailure();
        Assert.Equal(_now.AddMinutes(15), policy.NextAttemptUtc(null));
    }

    [Fact]
    public void SuccessResetsTheBackoff()
    {
        var policy = Policy();
        policy.RecordFailure();
        policy.RecordFailure();
        policy.RecordFailure();

        policy.RecordSuccess();

        Assert.Equal(0, policy.Failures);
        var expiry = _now.AddHours(23);
        Assert.Equal(expiry.AddHours(-2), policy.NextAttemptUtc(expiry));
    }

    [Fact]
    public void AnAdoptedSessionResetsTheBackoffWithoutCountingAsAnAttempt()
    {
        var policy = Policy();
        policy.RecordFailure();
        policy.RecordRejected();
        var lastAttempt = policy.LastAttemptUtc;

        policy.RecordAdopted();

        Assert.Equal(0, policy.Failures);
        Assert.True(policy.LastCheckPassed);
        Assert.Equal(lastAttempt, policy.LastAttemptUtc);
    }

    // MARK: - Wake, unlock, network and 401

    [Fact]
    public void AKickSkipsTheBackoffWhenTheTokenIsGone()
    {
        var policy = Policy();
        policy.RecordFailure();
        policy.RecordFailure();
        Advance(TimeSpan.FromMinutes(1));

        Assert.False(policy.ShouldAttempt(null, kicked: false));
        Assert.True(policy.ShouldAttempt(null, kicked: true));
    }

    [Fact]
    public void AKickRunsAtMostOncePerThirtySeconds()
    {
        var policy = Policy();
        policy.RecordFailure();

        Advance(TimeSpan.FromSeconds(29));
        Assert.False(policy.ShouldAttempt(null, kicked: true));
        Advance(TimeSpan.FromSeconds(1));
        Assert.True(policy.ShouldAttempt(null, kicked: true));
    }

    [Fact]
    public void AKickDoesNotRenewAHealthyTokenEarly()
    {
        var policy = Policy();
        Assert.False(policy.ShouldAttempt(_now.AddHours(10), kicked: true));
        Assert.True(policy.ShouldAttempt(_now.AddMinutes(90), kicked: true));
    }

    // MARK: - Adopting a saved session

    [Fact]
    public void ASavedSessionThatOutlastsTheHeldOneIsAdopted()
    {
        var policy = Policy();
        var saved = Saved(_now.AddHours(20));

        Assert.True(policy.ShouldAdopt(saved, Address, BaseUrl, tokenVerified: true, "held-token", _now.AddHours(3)));
        Assert.True(policy.ShouldAdopt(saved, Address, BaseUrl, tokenVerified: true, null, null));
    }

    [Fact]
    public void ASavedSessionThatDoesNotOutlastTheHeldOneIsLeft()
    {
        var policy = Policy();
        var held = _now.AddHours(20);

        Assert.False(policy.ShouldAdopt(Saved(_now.AddHours(19)), Address, BaseUrl, true, "held-token", held));
        Assert.False(policy.ShouldAdopt(Saved(held), Address, BaseUrl, true, "held-token", held));
    }

    [Fact]
    public void AnOlderSavedSessionStillReplacesAnExpiredHeldOne()
    {
        var policy = Policy();
        Assert.True(policy.ShouldAdopt(Saved(_now.AddHours(1)), Address, BaseUrl, true, "held-token", _now.AddHours(2).AddMinutes(-119)));
    }

    [Fact]
    public void TheSameTokenIsNeverAdoptedAgain()
    {
        var policy = Policy();
        Assert.False(policy.ShouldAdopt(Saved(_now.AddHours(20), token: "same"), Address, BaseUrl, true, "same", null));
    }

    [Theory]
    [InlineData("other@example.invalid", BaseUrl, true)]
    [InlineData(Address, "https://other.example.com", true)]
    [InlineData(Address, BaseUrl, false)]
    public void ASavedSessionForAnotherAccountOrTenantOrUnverifiedTokenIsRefused(string savedAddress, string savedBaseUrl, bool verified)
    {
        var policy = Policy();
        var saved = Saved(_now.AddHours(20), address: savedAddress, baseUrl: savedBaseUrl);

        Assert.False(policy.ShouldAdopt(saved, Address, BaseUrl, verified, null, null));
    }

    [Fact]
    public void ASavedSessionIsNeverAdoptedWithoutAnExpectedAddress()
    {
        var policy = Policy();
        Assert.False(policy.ShouldAdopt(Saved(_now.AddHours(20)), null, BaseUrl, true, null, null));
    }

    [Fact]
    public void ASavedSessionWithFiveMinutesOrLessLeftIsRefused()
    {
        var policy = Policy();
        Assert.False(policy.ShouldAdopt(Saved(_now.AddMinutes(5)), Address, BaseUrl, true, null, null));
        Assert.True(policy.ShouldAdopt(Saved(_now.AddMinutes(6)), Address, BaseUrl, true, null, null));
    }

    [Fact]
    public void ALateSignInNeverReplacesANewerSession()
    {
        var policy = Policy();
        var held = _now.AddHours(20);

        Assert.False(policy.ShouldAccept(_now.AddHours(19), held));
        Assert.True(policy.ShouldAccept(_now.AddHours(21), held));
        Assert.True(policy.ShouldAccept(_now.AddHours(1), _now.AddMinutes(2)));
        Assert.True(policy.ShouldAccept(_now.AddHours(1), null));
    }

    // MARK: - Banner and status

    [Fact]
    public void AttentionIsAskedOnlyAfterRepeatedFailuresWithNoUsableToken()
    {
        var policy = Policy();
        Assert.False(policy.NeedsAttention(null, hasToken: false, identityRefused: false));

        policy.RecordFailure();
        Assert.False(policy.NeedsAttention(null, false, false));
        policy.RecordFailure();
        Assert.True(policy.NeedsAttention(null, false, false));
        Assert.True(policy.NeedsAttention(_now.AddMinutes(2), true, false));

        // Still signed in: renewal failing is not yet the person's problem.
        Assert.False(policy.NeedsAttention(_now.AddHours(1), true, false));

        policy.RecordSuccess();
        Assert.False(policy.NeedsAttention(null, false, false));
    }

    [Fact]
    public void ARefusedTokenNeedsAttentionOnceRenewalKeepsFailing()
    {
        var policy = Policy();
        policy.RecordRejected();
        policy.RecordFailure();
        policy.RecordFailure();

        Assert.True(policy.NeedsAttention(_now.AddHours(5), hasToken: true, identityRefused: false));
    }

    [Fact]
    public void TheWrongAccountAlwaysNeedsAttention()
    {
        Assert.True(Policy().NeedsAttention(null, false, identityRefused: true));
    }

    [Fact]
    public void ValidStandsOnlyWithAnUnexpiredTokenThatPassedItsLastCheck()
    {
        var policy = Policy();
        var valid = AuthTokenState.Valid("Pat Example", _now.AddHours(5));

        Assert.Equal(AuthStateKind.Valid, policy.EffectiveState(valid, hasToken: true, _now.AddHours(5)).Kind);

        var expired = policy.EffectiveState(valid, hasToken: true, _now.AddSeconds(-1));
        Assert.Equal(AuthStateKind.Expired, expired.Kind);
        Assert.Equal("Pat Example", expired.User);
        Assert.Equal(AuthStateKind.Expired, policy.EffectiveState(valid, hasToken: false, _now.AddHours(5)).Kind);

        policy.RecordRejected();
        Assert.Equal(AuthStateKind.Expired, policy.EffectiveState(valid, true, _now.AddHours(5)).Kind);
        policy.RecordSuccess();
        Assert.Equal(AuthStateKind.Valid, policy.EffectiveState(valid, true, _now.AddHours(5)).Kind);
    }

    [Fact]
    public void OtherStatesPassThrough()
    {
        var policy = Policy();
        var failed = AuthTokenState.Failed("no");
        Assert.Same(failed, policy.EffectiveState(failed, false, null));
        var signingIn = AuthTokenState.Authenticating();
        Assert.Same(signingIn, policy.EffectiveState(signingIn, false, null));
    }
}
