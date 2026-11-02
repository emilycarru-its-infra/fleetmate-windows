namespace FleetMate.GUI.Views.Tickets;

/// <summary>Why the ticket list shows nothing, so it can say so instead of a bare empty list.</summary>
internal enum TicketsEmptyReason
{
    /// <summary>Something is listed, or the loading progress covers the list.</summary>
    None,
    /// <summary>Nothing loaded and TeamDynamix is not signed in.</summary>
    NotSignedIn,
    /// <summary>Signed in, but the last load failed.</summary>
    LoadFailed,
    /// <summary>Signed in and loaded, and the range holds no tickets.</summary>
    NoTickets,
    /// <summary>Tickets are loaded; the search matches none of them.</summary>
    Search,
    /// <summary>Tickets are loaded; none are assigned to the signed-in person.</summary>
    AssignedToMe,
    /// <summary>Tickets are loaded; the filters, date range or hidden closed tickets leave none.</summary>
    Filters,
}

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

    /// <summary>
    /// The progress over the list shows only while it is empty: during the
    /// launch sign-in, or a load. With a snapshot showing, nothing.
    /// </summary>
    public static bool ShowLoading(bool loading, FleetMate.Core.Models.Tickets.TdxSignInStep? step, int ticketCount) =>
        ticketCount == 0 && (loading || step != null);

    /// <summary>The Activity bar's button: "(Draft)" when closed over unsent text.</summary>
    public static string AddCommentLabel(bool composeOpen, string? draft) =>
        !composeOpen && !string.IsNullOrWhiteSpace(draft) ? "Add Comment (Draft)" : "Add Comment";

    /// <summary>
    /// Why the list is empty. A link opened from a cold launch lands before
    /// sign-in finishes, and the list used to read as an empty search result;
    /// this tells not signed in, a failed load and the filters apart.
    /// </summary>
    public static TicketsEmptyReason EmptyReason(
        bool loadingShown, int ticketCount, int listedCount,
        bool ssoEnabled, bool signedIn, string? loadError,
        bool searching, bool assignedToMe)
    {
        if (loadingShown || listedCount > 0) return TicketsEmptyReason.None;
        if (ticketCount == 0)
        {
            if (ssoEnabled && !signedIn) return TicketsEmptyReason.NotSignedIn;
            return loadError != null ? TicketsEmptyReason.LoadFailed : TicketsEmptyReason.NoTickets;
        }
        if (searching) return TicketsEmptyReason.Search;
        return assignedToMe ? TicketsEmptyReason.AssignedToMe : TicketsEmptyReason.Filters;
    }

    /// <summary>
    /// A linked ticket that could not be fetched waits for sign-in when
    /// TeamDynamix is not signed in yet; once it is, the failure is real.
    /// </summary>
    public static bool HoldLinkForSignIn(bool ssoEnabled, bool signedIn) => ssoEnabled && !signedIn;
}
