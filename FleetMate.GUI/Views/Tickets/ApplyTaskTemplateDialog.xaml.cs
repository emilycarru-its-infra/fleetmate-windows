using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>
/// Apply a task template to a ticket. The list is what TeamDynamix's own Add
/// Task Template page offers for this ticket — never an ID typed in or a list
/// kept in FleetMate — and it is read again just before applying, so a
/// template withdrawn since the dialog opened is not applied. A confirmation
/// names the template and, when removal was chosen, says the existing tasks
/// cannot be restored. Removal defaults to off.
/// </summary>
public partial class ApplyTaskTemplateDialog : Window
{
    /// <summary>Reads the ticket's template list; throws when it cannot be read.</summary>
    public delegate Task<IReadOnlyList<TaskTemplateChoice>> CatalogLoader(CancellationToken ct);

    private readonly TicketTaskController _controller;
    private readonly int _ticketId;
    private readonly CatalogLoader _catalog;
    private CancellationTokenSource? _loadCts;
    private bool _busy;
    // From the re-read before applying until the apply answers: the dialog
    // cannot be closed, because what happened must be reported.
    private bool _applying;

    public TicketTaskSubmitOutcome? Outcome { get; private set; }

    public ApplyTaskTemplateDialog(TicketTaskController controller, int ticketId, CatalogLoader catalog)
    {
        InitializeComponent();
        _controller = controller;
        _ticketId = ticketId;
        _catalog = catalog;
        Title = $"Apply Task Template · Ticket #{ticketId}";
        StartDateCombo.ItemsSource = new[]
        {
            new KeyValuePair<TaskTemplateStartDate, string>(TaskTemplateStartDate.Current, "Now"),
            new KeyValuePair<TaskTemplateStartDate, string>(TaskTemplateStartDate.TicketCreated, "The ticket's created date"),
            new KeyValuePair<TaskTemplateStartDate, string>(TaskTemplateStartDate.TicketStart, "The ticket's start date (next operational day)"),
        };
        StartDateCombo.SelectedIndex = 0;
        Loaded += async (_, _) => await LoadCatalogAsync();
        Closing += (_, e) =>
        {
            if (_applying) e.Cancel = true;
            else _loadCts?.Cancel();
        };
    }

    private async Task LoadCatalogAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var previous = (TemplateCombo.SelectedItem as TaskTemplateChoice)?.Id;
        SetBusy(true, "Reading TeamDynamix's task templates for this ticket…");
        try
        {
            var choices = await _catalog(cts.Token);
            if (cts.IsCancellationRequested) return;
            TemplateCombo.ItemsSource = choices;
            TemplateCombo.SelectedItem = choices.FirstOrDefault(c => c.Id == previous);
            TemplateCombo.IsEnabled = choices.Count > 0;
            CatalogText.Text = choices.Count switch
            {
                0 => "TeamDynamix offers no task templates for this ticket.",
                1 => "1 template offered for this ticket.",
                _ => $"{choices.Count} templates offered for this ticket.",
            };
            if (choices.Count > 0 && TemplateCombo.SelectedItem == null) TemplateCombo.Focus();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            TemplateCombo.ItemsSource = null;
            TemplateCombo.IsEnabled = false;
            CatalogText.Text = ex is TdxTicketTaskException ? ex.Message : "The task template list could not be read.";
        }
        finally
        {
            if (!cts.IsCancellationRequested) SetBusy(false, "");
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        LoadProgress.IsActive = busy;
        LoadProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ReloadButton.IsEnabled = !busy;
        TemplateCombo.IsEnabled = !busy && TemplateCombo.Items.Count > 0;
        RemoveExistingBox.IsEnabled = !busy;
        StartDateCombo.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy && TemplateCombo.SelectedItem is TaskTemplateChoice;
        CancelButton.IsEnabled = !_applying;
        StatusText.Text = status;
    }

    private async void OnReload(object sender, RoutedEventArgs e) => await LoadCatalogAsync();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyButton.IsEnabled = !_busy && TemplateCombo.SelectedItem is TaskTemplateChoice;

    private void OnRemoveChanged(object sender, RoutedEventArgs e) =>
        RemoveWarning.Visibility = RemoveExistingBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_busy || TemplateCombo.SelectedItem is not TaskTemplateChoice template) return;
        var remove = RemoveExistingBox.IsChecked == true;
        var startDate = StartDateCombo.SelectedValue is TaskTemplateStartDate s ? s : TaskTemplateStartDate.Current;

        // Read the list again: only a template TeamDynamix still offers is applied.
        _applying = true;
        SetBusy(true, "Checking the template is still offered…");
        IReadOnlyList<TaskTemplateChoice> fresh;
        try
        {
            fresh = await _catalog(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _applying = false;
            SetBusy(false, ex is TdxTicketTaskException ? ex.Message : "The task template list could not be read.");
            return;
        }
        _applying = false;
        SetBusy(false, "");
        TemplateCombo.ItemsSource = fresh;
        TemplateCombo.SelectedItem = fresh.FirstOrDefault(c => c.Id == template.Id);
        if (TemplateCombo.SelectedItem == null)
        {
            StatusText.Text = $"“{template.Name}” is no longer offered for this ticket. Choose a template again.";
            return;
        }

        var prompt = $"Apply the task template “{template.Name}” to ticket #{_ticketId}?";
        if (remove)
            prompt += "\n\nThe ticket's existing tasks will be removed first. Removed tasks cannot be restored. "
                      + "TeamDynamix only removes them if you are allowed to delete tasks; otherwise they are kept.";
        var confirm = MessageBox.Show(this, prompt, remove ? "Remove tasks and apply template" : "Apply task template",
            MessageBoxButton.YesNo, remove ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        _applying = true;
        SetBusy(true, "Applying the template…");
        try
        {
            Outcome = await _controller.ApplyTemplateAsync(_ticketId, template, fresh, remove, startDate);
        }
        finally
        {
            _applying = false;
            SetBusy(false, "");
        }

        if (Outcome.Status == TicketTaskSubmitStatus.NotSent
            && _controller.Current is { Phase: TicketTasksPhase.Loaded } current && current.TicketId == _ticketId)
        {
            StatusText.Text = Outcome.Message;
            Outcome = null;
            return;
        }
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_applying) return;
        _loadCts?.Cancel();
        DialogResult = false;
    }
}
