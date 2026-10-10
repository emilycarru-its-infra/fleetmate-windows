using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using FleetMate.GUI.Views.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// At launch the Tickets page loaded before silent SSO finished. It got an
/// empty board, and nothing reloaded it when SSO succeeded seconds later.
/// </summary>
public class TicketBoardSignInTests
{
    private static List<TdxTicket> Board(int count) =>
        Enumerable.Range(1, count).Select(i => new TdxTicket { Id = i }).ToList();

    [Fact]
    public async Task ALoadWaitsForTheSignInInFlight()
    {
        var signIn = new TaskCompletionSource();
        var signedIn = false;
        var fetchedSignedIn = (bool?)null;
        var loader = new TicketBoardLoader(
            () => signIn.Task,
            _ => { fetchedSignedIn = signedIn; return Task.FromResult(Board(signedIn ? 3 : 0)); });

        var load = loader.LoadAsync(TicketDateRangePreset.CurrentTerm);
        await Task.Delay(50);
        Assert.False(load.IsCompleted);
        Assert.Null(fetchedSignedIn);

        signedIn = true;
        signIn.SetResult();

        Assert.Equal(3, (await load).Count);
        Assert.True(fetchedSignedIn);
    }

    [Fact]
    public async Task AFailedSignInStillEndsTheWait()
    {
        var loader = new TicketBoardLoader(
            () => Task.FromException(new InvalidOperationException("no SSO")),
            _ => Task.FromResult(Board(0)));

        Assert.Empty(await loader.LoadAsync(TicketDateRangePreset.CurrentTerm));
    }

    [Fact]
    public async Task ThePageAndThePreloadShareOneFetch()
    {
        var signIn = new TaskCompletionSource();
        var fetches = 0;
        var loader = new TicketBoardLoader(
            () => signIn.Task,
            _ => { Interlocked.Increment(ref fetches); return Task.FromResult(Board(5)); });

        var page = loader.LoadAsync(TicketDateRangePreset.CurrentTerm);
        var preload = loader.LoadAsync(TicketDateRangePreset.CurrentTerm);
        signIn.SetResult();
        await Task.WhenAll(page, preload);

        Assert.Same(page, preload);
        Assert.Equal(1, fetches);
    }

    [Fact]
    public async Task ADifferentRangeOrALaterLoadFetchesAgain()
    {
        var fetches = 0;
        var loader = new TicketBoardLoader(
            () => Task.CompletedTask,
            _ => { Interlocked.Increment(ref fetches); return Task.FromResult(Board(1)); });

        await loader.LoadAsync(TicketDateRangePreset.CurrentTerm);
        await loader.LoadAsync(TicketDateRangePreset.CurrentTerm);
        var values = Enum.GetValues<TicketDateRangePreset>();
        await loader.LoadAsync(values.First(v => v != TicketDateRangePreset.CurrentTerm));

        Assert.Equal(3, fetches);
    }

    [Theory]
    // Signed in after the page drew an empty board: reload.
    [InlineData(true, false, false, 0, true)]
    [InlineData(true, false, true, 0, true)]
    // A load is already running and waits for the sign-in: leave it.
    [InlineData(true, true, false, 0, false)]
    // The cache already holds a fresh board: just redraw.
    [InlineData(true, false, true, 1215, false)]
    // The page has not loaded yet; its Loaded handler loads.
    [InlineData(false, false, false, 0, false)]
    public void ReloadAfterSignIn(bool initialLoadDone, bool loading, bool cacheValid, int cached, bool expected)
    {
        Assert.Equal(expected, TicketsPageSync.ShouldReloadAfterSignIn(initialLoadDone, loading, cacheValid, cached));
    }

    [Theory]
    [InlineData("Tickets", true, false, true)]
    [InlineData("Tickets", true, true, false)]
    [InlineData("Tickets", false, false, false)]
    [InlineData("Assets", true, false, false)]
    public void RedrawOnCacheChange(string key, bool initialLoadDone, bool loading, bool expected)
    {
        Assert.Equal(expected, TicketsPageSync.ShouldRedrawOnCacheChange(key, initialLoadDone, loading));
    }
}
