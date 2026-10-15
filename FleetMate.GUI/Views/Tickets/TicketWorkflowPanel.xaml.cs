using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>An action button: one action TeamDynamix offered on one step.</summary>
public sealed record WorkflowActionRow(int TicketId, string StepId, string StepName, string ActionId, string Name, string? Tooltip);

/// <summary>An active step as the panel draws it.</summary>
public sealed record WorkflowStepRow(
    string Name, string? TypeName, string? TypeDescription, string? Description,
    bool IsInError, string? Note, IReadOnlyList<WorkflowActionRow> Actions)
{
    public Visibility TypeVisibility => Show(TypeName);
    public Visibility DescriptionVisibility => Show(Description);
    public Visibility NoteVisibility => Show(Note);
    public Visibility InErrorVisibility => IsInError ? Visibility.Visible : Visibility.Collapsed;

    private static Visibility Show(string? text) => string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The rows for a snapshot, one per active step, actions in the order TeamDynamix gave them.</summary>
    public static IReadOnlyList<WorkflowStepRow> From(TicketWorkflowSnapshot snapshot) =>
        snapshot.ActiveSteps.Select(view => new WorkflowStepRow(
            view.Step.DisplayName,
            Blank(view.Step.TypeName),
            Blank(view.Step.TypeDescription),
            Blank(view.Step.Description),
            view.Step.IsInError,
            view.ActionsNote,
            view.Actions.Select(a => new WorkflowActionRow(
                snapshot.TicketId, view.Step.Id, view.Step.DisplayName, a.Id, a.DisplayName, Blank(a.Tooltip))).ToList()))
        .ToList();

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>
/// The Workflow section of the ticket detail panel. Hidden until a ticket is
/// loaded; a ticket without a workflow keeps it hidden. Reassigning a step is
/// not offered: TeamDynamix reserves it for admin service accounts, and this
/// panel acts only as the signed-in user.
/// </summary>
public partial class TicketWorkflowPanel : UserControl
{
    private TicketWorkflowController? _controller;
    private WorkflowActionRow? _pending;
    private int? _ticketId;

    /// <summary>
    /// Raised after an action is answered, whether or not this ticket is still
    /// on screen, so the page can refresh the ticket and its feed or report
    /// the outcome elsewhere.
    /// </summary>
    public event Action<TicketWorkflowSubmitOutcome>? ActionFinished;

    public TicketWorkflowPanel()
    {
        InitializeComponent();
    }

    public void Attach(ITicketWorkflowApi api) => _controller = new TicketWorkflowController(api);

    /// <summary>Hide the section and drop whatever ticket it showed.</summary>
    public void Clear()
    {
        _controller?.Clear();
        _ticketId = null;
        HideConfirm();
        ResultBorder.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Load (or reload) the workflow of <paramref name="ticketId"/>. A load
    /// overtaken by another ticket is dropped rather than drawn.
    /// </summary>
    public async Task LoadAsync(int ticketId)
    {
        if (_controller == null) return;

        if (_ticketId != ticketId)
        {
            // A confirmation, its comments and the last result belong to the
            // ticket they were made on.
            HideConfirm();
            ResultBorder.Visibility = Visibility.Collapsed;
            StepsList.ItemsSource = null;
            WorkflowHeader.Visibility = Visibility.Collapsed;
            WorkflowDescriptionText.Visibility = Visibility.Collapsed;
            Visibility = Visibility.Collapsed;
        }
        _ticketId = ticketId;

        SetLoading(true);
        var snapshot = await _controller.LoadAsync(ticketId);
        if (snapshot == null || _ticketId != ticketId) return;
        SetLoading(false);
        Render(snapshot);
    }

    private void Render(TicketWorkflowSnapshot snapshot)
    {
        switch (snapshot.Phase)
        {
            case TicketWorkflowPhase.None:
                // Most tickets have no workflow; an empty section would only be noise.
                HideConfirm();
                Visibility = Visibility.Collapsed;
                return;

            case TicketWorkflowPhase.Failed:
                Visibility = Visibility.Visible;
                WorkflowHeader.Visibility = Visibility.Collapsed;
                WorkflowDescriptionText.Visibility = Visibility.Collapsed;
                StepsList.ItemsSource = null;
                HideConfirm();
                ShowMessage(snapshot.Error ?? "Could not read the workflow.");
                return;
        }

        var workflow = snapshot.Workflow!;
        Visibility = Visibility.Visible;
        WorkflowHeader.Visibility = Visibility.Visible;
        WorkflowNameText.Text = string.IsNullOrWhiteSpace(workflow.Name) ? "Workflow" : workflow.Name;
        WorkflowStatusText.Text = workflow.StatusLabel;
        WorkflowDescriptionText.Text = workflow.Description ?? "";
        WorkflowDescriptionText.Visibility = string.IsNullOrWhiteSpace(workflow.Description) ? Visibility.Collapsed : Visibility.Visible;

        var rows = WorkflowStepRow.From(snapshot);
        StepsList.ItemsSource = rows;
        if (rows.Count == 0)
            ShowMessage(workflow.IsComplete ? "The workflow is complete." : "No step is active right now.");
        else
            WorkflowMessageText.Visibility = Visibility.Collapsed;

        // A pending confirmation survives a refresh only if its action is still offered.
        if (_pending is { } pending && !rows.SelectMany(r => r.Actions).Contains(pending))
            HideConfirm();
    }

    private void ShowMessage(string text)
    {
        WorkflowMessageText.Text = text;
        WorkflowMessageText.Visibility = Visibility.Visible;
    }

    private void SetLoading(bool loading)
    {
        WorkflowProgress.IsActive = loading;
        WorkflowProgress.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        RefreshWorkflowButton.IsEnabled = !loading && _controller?.IsSubmitting != true;
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (_ticketId is int id) await LoadAsync(id);
    }

    private void OnActionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WorkflowActionRow action } || _controller?.IsSubmitting == true) return;
        if (action.TicketId != _ticketId) return;

        _pending = action;
        ResultBorder.Visibility = Visibility.Collapsed;
        ConfirmPromptText.Text = $"{action.Name} “{action.StepName}” on ticket #{action.TicketId}?";
        ConfirmTooltipText.Text = action.Tooltip ?? "";
        ConfirmTooltipText.Visibility = action.Tooltip == null ? Visibility.Collapsed : Visibility.Visible;
        ConfirmButton.Content = action.Name;
        ConfirmCommentsBox.Text = "";
        ConfirmPanel.Visibility = Visibility.Visible;
        ConfirmCommentsBox.Focus();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => HideConfirm();

    private void HideConfirm()
    {
        _pending = null;
        ConfirmPanel.Visibility = Visibility.Collapsed;
    }

    private async void OnConfirmClicked(object sender, RoutedEventArgs e)
    {
        if (_controller == null || _pending is not { } action || _controller.IsSubmitting) return;

        SetSubmitting(true);
        TicketWorkflowSubmitOutcome outcome;
        try
        {
            outcome = await _controller.SubmitAsync(action.TicketId, action.StepId, action.ActionId, ConfirmCommentsBox.Text);
        }
        finally
        {
            SetSubmitting(false);
        }

        if (_ticketId == outcome.TicketId)
        {
            // On failure the confirmation and its comments stay, so the
            // operator can correct and retry without retyping.
            if (outcome.Succeeded) HideConfirm();
            ShowResult(outcome.Message, isError: !outcome.Succeeded);
        }

        ActionFinished?.Invoke(outcome);
    }

    private void SetSubmitting(bool submitting)
    {
        SubmitProgress.IsActive = submitting;
        SubmitProgress.Visibility = submitting ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.IsEnabled = !submitting;
        ConfirmCancelButton.IsEnabled = !submitting;
        ConfirmCommentsBox.IsReadOnly = submitting;
        StepsList.IsEnabled = !submitting;
        RefreshWorkflowButton.IsEnabled = !submitting;
    }

    private void ShowResult(string message, bool isError)
    {
        ResultText.Text = message;
        // Failures are orange, never red, as everywhere else in FleetMate.
        ResultBorder.Background = isError
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0xB2))
            : new SolidColorBrush(Color.FromRgb(0xC0, 0xFF, 0xC0));
        ResultBorder.Visibility = Visibility.Visible;
    }
}
