namespace FleetMate.GUI.Views.Tickets;

/// <summary>
/// When the Tickets page redraws or reloads in response to app events. Kept
/// apart from the page so the rules are testable without a window.
/// </summary>
internal static class TicketsPageSync
{
    /// <summary>
    /// After a sign-in succeeds, reload unless a load is already running (it
    /// waits for the sign-in and will finish with the new credential) or the
    /// cache already holds a fresh board. Before the page's first load, the
    /// Loaded handler does it.
    /// </summary>
    public static bool ShouldReloadAfterSignIn(bool initialLoadDone, bool loading, bool cacheValid, int cachedCount) =>
        initialLoadDone && !loading && (!cacheValid || cachedCount == 0);

    /// <summary>
    /// Redraw when the shared ticket cache changes, except mid-load, when the
    /// load redraws itself once it is done.
    /// </summary>
    public static bool ShouldRedrawOnCacheChange(string key, bool initialLoadDone, bool loading) =>
        key == "Tickets" && initialLoadDone && !loading;
}
