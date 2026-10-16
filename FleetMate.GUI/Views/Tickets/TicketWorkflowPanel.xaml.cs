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
/// The Workflow section of the ticket detail panel, collapsed until opened.
/// Hidden until a ticket is loaded; a ticket without a workflow keeps it
/// hidden unless a workflow could be assigned to it. Reassigning a step is not
/// offered: TeamDynamix reserves it for admin service accounts, and this panel
/// acts only as the signed-in user.
///
/// Restart Workflow and Change Workflow capture the ticket and the workflow on
/// screen when chosen, confirm in a native dialog whose default is Cancel, and
/// hand the rest to <see cref="TicketWorkflowAssignmentController"/>, which
/// checks the live workflow again before it sends anything.
/// </summary>
public partial class TicketWorkflowPanel : UserControl
{
    private TicketWorkflowController? _controller;
    private TicketWorkflowAssignmentController? _assignment;
    private WorkflowActionRow? _pending;
    private int? _ticketId;
    private bool _loading;
    private bool _submitting;
    /// <summary>True while the workflow list is read for Change, or a change is checked and sent.</summary>
    private bool _managing;
    /// <summary>The snapshot an open Change Workflow picker was opened against.</summary>
    private TicketWorkflowSnapshot? _changeBasis;
    /// <summary>A Restart or Change asked for from the ticket list, waiting for that ticket's workflow.</summary>
    private (int TicketId, TicketWorkflowChangeKind Kind)? _queued;

    /// <summary>
    /// Raised after an action was attempted, whether or not this ticket is
    /// still on screen. Whatever the outcome, the loaded actions are spent:
    /// the page reloads the ticket, its feed and the workflow before anything
    /// can be offered again.
    /// </summary>
    public event Action<TicketWorkflowSubmitOutcome>? ActionFinished;

    /// <summary>
    /// Raised after a restart or change was attempted, or found the workflow
    /// had moved. When the outcome says so, the page reloads the ticket, its
    /// feed and the workflow; a change is never sent again by itself.
    /// </summary>
    public event Action<TicketWorkflowChangeOutcome>? ChangeFinished;

    public TicketWorkflowPanel()
    {
        InitializeComponent();
    }

    public void Attach(ITicketWorkflowApi api, ITicketWorkflowAssignmentApi? assignment = null, ITicketWorkflowCatalog? catalog = null)
    {
        _controller = new TicketWorkflowController(api);
        _assignment = assignment == null ? null : new TicketWorkflowAssignmentController(assignment, catalog);
    }

    /// <summary>Hide the section and drop whatever ticket it showed.</summary>
    public void Clear()
    {
        _controller?.Clear();
        _ticketId = null;
        _loading = false;
        _queued = null;
        HideConfirm();
        HideChange();
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
            HideChange();
            if (_queued is { } queued && queued.TicketId != ticketId) _queued = null;
            WorkflowSummaryText.Text = "";
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
        RunQueued();
    }

    private void Render(TicketWorkflowSnapshot snapshot)
    {
        switch (snapshot.Phase)
        {
            case TicketWorkflowPhase.None:
                HideConfirm();
                // Most tickets have no workflow. The section stays only when one
                // could be assigned, and then collapsed, saying "None".
                if (_assignment is not { CanChange: true })
                {
                    Visibility = Visibility.Collapsed;
                    return;
                }
                Visibility = Visibility.Visible;
                WorkflowHeader.Visibility = Visibility.Collapsed;
                WorkflowDescriptionText.Visibility = Visibility.Collapsed;
                StepsList.ItemsSource = null;
                WorkflowSummaryText.Text = "None";
                ShowMessage("This ticket has no workflow.");
                return;

            case TicketWorkflowPhase.Failed:
                Visibility = Visibility.Visible;
                WorkflowSummaryText.Text = "Could not load";
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
        WorkflowSummaryText.Text = $"{WorkflowNameText.Text} · {workflow.StatusLabel}";
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
        var busy = _loading || _submitting || _managing;
        WorkflowProgress.IsActive = _loading;
        WorkflowProgress.Visibility = _loading ? Visibility.Visible : Visibility.Collapsed;
        SubmitProgress.IsActive = _submitting;
        SubmitProgress.Visibility = _submitting ? Visibility.Visible : Visibility.Collapsed;
        ChangeProgress.IsActive = _managing;
        ChangeProgress.Visibility = _managing ? Visibility.Visible : Visibility.Collapsed;

        RefreshWorkflowButton.IsEnabled = !busy;
        RestartWorkflowButton.IsEnabled = !busy && RestartBasis() != null;
        var assign = ChangeBasis() is { Phase: TicketWorkflowPhase.None };
        ChangeWorkflowButton.IsEnabled = !busy && ChangeBasis() != null;
        ChangeWorkflowButton.Content = assign ? "Assign Workflow…" : "Change Workflow…";
        MenuChangeWorkflow.Header = ChangeWorkflowButton.Content;
        StepsList.IsEnabled = !busy;
        ConfirmButton.IsEnabled = !busy && _pending != null;
        ConfirmCancelButton.IsEnabled = !_submitting;
        ConfirmCommentsBox.IsReadOnly = busy || _pending == null;
        ChangeChoiceBox.IsEnabled = !busy && _changeBasis != null;
        ChangeContinueButton.IsEnabled = !busy && _changeBasis != null && ChangeChoiceBox.SelectedItem is TdxWorkflowChoice;
        ChangeCancelButton.IsEnabled = !_submitting;
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (_ticketId is int id) await LoadAsync(id);
    }

    private void OnActionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WorkflowActionRow action } || _loading || _submitting || _managing) return;
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
        if (_controller == null || _pending is not { } action || _loading || _submitting || _managing) return;

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

    // ── Restart Workflow and Change Workflow ───────────────────────────

    private bool Busy => _loading || _submitting || _managing;

    /// <summary>The loaded snapshot a restart can be made against, or null.</summary>
    private TicketWorkflowSnapshot? RestartBasis() =>
        _assignment is { CanChange: true } && _controller?.Current is { Phase: TicketWorkflowPhase.Loaded, Workflow.WorkflowConfigurationId: > 0 } snapshot
        && snapshot.TicketId == _ticketId && _controller.IsCurrent(snapshot)
            ? snapshot
            : null;

    /// <summary>The snapshot a change or first assignment can be made against, or null.</summary>
    private TicketWorkflowSnapshot? ChangeBasis() =>
        _assignment is { CanChange: true } && _controller?.Current is { Phase: TicketWorkflowPhase.Loaded or TicketWorkflowPhase.None } snapshot
        && snapshot.TicketId == _ticketId && _controller.IsCurrent(snapshot)
            ? snapshot
            : null;

    /// <summary>
    /// Restart or change the workflow of <paramref name="ticketId"/>, from the
    /// ticket list's menu: at once when its workflow is on screen, otherwise
    /// as soon as it has loaded.
    /// </summary>
    public void RequestChange(int ticketId, TicketWorkflowChangeKind kind)
    {
        _queued = (ticketId, kind);
        if (_ticketId == ticketId && !_loading) RunQueued();
    }

    private void RunQueued()
    {
        if (_queued is not { } queued || queued.TicketId != _ticketId) return;
        _queued = null;
        Visibility = Visibility.Visible;
        WorkflowExpander.IsExpanded = true;
        if (queued.Kind == TicketWorkflowChangeKind.Restart) BeginRestart();
        else _ = BeginChangeAsync();
    }

    private void OnSectionContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        MenuRefreshWorkflow.IsEnabled = RefreshWorkflowButton.IsEnabled;
        MenuRestartWorkflow.IsEnabled = RestartWorkflowButton.IsEnabled;
        MenuChangeWorkflow.IsEnabled = ChangeWorkflowButton.IsEnabled;
    }

    private void OnRestartClicked(object sender, RoutedEventArgs e) => BeginRestart();

    private void OnChangeClicked(object sender, RoutedEventArgs e) => _ = BeginChangeAsync();

    private async void BeginRestart()
    {
        if (Busy || _ticketId is not int ticketId) return;
        WorkflowExpander.IsExpanded = true;
        HideChange();

        var basis = RestartBasis();
        var (request, reason) = TicketWorkflowChangeRequest.Restart(ticketId, basis?.Workflow);
        if (basis == null || request == null)
        {
            ShowResult(reason ?? "The workflow cannot be restarted until it has loaded.", isError: true);
            return;
        }
        await ConfirmAndSubmitAsync(request, basis);
    }

    /// <summary>Counts Change pickers, so a list read for one that was closed is dropped.</summary>
    private long _changeGeneration;

    private async Task BeginChangeAsync()
    {
        if (Busy || _assignment == null || ChangeBasis() is not { } basis) return;
        WorkflowExpander.IsExpanded = true;
        HideConfirm();
        HideChange();
        var generation = ++_changeGeneration;

        var current = basis.Workflow;
        ResultBorder.Visibility = Visibility.Collapsed;
        ChangePromptText.Text = current == null
            ? $"Assign a workflow to ticket #{basis.TicketId}"
            : $"Replace “{TicketWorkflowExpectation.DisplayName(current)}” on ticket #{basis.TicketId}";
        ChangeNote("Reading the workflows TeamDynamix offers for this ticket…");
        ChangePanel.Visibility = Visibility.Visible;

        IReadOnlyList<TdxWorkflowChoice>? choices = null;
        string? error = null;
        _managing = true;
        UpdateInteractivity();
        try
        {
            choices = await _assignment.GetChoicesAsync(basis.TicketId, current);
        }
        catch (Exception ex)
        {
            error = ex is TdxWorkflowException ? ex.Message : "The workflow list could not be read from TeamDynamix.";
        }
        finally
        {
            _managing = false;
        }

        // Closed, another ticket opened, or the workflow reloaded meanwhile:
        // this list belongs to none of them.
        if (generation != _changeGeneration || _ticketId != basis.TicketId || _controller?.IsCurrent(basis) != true)
        {
            if (generation == _changeGeneration) HideChange();
            UpdateInteractivity();
            return;
        }

        if (choices == null)
            ChangeNote(error!);
        else if (choices.Count == 0)
            ChangeNote("TeamDynamix offers no other workflow for this ticket.");
        else
        {
            _changeBasis = basis;
            ChangeChoiceBox.ItemsSource = choices;
            ChangeNote(current == null
                ? "Choose the workflow to assign."
                : "Choose the workflow to put on the ticket in place of the current one.");
        }
        UpdateInteractivity();
        if (_changeBasis != null) ChangeChoiceBox.Focus();
    }

    private void ChangeNote(string text)
    {
        ChangeNoteText.Text = text;
        ChangeNoteText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnChangeChoiceChanged(object sender, SelectionChangedEventArgs e) => UpdateInteractivity();

    private void OnChangeCancelClicked(object sender, RoutedEventArgs e) => HideChange();

    private void HideChange()
    {
        _changeGeneration++;
        _changeBasis = null;
        ChangePanel.Visibility = Visibility.Collapsed;
        ChangeChoiceBox.ItemsSource = null;
        UpdateInteractivity();
    }

    private async void OnChangeContinueClicked(object sender, RoutedEventArgs e)
    {
        if (Busy || _changeBasis is not { } basis || ChangeChoiceBox.SelectedItem is not TdxWorkflowChoice choice) return;
        var (request, reason) = TicketWorkflowChangeRequest.Change(basis.TicketId, basis.Workflow, choice);
        if (request == null)
        {
            ChangeNote(reason!);
            return;
        }
        await ConfirmAndSubmitAsync(request, basis);
    }

    /// <summary>
    /// Confirm <paramref name="request"/> in a native dialog whose default
    /// button is Cancel, so Enter never sends it, and send it only if the
    /// ticket and the workflow on screen are still the ones it was made from.
    /// </summary>
    private async Task ConfirmAndSubmitAsync(TicketWorkflowChangeRequest request, TicketWorkflowSnapshot basis)
    {
        if (_assignment == null) return;
        HideConfirm();

        var owner = Window.GetWindow(this);
        var answer = owner != null
            ? MessageBox.Show(owner, request.ConfirmationText(), request.Title, MessageBoxButton.OKCancel,
                MessageBoxImage.Warning, MessageBoxResult.Cancel)
            : MessageBox.Show(request.ConfirmationText(), request.Title, MessageBoxButton.OKCancel,
                MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;

        // The dialog may have stayed open while the workflow reloaded or the
        // operator's selection moved; what they confirmed is no longer on screen.
        if (Busy || _ticketId != request.TicketId || _controller?.IsCurrent(basis) != true)
        {
            if (_ticketId == request.TicketId)
                ShowResult("The workflow changed while the confirmation was open, so nothing was sent. Review it and try again.", isError: true);
            return;
        }

        _managing = true;
        UpdateInteractivity();
        TicketWorkflowChangeOutcome outcome;
        try
        {
            outcome = await _assignment.SubmitAsync(request);
        }
        finally
        {
            _managing = false;
        }

        if (_ticketId == outcome.TicketId)
        {
            if (outcome.Status != TicketWorkflowChangeStatus.NotSent || outcome.WorkflowChanged) HideChange();
            ShowResult(outcome.Message, isError: !outcome.Succeeded);
        }
        UpdateInteractivity();

        ChangeFinished?.Invoke(outcome);
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
