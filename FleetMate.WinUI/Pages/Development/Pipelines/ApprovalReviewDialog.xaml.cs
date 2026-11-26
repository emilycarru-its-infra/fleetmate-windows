using FleetMate.Core.Development;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FleetMate.WinUI.Pages.Development.Pipelines;

/// <summary>
/// Approve or reject the approvals holding a run, one at a time, with an
/// optional comment. Stays open while more are waiting; closes once none are.
/// </summary>
public sealed partial class ApprovalReviewDialog : ContentDialog
{
    private readonly PipelineRun _run;
    private readonly Func<IReadOnlyList<PipelineApproval>> _approvals;
    private bool _working;
    private bool _confirmingReject;

    /// <summary>Raised after an approval was answered, so the run can be re-read.</summary>
    public event EventHandler? Responded;

    public ApprovalReviewDialog(PipelineRun run, Func<IReadOnlyList<PipelineApproval>> approvals)
    {
        _run = run;
        _approvals = approvals;
        InitializeComponent();
        Title = ApprovalReviewText.Title(run);
        RejectBar.Message = ApprovalReviewText.RejectMessage;
        PrimaryButtonClick += OnApprove;
        SecondaryButtonClick += OnReject;
        Opened += (_, _) => CommentBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        Render();
    }

    private PipelineApproval? Current => _approvals().FirstOrDefault();

    private void Render()
    {
        var approvals = _approvals();
        ErrorBar.IsOpen = false;
        SetConfirmingReject(false);
        if (approvals.FirstOrDefault() is not { } approval)
        {
            ApprovalPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            NothingText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            NothingText.Text = ApprovalReviewText.NothingWaiting;
            PrimaryButtonText = SecondaryButtonText = "";
            CloseButtonText = "Done";
            DefaultButton = ContentDialogButton.Close;
            return;
        }
        MetaText.Text = ApprovalReviewText.Meta(approval);
        MetaText.Visibility = DevelopmentUi.Show(MetaText.Text.Length > 0);
        InstructionsBox.Text = ApprovalReviewText.Instructions(approval);
        MoreText.Text = ApprovalReviewText.More(approvals.Count);
    }

    private void SetConfirmingReject(bool confirming)
    {
        _confirmingReject = confirming;
        RejectBar.IsOpen = confirming;
        SecondaryButtonText = confirming ? "Confirm Reject" : "Reject";
    }

    private async void ApproveAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ApprovalPanel.Visibility != Microsoft.UI.Xaml.Visibility.Visible) return;
        args.Handled = true;
        if (await RespondAsync(approve: true)) Hide();
    }

    private async void OnApprove(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try { args.Cancel = !await RespondAsync(approve: true); }
        finally { deferral.Complete(); }
    }

    private async void OnReject(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!_confirmingReject)
        {
            args.Cancel = true;
            SetConfirmingReject(true);
            return;
        }
        var deferral = args.GetDeferral();
        try { args.Cancel = !await RespondAsync(approve: false); }
        finally { deferral.Complete(); }
    }

    /// <summary>Answer the current approval. True when none are left, so the dialog may close.</summary>
    private async Task<bool> RespondAsync(bool approve)
    {
        if (_working || Current is not { } approval) return Current is null;
        var services = App.Services;
        SetWorking(true);
        try
        {
            if (!services.DemoMode)
            {
                if (services.Config.AzureDevOps is not { } ado)
                {
                    ShowError("Azure DevOps is not configured.");
                    return false;
                }
                using var service = new AzureDevOpsService(ado);
                var result = await service.RespondToApprovalAsync(approval.Project, approval.Id, approve, CommentBox.Text);
                if (!result.Success)
                {
                    ShowError($"Could not {(approve ? "approve" : "reject")}: {result.Error}");
                    return false;
                }
            }

            services.PipelineApprovals.Remove(approval);
            Responded?.Invoke(this, EventArgs.Empty);
            CommentBox.Text = "";
            if (_approvals().Count == 0) return true;
            Render();
            return false;
        }
        finally
        {
            SetWorking(false);
        }
    }

    private void SetWorking(bool working)
    {
        _working = working;
        Working.IsActive = working;
        IsPrimaryButtonEnabled = IsSecondaryButtonEnabled = !working;
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
