using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Development;

/// <summary>
/// The open segment's filter, driven by the toolbar search field. The Inbox has
/// none, so on it the field searches everything.
/// </summary>
public partial class DevelopmentView
{
    public TabSearchScope? SearchScope =>
        PullRequestsSegment.IsChecked == true ? new(SearchBox, "Search pull requests")
        : CommitsSegment.IsChecked == true ? new(CommitsSearchBox, "Search commits")
        : PipelinesSegment.IsChecked == true ? new(PipelinesSearchBox, "Search pipeline runs")
        : SkillsSegment.IsChecked == true ? new(SkillsSearchBox, "Filter skills")
        : null;

    public event EventHandler? SearchScopeChanged;
}
