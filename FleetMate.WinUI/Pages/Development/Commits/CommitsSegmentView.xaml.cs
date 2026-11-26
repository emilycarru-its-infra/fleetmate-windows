using FleetMate.Core.Development;
using FleetMate.Core.Links;
using Microsoft.UI.Xaml.Controls;

namespace FleetMate.WinUI.Pages.Development.Commits;

/// <summary>
/// Development › Commits: one card per repository with recent commits, and the
/// selected commit's detail. A placeholder until the Commits sub-tab is ported.
/// </summary>
public sealed partial class CommitsSegmentView : UserControl, IDevelopmentSegmentView
{
    public CommitsSegmentView() => InitializeComponent();

    public DevelopmentSegment Segment => DevelopmentSegment.Commits;
    public bool IsBusy => false;
    public event EventHandler? BusyChanged { add { } remove { } }

    public void Attach(DevelopmentPage page) { }
    public void SetShown(bool shown) { }
    public void ApplySearch(string text) { }
    public Task RefreshAsync() => Task.CompletedTask;

    /// <summary>A <c>fleetmate:</c> commit link: clear the filters and open the commit.</summary>
    public Task OpenLinkAsync(FleetMateLink link) => Task.CompletedTask;

    /// <summary>Show Commits filtered to one repository — the Commits graphs' repository bar.</summary>
    public void ShowRepository(string repository) { }
}
