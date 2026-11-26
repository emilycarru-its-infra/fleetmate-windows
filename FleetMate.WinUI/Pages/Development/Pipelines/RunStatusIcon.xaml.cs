using FleetMate.Core.Development;
using FleetMate.Core.Models.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FleetMate.WinUI.Pages.Development.Pipelines;

/// <summary>A run's or log section's status glyph, with the approval badge while an approval holds the run.</summary>
public sealed partial class RunStatusIcon : UserControl
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(PipelineRunStatus), typeof(RunStatusIcon),
        new PropertyMetadata(PipelineRunStatus.Queued, (d, _) => ((RunStatusIcon)d).Render()));

    public static readonly DependencyProperty AwaitingApprovalProperty = DependencyProperty.Register(
        nameof(AwaitingApproval), typeof(bool), typeof(RunStatusIcon),
        new PropertyMetadata(false, (d, _) => ((RunStatusIcon)d).Render()));

    public RunStatusIcon()
    {
        InitializeComponent();
        Loaded += (_, _) => Render();
        Render();
    }

    public PipelineRunStatus Status
    {
        get => (PipelineRunStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public bool AwaitingApproval
    {
        get => (bool)GetValue(AwaitingApprovalProperty);
        set => SetValue(AwaitingApprovalProperty, value);
    }

    private void Render()
    {
        Glyph.Glyph = DevelopmentText.StatusGlyph(Status);
        var state = AwaitingApproval ? "Approval" : Status switch
        {
            PipelineRunStatus.Succeeded => "Succeeded",
            PipelineRunStatus.Failed or PipelineRunStatus.Partial => "Failed",
            PipelineRunStatus.Running => "Running",
            _ => "Other",
        };
        VisualStateManager.GoToState(this, state, false);
        AutomationProperties.SetName(this, AwaitingApproval ? DevelopmentText.WaitingForApproval : Status.DisplayName());
    }
}
