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
    private bool _loading;
    private bool _submitting;

    /// <summary>
    /// Raised after an action was attempted, whether or not this ticket is
    /// still on screen. Whatever the outcome, the loaded actions are spent:
    /// the page reloads the ticket, its feed and the workflow before anything
    /// can be offered again.
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
        _loading = false;
        HideConfirm();
        ResultBorder.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Collapsed;
        UpdateInteractivity();
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

        // While actions load, the buttons on screen describe a workflow that
        // may already be gone, so none of them can be pressed.
        _loading = true;
        UpdateInteractivity();

        var snapshot = await _controller.LoadAsync(ticketId);
        if (snapshot == null || _ticketId != ticketId) return;

        _loading = false;
        Render(snapshot);
        UpdateInteractivity();
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

    /// <summary>
    /// One place decides what can be pressed: nothing while the workflow loads
    /// or an action is in flight, and Confirm only while a live choice is
    /// pending. A spent confirmation stays readable but cannot be sent.
    /// </summary>
    private void UpdateInteractivity()
    {
        var busy = _loading || _submitting;
        WorkflowProgress.IsActive = _loading;
        WorkflowProgress.Visibility = _loading ? Visibility.Visible : Visibility.Collapsed;
        SubmitProgress.IsActive = _submitting;
        SubmitProgress.Visibility = _submitting ? Visibility.Visible : Visibility.Collapsed;

        RefreshWorkflowButton.IsEnabled = !busy;
        StepsList.IsEnabled = !busy;
        ConfirmButton.IsEnabled = !busy && _pending != null;
        ConfirmCancelButton.IsEnabled = !_submitting;
        ConfirmCommentsBox.IsReadOnly = busy || _pending == null;
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (_ticketId is int id) await LoadAsync(id);
    }

    private void OnActionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WorkflowActionRow action } || _loading || _submitting) return;
        if (action.TicketId != _ticketId || _controller?.Current is not { Phase: TicketWorkflowPhase.Loaded } current
            || current.TicketId != action.TicketId) return;

        // Comments from an earlier attempt on this ticket are kept, so a retry
        // after a failure does not mean retyping them; anything else starts empty.
        var keepComments = ConfirmPanel.Visibility == Visibility.Visible && _pending == null;

        _pending = action;
        ResultBorder.Visibility = Visibility.Collapsed;
        ConfirmPromptText.Text = $"{action.Name} “{action.StepName}” on ticket #{action.TicketId}?";
        ConfirmTooltipText.Text = action.Tooltip ?? "";
        ConfirmTooltipText.Visibility = action.Tooltip == null ? Visibility.Collapsed : Visibility.Visible;
        ConfirmButton.Content = action.Name;
        if (!keepComments) ConfirmCommentsBox.Text = "";
        ConfirmPanel.Visibility = Visibility.Visible;
        UpdateInteractivity();
        ConfirmCommentsBox.Focus();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => HideConfirm();

    private void HideConfirm()
    {
        _pending = null;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        UpdateInteractivity();
    }

    private async void OnConfirmClicked(object sender, RoutedEventArgs e)
    {
        if (_controller == null || _pending is not { } action || _loading || _submitting) return;

        // The choice is used up the moment it is sent; it is never sendable twice.
        _pending = null;
        _submitting = true;
        UpdateInteractivity();
        TicketWorkflowSubmitOutcome outcome;
        try
        {
            outcome = await _controller.SubmitAsync(action.TicketId, action.StepId, action.ActionId, ConfirmCommentsBox.Text);
        }
        finally
        {
            _submitting = false;
        }

        if (_ticketId == outcome.TicketId)
        {
            if (outcome.Succeeded)
            {
                HideConfirm();
                ShowResult(outcome.Message, isError: false);
            }
            else
            {
                // The confirmation stays on screen, spent: the message and the
                // comments remain readable, but a retry needs the refreshed
                // workflow and a fresh choice — the failed request may still
                // have been applied.
                ConfirmPromptText.Text = $"{action.Name} “{action.StepName}” was not confirmed.";
                ConfirmTooltipText.Text = "The workflow is being refreshed. Choose an action again to retry; your comments are kept.";
                ConfirmTooltipText.Visibility = Visibility.Visible;
                ShowResult(outcome.Message, isError: true);
            }
        }
        UpdateInteractivity();

        ActionFinished?.Invoke(outcome);
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
