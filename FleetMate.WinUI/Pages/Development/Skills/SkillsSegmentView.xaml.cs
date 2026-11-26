using FleetMate.Core.Development;
using Microsoft.UI.Xaml.Controls;

namespace FleetMate.WinUI.Pages.Development.Skills;

/// <summary>Development › Skills: the shared skills, hooks and standards every repository inherits, and this PC's own.</summary>
public sealed partial class SkillsSegmentView : UserControl, IDevelopmentSegmentView
{
    public SkillsSegmentView() => InitializeComponent();

    public DevelopmentSegment Segment => DevelopmentSegment.Skills;
    public bool IsBusy => false;
    public event EventHandler? BusyChanged { add { } remove { } }

    public void Attach(DevelopmentPage page) { }
    public void SetShown(bool shown) { }
    public void ApplySearch(string text) { }
    public Task RefreshAsync() => Task.CompletedTask;
}
