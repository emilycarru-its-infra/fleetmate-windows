using FleetMate.Core.Development;
using FleetMate.Core.Links;
using Microsoft.UI.Xaml.Controls;

namespace FleetMate.WinUI.Pages.Development.Pipelines;

/// <summary>
/// Development › Pipelines: recent runs, the selected run's summary, jobs and
/// log, and approvals waiting on a run. A placeholder until the Pipelines
/// sub-tab is ported.
/// </summary>
public sealed partial class PipelinesSegmentView : UserControl, IDevelopmentSegmentView
{
    public PipelinesSegmentView() => InitializeComponent();

    public DevelopmentSegment Segment => DevelopmentSegment.Pipelines;
    public bool IsBusy => false;
    public event EventHandler? BusyChanged { add { } remove { } }

    public void Attach(DevelopmentPage page) { }
    public void SetShown(bool shown) { }
    public void ApplySearch(string text) { }
    public Task RefreshAsync() => Task.CompletedTask;

    /// <summary>A <c>fleetmate:</c> run or pipeline link: clear the filters and open the run.</summary>
    public Task OpenLinkAsync(FleetMateLink link) => Task.CompletedTask;

    /// <summary>Show Pipelines filtered to one repository — the Pipelines graphs' repository bar.</summary>
    public void ShowRepository(string repository) { }

    /// <summary>Show Pipelines filtered to one status — the Failing and Running figures' click.</summary>
    public void ShowStatus(PipelineStatusFilter status) { }
}
