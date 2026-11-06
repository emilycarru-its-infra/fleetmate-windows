using FleetMate.Core.Models;

namespace FleetMate.Core.Services.Tickets;

/// <summary>
/// Decides how the window keeps its TeamDynamix sign-in alive: when to renew,
/// how to back off when silent sign-in keeps failing, whether a session another
/// process saved is worth adopting, whether the sign-in window may open by
/// itself, when Tickets asks for attention, and what the Authentication panel
/// may claim. The GUI wires it to timers and system events. Everything here
/// reads the injected clock, so it can be tested without waiting.
///
/// The numbers follow FleetMate for Mac: renew two hours before expiry; after a
/// failure, retry after 1, 5 and 15 minutes, then every 15.
/// </summary>
public sealed class TdxSessionPolicy
{
    /// <summary>Renew this long before the token expires.</summary>
    public static readonly TimeSpan RenewLead = TimeSpan.FromHours(2);

    /// <summary>Never renew a healthy token more often than this, however short-lived it is.</summary>
    public static readonly TimeSpan MinRenewInterval = TimeSpan.FromMinutes(15);

    /// <summary>Waits after the 1st, 2nd, 3rd… consecutive failure; the last repeats.</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays = new[]
    {
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
    };

    /// <summary>A token with less than this left is treated as already gone.</summary>
    public static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);

    /// <summary>Consecutive failures, with no usable token left, before Tickets asks for attention.</summary>
    public const int FailuresBeforeAttention = 2;

    /// <summary>The window may open by itself only when the token has less than this left.</summary>
    public static readonly TimeSpan WindowNearExpiry = TimeSpan.FromMinutes(10);

    /// <summary>After the window was closed or timed out, it does not open by itself again for this long.</summary>
    public static readonly TimeSpan WindowCooldown = TimeSpan.FromMinutes(30);

    /// <summary>The window gives up on its own after this long.</summary>
    public static readonly TimeSpan WindowTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Wake, unlock and network events arriving together count as one.</summary>
    public static readonly TimeSpan KickDebounce = TimeSpan.FromSeconds(5);

    /// <summary>An event-driven or 401-driven attempt runs at most this often.</summary>
    public static readonly TimeSpan KickSpacing = TimeSpan.FromSeconds(30);

    private readonly Func<DateTime> _utcNow;

    public TdxSessionPolicy(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    public DateTime UtcNow => _utcNow();

    /// <summary>Consecutive failed attempts since the last success.</summary>
    public int Failures { get; private set; }

    /// <summary>When the last attempt finished, successful or not.</summary>
    public DateTime? LastAttemptUtc { get; private set; }

    /// <summary>
    /// Whether the token held passed its last check. A 401 clears it, and a
    /// sign-in, restore or adoption sets it again.
    /// </summary>
    public bool LastCheckPassed { get; private set; } = true;

    /// <summary>When the sign-in window was last closed or timed out without signing in.</summary>
    public DateTime? WindowDismissedUtc { get; private set; }

    // MARK: - Renewal

    /// <summary>True when <paramref name="expiryUtc"/> leaves enough time to keep using the token.</summary>
    public bool IsUsable(DateTime? expiryUtc) =>
        expiryUtc is { } expiry && expiry.ToUniversalTime() > UtcNow + ExpiryMargin;

    /// <summary>The wait after <paramref name="failures"/> consecutive failures: 1, 5, then 15 minutes, repeating.</summary>
    public static TimeSpan RetryDelay(int failures) =>
        failures <= 0 ? TimeSpan.Zero : RetryDelays[Math.Min(failures, RetryDelays.Count) - 1];

    /// <summary>
    /// When the next attempt is due. After a failure the backoff decides,
    /// whatever the token's state. Otherwise a missing or nearly expired token
    /// is renewed now, and a healthy one <see cref="RenewLead"/> before it
    /// expires, but no sooner than <see cref="MinRenewInterval"/> after the
    /// last attempt.
    /// </summary>
    public DateTime NextAttemptUtc(DateTime? expiryUtc)
    {
        var now = UtcNow;
        if (Failures > 0 && LastAttemptUtc is { } last)
            return Max(now, last + RetryDelay(Failures));
        if (!IsUsable(expiryUtc)) return now;

        var due = expiryUtc!.Value.ToUniversalTime() - RenewLead;
        if (LastAttemptUtc is { } previous) due = Max(due, previous + MinRenewInterval);
        return Max(now, due);
    }

    /// <summary>
    /// Whether to start a silent attempt now. <paramref name="kicked"/> is a
    /// wake, unlock, network change or 401: it skips the backoff when the token
    /// is gone or already inside its renewal window, but still runs at most
    /// once per <see cref="KickSpacing"/>.
    /// </summary>
    public bool ShouldAttempt(DateTime? expiryUtc, bool kicked)
    {
        var now = UtcNow;
        if (kicked)
        {
            if (LastAttemptUtc is { } last && now - last < KickSpacing) return false;
            if (!IsUsable(expiryUtc)) return true;
            if (expiryUtc!.Value.ToUniversalTime() - RenewLead <= now) return true;
        }
        return NextAttemptUtc(expiryUtc) <= now;
    }

    /// <summary>A silent attempt, a restore or an adoption produced a usable token.</summary>
    public void RecordSuccess()
    {
        Failures = 0;
        LastAttemptUtc = UtcNow;
        LastCheckPassed = true;
    }

    /// <summary>A silent attempt finished without a new token.</summary>
    public void RecordFailure()
    {
        Failures++;
        LastAttemptUtc = UtcNow;
    }

    /// <summary>A token arrived without an attempt of this process's own: another process saved it.</summary>
    public void RecordAdopted()
    {
        Failures = 0;
        LastCheckPassed = true;
    }

    /// <summary>TeamDynamix refused the token held.</summary>
    public void RecordRejected() => LastCheckPassed = false;

    // MARK: - Saved sessions

    /// <summary>
    /// Whether a session another process saved should replace the token held.
    /// It must pass the same checks a live sign-in makes, this account and this
    /// tenant (<see cref="TdxSession.ReusableBy"/>) with a token that carries
    /// that address (<paramref name="tokenVerified"/>, from
    /// <see cref="TdxSsoIdentity.Verify"/>), and it must outlast the token held,
    /// unless that one is no longer usable.
    /// </summary>
    public bool ShouldAdopt(TdxSession saved, string? expectedAddress, string? baseUrl, bool tokenVerified,
        string? heldToken, DateTime? heldExpiryUtc)
    {
        if (!tokenVerified || !saved.ReusableBy(expectedAddress, baseUrl, UtcNow)) return false;
        if (string.Equals(saved.Token, heldToken, StringComparison.Ordinal)) return false;
        if (!IsUsable(heldExpiryUtc) || string.IsNullOrEmpty(heldToken)) return true;
        return saved.ExpiryUtc.ToUniversalTime() > heldExpiryUtc!.Value.ToUniversalTime();
    }

    /// <summary>
    /// Whether a sign-in that just finished may replace the token held: it
    /// must outlast it, so a late result never overwrites a newer session.
    /// </summary>
    public bool ShouldAccept(DateTime resultExpiryUtc, DateTime? heldExpiryUtc) =>
        !IsUsable(heldExpiryUtc) || resultExpiryUtc.ToUniversalTime() > heldExpiryUtc!.Value.ToUniversalTime();

    // MARK: - Sign-in window

    /// <summary>
    /// Whether the sign-in window may open without a click. Only after the
    /// whole silent chain has just failed, with the token expired or under
    /// <see cref="WindowNearExpiry"/> from it, the identity not refused, no
    /// window already open, and not within <see cref="WindowCooldown"/> of the
    /// last one being closed or timing out.
    /// </summary>
    public bool ShouldAutoOpenWindow(DateTime? expiryUtc, bool silentChainFailed, bool identityRefused,
        bool windowOpen)
    {
        if (!silentChainFailed || identityRefused || windowOpen) return false;
        var now = UtcNow;
        if (expiryUtc is { } expiry && expiry.ToUniversalTime() - now >= WindowNearExpiry) return false;
        return WindowDismissedUtc is not { } dismissed || now - dismissed >= WindowCooldown;
    }

    /// <summary>The window closed. Only a close without a sign-in starts the cool-down.</summary>
    public void RecordWindowClosed(bool signedIn)
    {
        if (signedIn) RecordSuccess();
        else WindowDismissedUtc = UtcNow;
    }

    // MARK: - What to show

    /// <summary>
    /// Whether Tickets shows the sign-in banner: silent renewal has failed
    /// <see cref="FailuresBeforeAttention"/> times in a row with no usable
    /// token left, or the sign-in was refused for being the wrong account.
    /// </summary>
    public bool NeedsAttention(DateTime? expiryUtc, bool hasToken, bool identityRefused) =>
        identityRefused
        || ((!hasToken || !IsUsable(expiryUtc) || !LastCheckPassed) && Failures >= FailuresBeforeAttention);

    /// <summary>
    /// What the TeamDynamix row may claim. A Valid stands only while an
    /// unexpired token is held and its last check passed; otherwise the row
    /// says Expired, whatever the last probe found.
    /// </summary>
    public AuthTokenState EffectiveState(AuthTokenState reported, bool hasToken, DateTime? expiryUtc)
    {
        if (reported.Kind != AuthStateKind.Valid) return reported;
        var unexpired = hasToken && expiryUtc is { } expiry && expiry.ToUniversalTime() > UtcNow;
        return unexpired && LastCheckPassed ? reported : AuthTokenState.Expired(reported.User);
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
