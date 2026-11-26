using FleetMate.Core.Development;

namespace FleetMate.WinUI.Pages.Development;

/// <summary>
/// One Development sub-tab: Repos, Pulls, Commits, Pipelines or Skills. Each
/// is a self-contained control with its own list and detail pane, hosted by
/// <see cref="DevelopmentPage"/>, which owns the segment bar, the Inbox, the
/// Refresh button and the Graphs strip above.
///
/// A segment reads its data from <c>App.Services</c> (the Development caches,
/// <c>CacheChanged</c>, the inbox, approvals and <c>DevelopmentSync</c>). While it
/// is shown it tells the refresh worker what it shows with
/// <c>App.Services.DevelopmentSync.AttachView(this, Segment, ...)</c>, and the
/// page detaches it when it is hidden.
/// </summary>
public interface IDevelopmentSegmentView
{
    DevelopmentSegment Segment { get; }

    /// <summary>Called once, before the segment is first shown.</summary>
    void Attach(DevelopmentPage page);

    /// <summary>The segment became the open one (true) or another did (false).</summary>
    void SetShown(bool shown);

    /// <summary>The toolbar search text: this segment's filter while it is open.</summary>
    void ApplySearch(string text);

    /// <summary>The Refresh button and F5: what Refresh means for this segment.</summary>
    Task RefreshAsync();

    /// <summary>True while the segment is loading with nothing to show yet; the page shows its ring.</summary>
    bool IsBusy { get; }

    /// <summary>Raised when <see cref="IsBusy"/> changes.</summary>
    event EventHandler? BusyChanged;
}
