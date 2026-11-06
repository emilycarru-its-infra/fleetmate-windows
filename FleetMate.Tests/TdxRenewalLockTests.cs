using FleetMate.Core.Services.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The renewal lock shared by the window, the inbox helper and the other
/// edition: one process renews, and the one that loses the race waits and
/// takes the winner's session instead of signing in itself. Each
/// <see cref="TdxRenewalLock"/> here stands for a separate process on the same
/// lock file, which Windows treats the same way.
/// </summary>
public sealed class TdxRenewalLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fm-renewal-lock-" + Guid.NewGuid().ToString("N"));
    private string LockPath => Path.Combine(_dir, "tdx-renewal.lock");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private TdxRenewalLock Peer(Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(LockPath, delay ?? ((_, _) => Task.CompletedTask));

    [Fact]
    public void OnlyOneProcessHoldsTheLock()
    {
        using var held = Peer().TryAcquire();
        Assert.NotNull(held);
        Assert.Null(Peer().TryAcquire());
    }

    [Fact]
    public void ClosingTheHolderReleasesTheLock()
    {
        // A crash closes the handle the same way Dispose does.
        var held = Peer().TryAcquire();
        Assert.NotNull(held);
        held!.Dispose();

        using var next = Peer().TryAcquire();
        Assert.NotNull(next);
    }

    [Fact]
    public async Task WithTheLockFreeTheProcessRenewsHoldingIt()
    {
        var adopted = false;
        var turn = await Peer().RenewOrAdoptAsync(
            renew: () =>
            {
                Assert.Null(Peer().TryAcquire());
                return Task.FromResult(true);
            },
            adopt: () => Task.FromResult(adopted = true),
            wait: TimeSpan.FromSeconds(5));

        Assert.Equal(TdxRenewalTurn.Renewed, turn);
        Assert.False(adopted);
        using var after = Peer().TryAcquire();
        Assert.NotNull(after);
    }

    [Fact]
    public async Task AFailedRenewalIsReportedAndReleasesTheLock()
    {
        var turn = await Peer().RenewOrAdoptAsync(() => Task.FromResult(false), () => Task.FromResult(true),
            TimeSpan.FromSeconds(5));

        Assert.Equal(TdxRenewalTurn.RenewFailed, turn);
        using var after = Peer().TryAcquire();
        Assert.NotNull(after);
    }

    [Fact]
    public async Task TheLockIsReleasedWhenRenewalThrows()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Peer().RenewOrAdoptAsync(
            () => throw new InvalidOperationException("boom"), () => Task.FromResult(false), TimeSpan.FromSeconds(5)));

        using var after = Peer().TryAcquire();
        Assert.NotNull(after);
    }

    [Fact]
    public async Task TheLoserWaitsForTheWinnerAndAdoptsInsteadOfSigningIn()
    {
        var winner = Peer().TryAcquire();
        var polls = 0;
        var loser = Peer((_, _) =>
        {
            // The winner finishes its sign-in on the third poll.
            if (++polls == 3) winner!.Dispose();
            return Task.CompletedTask;
        });

        var renewed = false;
        var adoptedWithLock = false;
        var turn = await loser.RenewOrAdoptAsync(
            renew: () => Task.FromResult(renewed = true),
            adopt: () =>
            {
                adoptedWithLock = Peer().TryAcquire() == null;
                return Task.FromResult(true);
            },
            wait: TimeSpan.FromSeconds(10));

        Assert.Equal(TdxRenewalTurn.Adopted, turn);
        Assert.False(renewed);
        Assert.True(adoptedWithLock);
        Assert.Equal(3, polls);
    }

    [Fact]
    public async Task WhenTheWinnerLeftNothingTheLoserStillDoesNotSignIn()
    {
        var winner = Peer().TryAcquire();
        var loser = Peer((_, _) =>
        {
            winner!.Dispose();
            return Task.CompletedTask;
        });

        var renewed = false;
        var turn = await loser.RenewOrAdoptAsync(() => Task.FromResult(renewed = true), () => Task.FromResult(false),
            TimeSpan.FromSeconds(10));

        Assert.Equal(TdxRenewalTurn.OtherFailed, turn);
        Assert.False(renewed);
    }

    [Fact]
    public async Task AWinnerThatNeverFinishesIsWaitedOnOnlyAsLongAsAllowed()
    {
        using var winner = Peer().TryAcquire();
        var polls = 0;
        var loser = Peer((_, _) =>
        {
            polls++;
            return Task.CompletedTask;
        });

        var adopts = 0;
        var turn = await loser.RenewOrAdoptAsync(() => Task.FromResult(true), () =>
        {
            adopts++;
            return Task.FromResult(true);
        }, wait: TimeSpan.FromSeconds(2));

        Assert.Equal(TdxRenewalTurn.Adopted, turn);
        Assert.Equal((int)(TimeSpan.FromSeconds(2) / TdxRenewalLock.PollInterval), polls);
        Assert.Equal(1, adopts);
    }

    [Fact]
    public void BothEditionsShareOneLock()
    {
        Assert.Equal(
            Path.Combine(FleetMate.Core.Config.AppEdition.FleetMate.LocalAppDataDirectory, "tdx-renewal.lock"),
            TdxRenewalLock.DefaultPath);
    }
}
