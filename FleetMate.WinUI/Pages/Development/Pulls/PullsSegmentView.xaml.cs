using FleetMate.Core.Development;
using FleetMate.Core.Links;
using FleetMate.Core.Models.Projects;
using Microsoft.UI.Xaml.Controls;

namespace FleetMate.WinUI.Pages.Development.Pulls;

/// <summary>Development › Pulls: every open pull request across Azure DevOps and GitHub, and the selected one's detail.</summary>
public sealed partial class PullsSegmentView : UserControl, IDevelopmentSegmentView
{
    public PullsSegmentView() => InitializeComponent();

    public DevelopmentSegment Segment => DevelopmentSegment.Pulls;
    public bool IsBusy => false;
    public event EventHandler? BusyChanged { add { } remove { } }

    public void Attach(DevelopmentPage page) { }
    public void SetShown(bool shown) { }
    public void ApplySearch(string text) { }
    public Task RefreshAsync() => Task.CompletedTask;

    /// <summary>A <c>fleetmate:</c> pull request link: clear the filters and select it, fetching it when it isn't loaded.</summary>
    public Task OpenLinkAsync(FleetMateLink link) => Task.CompletedTask;

    /// <summary>Pulls filtered to one repository — a repository bar's click.</summary>
    public void ShowRepository(string repository) { }

    /// <summary>Pulls filtered to one source, any repository — the pull request figures' click.</summary>
    public void ShowSource(DevelopmentSourceFilter source) { }

    /// <summary>Open one pull request, filters cleared so its row is in the list.</summary>
    public void ShowPullRequest(UnifiedPullRequest pr) { }
}
