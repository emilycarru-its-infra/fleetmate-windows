using System.Windows;
using System.Windows.Input;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;

namespace FleetMate.GUI.Views.Development;

/// <summary>
/// The Review dialog: the pipeline's instructions, who may approve and since
/// when, an optional comment, and Approve or Reject. Rejecting asks first,
/// because it stops the run.
/// </summary>
public partial class ApprovalReviewWindow : Window
{
    private readonly PipelineRun _run;
    private readonly Func<IReadOnlyList<PipelineApproval>> _approvals;
    private bool _working;

    /// <summary>Raised after an approval was answered, so the run can be re-read.</summary>
    public event EventHandler? Responded;

    public ApprovalReviewWindow(PipelineRun run, Func<IReadOnlyList<PipelineApproval>> approvals)
    {
        _run = run;
        _approvals = approvals;
        InitializeComponent();
        Title = ApprovalReviewText.Title(run);
        HeadingText.Text = Title;
        Render();
        Loaded += (_, _) => CommentBox.Focus();
    }

    private PipelineApproval? Current => _approvals().FirstOrDefault();

    private void Render()
    {
        var approvals = _approvals();
        if (approvals.FirstOrDefault() is not { } approval)
        {
            ApprovalPanel.Visibility = Visibility.Collapsed;
            NothingPanel.Visibility = Visibility.Visible;
            return;
        }

        MetaText.Text = ApprovalReviewText.Meta(approval);
        InstructionsBox.Text = ApprovalReviewText.Instructions(approval);
        MoreText.Text = ApprovalReviewText.More(approvals.Count);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && ApprovalPanel.IsVisible)
        {
            e.Handled = true;
            _ = RespondAsync(approve: true);
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();

    private async void OnApproveClicked(object sender, RoutedEventArgs e) => await RespondAsync(approve: true);

    private async void OnRejectClicked(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, ApprovalReviewText.RejectMessage, ApprovalReviewText.RejectTitle(_run),
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK) await RespondAsync(approve: false);
    }

    private async Task RespondAsync(bool approve)
    {
        if (_working || Current is not { } approval || Application.Current is not App app) return;
        if (app.Config.AzureDevOps is not { } ado)
        {
            ShowError("Azure DevOps is not configured.");
            return;
        }

        SetWorking(true);
        try
        {
            using var service = new AzureDevOpsService(ado);
            var result = await service.RespondToApprovalAsync(approval.Project, approval.Id, approve, CommentBox.Text);
            if (!result.Success)
            {
                ShowError($"Could not {(approve ? "approve" : "reject")}: {result.Error}");
                return;
            }

            app.PipelineApprovals.Remove(approval);
            Responded?.Invoke(this, EventArgs.Empty);
            CommentBox.Text = "";
            ErrorText.Visibility = Visibility.Collapsed;
            if (_approvals().Count == 0) Close(); else Render();
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
        Working.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = RejectButton.IsEnabled = ApproveButton.IsEnabled = !working;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
