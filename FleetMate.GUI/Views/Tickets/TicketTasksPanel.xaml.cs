using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>One task in the list.</summary>
public sealed record TicketTaskRow(TdxTicketTask Task)
{
    public int Id => Task.Id;
    public string Title => Task.DisplayTitle;
    public int Percent => Math.Clamp(Task.PercentComplete, 0, 100);
    public string PercentText => $"{Percent}%";
    public string State => TicketTaskFormat.State(Task);
    public string Responsibility => TicketTaskFormat.Responsibility(Task);
    public string TypeLabel => TicketTaskFormat.TypeLabel(Task.Type);
    /// <summary>Standard tasks carry no badge; the others say what they are.</summary>
    public Visibility TypeBadgeVisibility => Task.IsStandard ? Visibility.Collapsed : Visibility.Visible;
    public string? TypeTooltip => TicketTasksPanel.RestrictionNote(Task.Type);
    public string AccessibleName => $"{Title}, {TypeLabel}, {Percent} percent, {State}, {Responsibility}";
}

/// <summary>One activity entry of a task, as plain text.</summary>
public sealed record TaskFeedRow(string Author, string When, string Body, bool IsPrivate, IReadOnlyList<TaskFeedRow> Replies)
{
    public Visibility PrivateVisibility => IsPrivate ? Visibility.Visible : Visibility.Collapsed;

    public static TaskFeedRow From(TdxFeedEntry entry) => new(
        string.IsNullOrWhiteSpace(entry.CreatedFullName) ? "Someone" : entry.CreatedFullName!.Trim(),
        entry.CreatedDate.Year > 1900 ? entry.CreatedDate.ToLocalTime().ToString("MMM d, yyyy 'at' h:mm tt", CultureInfo.CurrentCulture) : "",
        TicketTaskFormat.PlainText(entry.Body),
        entry.IsPrivate,
        entry.ReplyList.OrderBy(r => r.CreatedDate).Select(From).ToList());
}

/// <summary>
/// The Tasks section of the ticket detail panel. It never changes anything
/// on its own: every change is the operator's explicit Save, Complete, Add,
/// Edit, Delete or Apply, sent once through <see cref="TicketTaskController"/>.
/// After anything was sent — whatever the outcome — the page reloads the
/// ticket, its feed, its workflow and these tasks before anything can be
/// sent again, because a completed workflow task can advance the workflow and
/// a failed request can still have been applied.
/// </summary>
public partial class TicketTasksPanel : UserControl
{
    /// <summary>Reads a ticket's task-template list (TeamDynamix's own page); throws when it cannot.</summary>
    public delegate Task<IReadOnlyList<TaskTemplateChoice>> TemplateCatalog(int ticketId, CancellationToken ct);

    // The section stays as the operator left it from one ticket to the next.
    private static bool _expanded;

    private TicketTaskController? _controller;
    private TemplateCatalog? _catalog;
    private int? _ticketId;
    private int? _selectedTaskId;
    private bool _loading;
    private bool _submitting;
    private bool _syncingPercent;
    private long _feedVersion;

    /// <summary>
    /// Raised when a change was sent (or the loaded tasks were found stale),
    /// so the page reloads the ticket, its feed, its workflow and the tasks.
    /// </summary>
    public event Action<TicketTaskSubmitOutcome>? TasksChanged;

    public TicketTasksPanel()
    {
        InitializeComponent();
        TasksExpander.IsExpanded = _expanded;
    }

    public void Attach(ITicketTaskApi api, TemplateCatalog catalog)
    {
        _controller = new TicketTaskController(api);
        _catalog = catalog;
    }

    /// <summary>What a non-standard task allows, for its badge and details.</summary>
    internal static string? RestrictionNote(TicketTaskType type) => type switch
    {
        TicketTaskType.Workflow =>
            "A workflow task is a step of the ticket's workflow. Its progress can be updated here; completing it can advance the workflow. It cannot be added, edited or deleted as a task.",
        TicketTaskType.MaintenanceActivity =>
            "A maintenance activity is scheduled by TeamDynamix. Its progress can be updated here; it cannot be added, edited or deleted as a task.",
        TicketTaskType.Standard => null,
        _ => "TeamDynamix did not say what kind of task this is, so only its progress can be updated here.",
    };

    /// <summary>Hide the section and drop whatever ticket it showed.</summary>
    public void Clear()
    {
        _controller?.Clear();
        _ticketId = null;
        _selectedTaskId = null;
        _loading = false;
        _feedVersion++;
        TasksList.ItemsSource = null;
        DetailBorder.Visibility = Visibility.Collapsed;
        ResultBorder.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Collapsed;
        UpdateInteractivity();
    }

    /// <summary>Load (or reload) the tasks of <paramref name="ticketId"/>. A load overtaken by another ticket is dropped.</summary>
    public async Task LoadAsync(int ticketId)
    {
        if (_controller == null) return;

        if (_ticketId != ticketId)
        {
            // A selection, a half-written update and the last result belong
            // to the ticket they were made on.
            _selectedTaskId = null;
            UpdateCommentBox.Text = "";
            UpdatePrivateBox.IsChecked = false;
            ResultBorder.Visibility = Visibility.Collapsed;
            TasksList.ItemsSource = null;
            DetailBorder.Visibility = Visibility.Collapsed;
            TasksHeaderText.Text = "Tasks";
        }
        _ticketId = ticketId;
        Visibility = Visibility.Visible;

        // While the tasks load, what is on screen may already be gone, so
        // nothing on it can be sent.
        _loading = true;
        UpdateInteractivity();

        var snapshot = await _controller.LoadAsync(ticketId);
        if (snapshot == null || _ticketId != ticketId) return;

        _loading = false;
        Render(snapshot);
        UpdateInteractivity();
    }

    private void Render(TicketTasksSnapshot snapshot)
    {
        if (snapshot.Phase == TicketTasksPhase.Failed)
        {
            TasksHeaderText.Text = "Tasks · unavailable";
            TasksList.ItemsSource = null;
            DetailBorder.Visibility = Visibility.Collapsed;
            ShowMessage(snapshot.Error ?? "The tasks could not be read.");
            return;
        }

        var tasks = snapshot.Tasks;
        TasksHeaderText.Text = tasks.Count == 0 ? "Tasks · none" : $"Tasks · {tasks.Count} · {TicketTaskFormat.Summary(tasks)}";
        var rows = tasks.Select(t => new TicketTaskRow(t)).ToList();
        TasksList.ItemsSource = rows;

        if (rows.Count == 0) ShowMessage("No tasks on this ticket.");
        else MessageText.Visibility = Visibility.Collapsed;

        var keep = rows.FirstOrDefault(r => r.Id == _selectedTaskId);
        if (keep == null) _selectedTaskId = null;
        TasksList.SelectedItem = keep;
        ShowDetail(keep?.Task, freshForm: true);
    }

    private void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessageText.Visibility = Visibility.Visible;
    }

    private void OnExpanderToggled(object sender, RoutedEventArgs e) => _expanded = TasksExpander.IsExpanded;

    // ── Selection and details ──────────────────────────────────────────

    private void OnTaskSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = TasksList.SelectedItem as TicketTaskRow;
        if (row?.Id == _selectedTaskId) return;
        _selectedTaskId = row?.Id;
        // An update typed for one task never follows the selection to another.
        UpdateCommentBox.Text = "";
        UpdatePrivateBox.IsChecked = false;
        ShowDetail(row?.Task, freshForm: true);
        UpdateInteractivity();
    }

    private void ShowDetail(TdxTicketTask? task, bool freshForm)
    {
        _feedVersion++;
        if (task == null)
        {
            DetailBorder.Visibility = Visibility.Collapsed;
            return;
        }

        DetailBorder.Visibility = Visibility.Visible;
        DetailTitleText.Text = task.DisplayTitle;
        DetailTypeText.Text = task.Order > 0 ? $"{TicketTaskFormat.TypeLabel(task.Type)} · #{task.Order}" : TicketTaskFormat.TypeLabel(task.Type);
        DetailStateText.Text = $"{TicketTaskFormat.State(task)} · {Math.Clamp(task.PercentComplete, 0, 100)}% complete"
                               + (task.DetectedConflictCount > 0 ? $" · {task.DetectedConflictCount} scheduling conflict(s)" : "");
        DetailResponsibleText.Text = $"Responsible: {TicketTaskFormat.Responsibility(task)}";
        SetLine(DetailDatesText, TicketTaskFormat.Dates(task));
        SetLine(DetailEffortText, TicketTaskFormat.Effort(task));
        SetLine(DetailPredecessorText, task.PredecessorId > 0
            ? $"After: {(string.IsNullOrWhiteSpace(task.PredecessorTitle) ? $"task {task.PredecessorId}" : task.PredecessorTitle!.Trim())}"
              + (task.IsActive ? "" : " (not complete yet, so this task is inactive)")
            : null);
        SetLine(DetailDescriptionText, TicketTaskFormat.PlainText(task.Description) is { Length: > 0 } d ? d : null);
        SetLine(DetailRestrictionText, RestrictionNote(task.Type));

        var editable = task.IsStandard ? Visibility.Visible : Visibility.Collapsed;
        EditTaskButton.Visibility = editable;
        DeleteTaskButton.Visibility = editable;
        CompleteButton.Visibility = task.IsComplete ? Visibility.Collapsed : Visibility.Visible;
        SetLine(DetailWaitingText, TicketTaskController.WaitingOnPredecessor(task));

        if (freshForm) SetPercent(task.PercentComplete);
        _ = LoadFeedAsync(task.Id);
    }

    private static void SetLine(TextBlock block, string? text)
    {
        block.Text = text ?? "";
        block.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task LoadFeedAsync(int taskId)
    {
        if (_controller == null || _ticketId is not int ticketId) return;
        var version = _feedVersion;
        FeedList.ItemsSource = null;
        FeedMessageText.Visibility = Visibility.Collapsed;
        FeedHeaderText.Text = "Activity";
        FeedProgress.IsActive = true;
        FeedProgress.Visibility = Visibility.Visible;

        var result = await _controller.LoadFeedAsync(ticketId, taskId);
        if (version != _feedVersion || _selectedTaskId != taskId) return;

        FeedProgress.IsActive = false;
        FeedProgress.Visibility = Visibility.Collapsed;
        if (result is not { } loaded) return;
        if (loaded.Error != null)
        {
            FeedMessageText.Text = loaded.Error;
            FeedMessageText.Visibility = Visibility.Visible;
            return;
        }
        var rows = loaded.Feed.Select(TaskFeedRow.From).ToList();
        FeedHeaderText.Text = rows.Count == 0 ? "Activity" : $"Activity · {rows.Count}";
        FeedList.ItemsSource = rows;
        if (rows.Count == 0)
        {
            FeedMessageText.Text = "No activity on this task yet.";
            FeedMessageText.Visibility = Visibility.Visible;
        }
    }

    // ── Progress ───────────────────────────────────────────────────────

    private void SetPercent(int percent)
    {
        _syncingPercent = true;
        PercentSlider.Value = Math.Clamp(percent, 0, 100);
        PercentBox.Text = Math.Clamp(percent, 0, 100).ToString(CultureInfo.InvariantCulture);
        _syncingPercent = false;
    }

    private void OnPercentSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingPercent || PercentBox == null) return;
        _syncingPercent = true;
        PercentBox.Text = ((int)Math.Round(e.NewValue)).ToString(CultureInfo.InvariantCulture);
        _syncingPercent = false;
    }

    private void OnPercentTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingPercent || PercentSlider == null) return;
        if (TryReadPercent(PercentBox.Text, out var percent))
        {
            _syncingPercent = true;
            PercentSlider.Value = percent;
            _syncingPercent = false;
        }
    }

    /// <summary>A whole number from 0 to 100, nothing else.</summary>
    internal static bool TryReadPercent(string? text, out int percent) =>
        int.TryParse((text ?? "").Trim().TrimEnd('%').Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out percent)
        && percent is >= 0 and <= 100;

    private void OnUpdateCommentKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            OnSaveProgressClicked(sender, e);
        }
    }

    private TdxTicketTask? SelectedTask() =>
        _controller?.Current is { Phase: TicketTasksPhase.Loaded } current && current.TicketId == _ticketId && _selectedTaskId is int id
            ? current.Find(id)
            : null;

    private async void OnSaveProgressClicked(object sender, RoutedEventArgs e)
    {
        if (Busy || SelectedTask() is not { } task || _ticketId is not int ticketId) return;
        if (!TryReadPercent(PercentBox.Text, out var percent))
        {
            ShowResult("Progress must be a whole number from 0 to 100.", ResultKind.Neutral);
            PercentBox.Focus();
            return;
        }
        if (percent == 100 && !task.IsComplete && !ConfirmComplete(task, ticketId)) return;
        await SubmitAsync(() => _controller!.UpdateProgressAsync(ticketId, task.Id, percent, UpdateCommentBox.Text,
            UpdatePrivateBox.IsChecked == true));
    }

    private async void OnCompleteClicked(object sender, RoutedEventArgs e)
    {
        if (Busy || SelectedTask() is not { } task || _ticketId is not int ticketId) return;
        if (!ConfirmComplete(task, ticketId)) return;
        SetPercent(100);
        await SubmitAsync(() => _controller!.CompleteAsync(ticketId, task.Id, UpdateCommentBox.Text, UpdatePrivateBox.IsChecked == true));
    }

    private bool ConfirmComplete(TdxTicketTask task, int ticketId)
    {
        var prompt = $"Complete “{task.DisplayTitle}” on ticket #{ticketId}? Its progress is set to 100%.";
        if (task.Type == TicketTaskType.Workflow) prompt += "\n\nCompleting a workflow task can advance the ticket's workflow.";
        return MessageBox.Show(Window.GetWindow(this)!, prompt, "Complete task",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    // ── Add, edit, delete, apply ───────────────────────────────────────

    private bool IsLoadedForTicket => _controller?.Current is { Phase: TicketTasksPhase.Loaded } current && current.TicketId == _ticketId;

    private void OnAddTaskClicked(object sender, RoutedEventArgs e)
    {
        if (Busy || !IsLoadedForTicket || _ticketId is not int ticketId) return;
        var dialog = new TicketTaskEditorDialog(_controller!, ticketId, null) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
        if (dialog.Outcome is { } outcome) Report(outcome);
    }

    private void OnEditTaskClicked(object sender, RoutedEventArgs e)
    {
        if (Busy || SelectedTask() is not { IsStandard: true } task || _ticketId is not int ticketId) return;
        var dialog = new TicketTaskEditorDialog(_controller!, ticketId, task) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
        if (dialog.Outcome is { } outcome) Report(outcome);
    }

    private async void OnDeleteTaskClicked(object sender, RoutedEventArgs e)
    {
        if (Busy || SelectedTask() is not { IsStandard: true } task || _ticketId is not int ticketId) return;
        var confirm = MessageBox.Show(Window.GetWindow(this)!,
            $"Delete the task “{task.DisplayTitle}” from ticket #{ticketId}?\n\nThis cannot be undone.",
            "Delete task", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;
        await SubmitAsync(() => _controller!.DeleteAsync(ticketId, task.Id));
    }

    private void OnApplyTemplateClicked(object sender, RoutedEventArgs e)
    {
        if (Busy || !IsLoadedForTicket || _ticketId is not int ticketId || _catalog == null) return;
        var catalog = _catalog;
        var dialog = new ApplyTaskTemplateDialog(_controller!, ticketId, ct => catalog(ticketId, ct)) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
        if (dialog.Outcome is { } outcome) Report(outcome);
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (_ticketId is int id) await LoadAsync(id);
    }

    // ── Sending ────────────────────────────────────────────────────────

    private bool Busy => _loading || _submitting || _controller?.IsSubmitting == true;

    private async Task SubmitAsync(Func<Task<TicketTaskSubmitOutcome>> send)
    {
        _submitting = true;
        ResultBorder.Visibility = Visibility.Collapsed;
        UpdateInteractivity();
        TicketTaskSubmitOutcome outcome;
        try
        {
            outcome = await send();
        }
        finally
        {
            _submitting = false;
            UpdateInteractivity();
        }
        Report(outcome);
    }

    /// <summary>
    /// Show what happened and, when anything may have changed (or the tasks
    /// on screen were found stale), have the page reload everything. Nothing
    /// is retried automatically.
    /// </summary>
    private void Report(TicketTaskSubmitOutcome outcome)
    {
        var mustReload = outcome.WasSent || _controller?.Current == null;
        if (_ticketId == outcome.TicketId)
        {
            ShowResult(outcome.Message, outcome.Status switch
            {
                TicketTaskSubmitStatus.Succeeded => ResultKind.Success,
                TicketTaskSubmitStatus.NotSent => ResultKind.Neutral,
                _ => ResultKind.Failure,
            });
            // A sent update is done with; one that failed keeps its comment
            // so it can be resent after the refresh, if it should be.
            if (outcome.Succeeded) UpdateCommentBox.Text = "";
        }
        if (mustReload) TasksChanged?.Invoke(outcome);
    }

    private enum ResultKind { Success, Failure, Neutral }

    private void ShowResult(string message, ResultKind kind)
    {
        ResultText.Text = message;
        // Failures are orange, never red, as everywhere else in FleetMate.
        ResultBorder.Background = kind switch
        {
            ResultKind.Success => new SolidColorBrush(Color.FromRgb(0xC0, 0xFF, 0xC0)),
            ResultKind.Failure => new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0xB2)),
            _ => new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
        };
        ResultBorder.Visibility = Visibility.Visible;
    }

    /// <summary>One place decides what can be pressed: nothing while loading or sending.</summary>
    private void UpdateInteractivity()
    {
        var busy = Busy;
        LoadProgress.IsActive = _loading;
        LoadProgress.Visibility = _loading ? Visibility.Visible : Visibility.Collapsed;
        SubmitProgress.IsActive = _submitting;
        SubmitProgress.Visibility = _submitting ? Visibility.Visible : Visibility.Collapsed;

        var loaded = IsLoadedForTicket;
        RefreshButton.IsEnabled = !busy;
        AddTaskButton.IsEnabled = !busy && loaded;
        ApplyTemplateButton.IsEnabled = !busy && loaded && _catalog != null;
        TasksList.IsEnabled = !_submitting;

        var selected = SelectedTask();
        var canAct = !busy && loaded && selected != null;
        // A task waiting on its predecessor takes a comment, not progress.
        var canProgress = canAct && TicketTaskController.WaitingOnPredecessor(selected!) == null;
        SaveProgressButton.IsEnabled = canAct;
        CompleteButton.IsEnabled = canProgress;
        EditTaskButton.IsEnabled = canAct;
        DeleteTaskButton.IsEnabled = canAct;
        PercentBox.IsReadOnly = !canProgress;
        PercentSlider.IsEnabled = canProgress;
        UpdateCommentBox.IsReadOnly = _submitting;
    }
}
