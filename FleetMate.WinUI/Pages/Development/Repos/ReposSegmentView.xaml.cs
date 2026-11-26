using FleetMate.Core.Development;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FleetMate.WinUI.Pages.Development.Repos;

/// <summary>
/// Development › Repos: tracked checkouts in the sidebar, the selected one's
/// git pane, files and editor beside it, and its Insights in the Graphs strip.
/// A placeholder until the Repos sub-tab is ported.
/// </summary>
public sealed partial class ReposSegmentView : UserControl, IDevelopmentSegmentView
{
    public ReposSegmentView() => InitializeComponent();

    public DevelopmentSegment Segment => DevelopmentSegment.Repos;
    public bool IsBusy => false;
    public event EventHandler? BusyChanged { add { } remove { } }

    public void Attach(DevelopmentPage page) { }
    public void SetShown(bool shown) { }
    public void ApplySearch(string text) { }
    public Task RefreshAsync() => Task.CompletedTask;

    /// <summary>The selected repository's Insights, for the Graphs strip; null when there is none.</summary>
    public UIElement? Insights => null;
}
