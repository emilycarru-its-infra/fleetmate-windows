using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>A predecessor choice; Id 0 is "None".</summary>
public sealed record PredecessorChoice(int Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Add a standard task to a ticket, or edit one: title, plain description,
/// estimate, operational duration, start and end, responsible person (search,
/// Assign to me, Clear) and group (search, Clear), the notify choice and the
/// predecessor. The dialog sends through <see cref="TicketTaskController"/>,
/// which reads the task again before an edit and refuses to send over a
/// change made since the dialog opened. It stays open only when nothing was
/// sent and what it shows is still current; any sent or uncertain change
/// closes it so the page reloads before anything can be retried.
/// </summary>
public partial class TicketTaskEditorDialog : Window
{
    private readonly TicketTaskController _controller;
    private readonly int _ticketId;
    private readonly TdxTicketTask? _existing;
    private readonly TicketTaskWrite _original;
    private Guid? _personUid;
    private int _groupId;
    private CancellationTokenSource? _personSearch;
    private CancellationTokenSource? _groupSearch;
    private bool _saving;

    /// <summary>What happened, when something was attempted; null when the dialog was cancelled.</summary>
    public TicketTaskSubmitOutcome? Outcome { get; private set; }

    public TicketTaskEditorDialog(TicketTaskController controller, int ticketId, TdxTicketTask? existing)
    {
        InitializeComponent();
        _controller = controller;
        _ticketId = ticketId;
        _existing = existing;
        _original = existing == null ? new TicketTaskWrite() : TicketTaskWrite.From(existing);

        Title = existing == null ? $"New Task · Ticket #{ticketId}" : $"Edit Task · Ticket #{ticketId}";
        HeadingText.Text = existing == null ? "New Task" : $"Edit “{existing.DisplayTitle}”";
        SaveButton.Content = existing == null ? "Add Task" : "Save";

        TitleBox.Text = _original.Title;
        DescriptionBox.Text = TicketTaskFormat.PlainText(_original.Description);
        EstimateBox.Text = _original.EstimatedMinutes.ToString(CultureInfo.InvariantCulture);
        WithinBox.Text = _original.CompleteWithinMinutes?.ToString(CultureInfo.InvariantCulture) ?? "";
        SetDate(StartDatePicker, StartTimeBox, _original.StartDate);
        SetDate(EndDatePicker, EndTimeBox, _original.EndDate);
        NotifyBox.IsChecked = _original.NotifyResponsible;
        _personUid = _original.ResponsibleUid;
        PersonPicked.Text = _personUid == null ? "Nobody" : existing?.ResponsibleFullName ?? "Assigned";
        _groupId = _original.ResponsibleGroupId;
        GroupPicked.Text = _groupId > 0 ? existing?.ResponsibleGroupName ?? $"Group {_groupId}" : "No group";
        EstimateBox.TextChanged += (_, _) => UpdateEstimateHint();
        WithinBox.TextChanged += (_, _) => UpdateEstimateHint();
        UpdateEstimateHint();

        Loaded += async (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
            await LoadPredecessorsAsync();
        };
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
    }

    private async Task LoadPredecessorsAsync()
    {
        var none = new PredecessorChoice(0, "None");
        PredecessorCombo.ItemsSource = new[] { none };
        PredecessorCombo.SelectedIndex = 0;
        PredecessorCombo.IsEnabled = false;
        try
        {
            var tasks = await _controller.LoadPredecessorChoicesAsync(_ticketId, _existing?.Id);
            var choices = new List<PredecessorChoice> { none };
            choices.AddRange(tasks.Select(t => new PredecessorChoice(t.Id, t.DisplayTitle)));
            // The current predecessor stays a choice even when TeamDynamix no
            // longer lists it as eligible, so the editor never silently drops it.
            if (_original.PredecessorId > 0 && choices.All(c => c.Id != _original.PredecessorId))
                choices.Add(new PredecessorChoice(_original.PredecessorId, _existing?.PredecessorTitle ?? $"Task {_original.PredecessorId}"));
            PredecessorCombo.ItemsSource = choices;
            PredecessorCombo.SelectedItem = choices.First(c => c.Id == _original.PredecessorId);
            PredecessorCombo.IsEnabled = true;
        }
        catch (Exception ex)
        {
            // Without the list the predecessor cannot be changed, but the one
            // the task has is kept as it is.
            if (_original.PredecessorId > 0)
            {
                var kept = new PredecessorChoice(_original.PredecessorId, _existing?.PredecessorTitle ?? $"Task {_original.PredecessorId}");
                PredecessorCombo.ItemsSource = new[] { kept };
                PredecessorCombo.SelectedIndex = 0;
            }
            PredecessorHint.Text = ex is TdxTicketTaskException ? $"Predecessors could not be read: {ex.Message}" : "Predecessors could not be read.";
        }
    }

    private void UpdateEstimateHint()
    {
        var parts = new List<string>();
        if (int.TryParse(EstimateBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var est) && est > 0)
            parts.Add($"Estimate {TicketTaskFormat.Minutes(est)}");
        if (int.TryParse(WithinBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var within) && within > 0)
            parts.Add($"within {TicketTaskFormat.Minutes(within)} of operational time");
        EstimateHint.Text = string.Join(", ", parts);
    }

    // ── Dates ──────────────────────────────────────────────────────────

    private static void SetDate(DatePicker picker, TextBox time, DateTimeOffset? value)
    {
        if (value is { } date)
        {
            var local = date.ToLocalTime();
            picker.SelectedDate = local.Date;
            time.Text = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        else
        {
            picker.SelectedDate = null;
            time.Text = "";
        }
    }

    /// <summary>
    /// The date the picker and time box describe, or null when no date is
    /// picked. Unchanged to the minute, the original value is kept exactly,
    /// so opening and saving does not rewrite a date. A blank time is the
    /// start of the day for a start and its last minute for an end.
    /// </summary>
    internal static bool TryReadDate(DateTime? day, string timeText, DateTimeOffset? original, bool isEnd,
        out DateTimeOffset? value, out string? error)
    {
        value = null;
        error = null;
        if (day == null) return true;

        TimeSpan time;
        var text = timeText.Trim();
        if (text.Length == 0)
        {
            time = isEnd ? new TimeSpan(23, 59, 0) : TimeSpan.Zero;
        }
        else if (!TimeSpan.TryParseExact(text, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out time)
                 || time >= TimeSpan.FromDays(1))
        {
            error = $"“{text}” is not a 24-hour time like 09:30.";
            return false;
        }

        var local = day.Value.Date + time;
        if (original is { } o)
        {
            var was = o.ToLocalTime();
            if (was.Date == local.Date && was.Hour == local.Hour && was.Minute == local.Minute)
            {
                value = o;
                return true;
            }
        }
        value = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local));
        return true;
    }

    private void OnClearStart(object sender, RoutedEventArgs e) => SetDate(StartDatePicker, StartTimeBox, null);
    private void OnClearEnd(object sender, RoutedEventArgs e) => SetDate(EndDatePicker, EndTimeBox, null);

    // ── Responsibility ─────────────────────────────────────────────────

    private async void OnPersonSearchChanged(object sender, TextChangedEventArgs e)
    {
        var text = PersonBox.Text.Trim();
        _personSearch?.Cancel();
        if (text.Length < 2)
        {
            PersonResults.Visibility = Visibility.Collapsed;
            return;
        }
        var cts = _personSearch = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token);
            var people = await _controller.Api.SearchPeopleAsync(text, cts.Token);
            if (cts.IsCancellationRequested) return;
            PersonResults.ItemsSource = people.Where(p => p.Uid != null).ToList();
            PersonResults.Visibility = PersonResults.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (TdxTicketTaskException ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void OnPersonPicked(object sender, SelectionChangedEventArgs e)
    {
        if (PersonResults.SelectedItem is not TdxPerson { Uid: { } uid } person) return;
        SetPerson(uid, person);
    }

    private void SetPerson(Guid? uid, TdxPerson? person)
    {
        _personUid = uid;
        PersonPicked.Text = person == null ? "Nobody"
            : string.IsNullOrWhiteSpace(person.PrimaryEmail) ? person.DisplayName : $"{person.DisplayName} · {person.PrimaryEmail}";
        PersonResults.Visibility = Visibility.Collapsed;
        PersonBox.Text = "";
    }

    private async void OnAssignMe(object sender, RoutedEventArgs e)
    {
        AssignMeButton.IsEnabled = false;
        try
        {
            var me = await _controller.Api.GetMeAsync();
            if (me?.Uid is { } uid) SetPerson(uid, me);
            else StatusText.Text = "No TeamDynamix person matches your sign-in exactly, so nobody was assigned.";
        }
        catch (TdxTicketTaskException ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            AssignMeButton.IsEnabled = true;
        }
    }

    private void OnClearPerson(object sender, RoutedEventArgs e) => SetPerson(null, null);

    private async void OnGroupSearchChanged(object sender, TextChangedEventArgs e)
    {
        var text = GroupBox.Text.Trim();
        _groupSearch?.Cancel();
        if (text.Length < 2)
        {
            GroupResults.Visibility = Visibility.Collapsed;
            return;
        }
        var cts = _groupSearch = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token);
            var groups = await _controller.Api.SearchGroupsAsync(text, cts.Token);
            if (cts.IsCancellationRequested) return;
            GroupResults.ItemsSource = groups;
            GroupResults.Visibility = groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (TdxTicketTaskException ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void OnGroupPicked(object sender, SelectionChangedEventArgs e)
    {
        if (GroupResults.SelectedItem is not TdxGroup group) return;
        _groupId = group.Id;
        GroupPicked.Text = group.ToString();
        GroupResults.Visibility = Visibility.Collapsed;
        GroupBox.Text = "";
    }

    private void OnClearGroup(object sender, RoutedEventArgs e)
    {
        _groupId = 0;
        GroupPicked.Text = "No group";
    }

    // ── Save ───────────────────────────────────────────────────────────

    /// <summary>The form as task values, or null after saying what is wrong.</summary>
    private TicketTaskWrite? ReadForm()
    {
        if (!int.TryParse(EstimateBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var estimate))
        {
            StatusText.Text = "The estimate must be a whole number of minutes (0 or more).";
            return null;
        }
        int? within = null;
        if (WithinBox.Text.Trim().Length > 0)
        {
            if (!int.TryParse(WithinBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var w))
            {
                StatusText.Text = "Complete within must be a whole number of minutes, or blank for none.";
                return null;
            }
            within = w;
        }
        if (!TryReadDate(StartDatePicker.SelectedDate, StartTimeBox.Text, _original.StartDate, false, out var start, out var error)
            || !TryReadDate(EndDatePicker.SelectedDate, EndTimeBox.Text, _original.EndDate, true, out var end, out error))
        {
            StatusText.Text = error;
            return null;
        }

        // The description is edited as plain text; an untouched one keeps its
        // original markup rather than being flattened.
        var description = TicketTaskWrite.DescriptionFromEditor(_original.Description, DescriptionBox.Text);

        var values = new TicketTaskWrite
        {
            Title = TitleBox.Text.Trim(),
            Description = description,
            EstimatedMinutes = estimate,
            CompleteWithinMinutes = within,
            StartDate = start,
            EndDate = end,
            PredecessorId = (PredecessorCombo.SelectedItem as PredecessorChoice)?.Id ?? _original.PredecessorId,
            ResponsibleUid = _personUid,
            ResponsibleGroupId = _groupId,
            NotifyResponsible = NotifyBox.IsChecked == true,
        };
        if (values.Validate(_existing?.Id) is { } invalid)
        {
            StatusText.Text = invalid;
            return null;
        }
        return values;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        var values = ReadForm();
        if (values == null) return;

        _saving = true;
        SaveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        SaveProgress.IsActive = true;
        SaveProgress.Visibility = Visibility.Visible;
        StatusText.Text = _existing == null ? "Adding the task…" : "Reading the latest task, then saving…";
        TicketTaskSubmitOutcome outcome;
        try
        {
            outcome = _existing == null
                ? await _controller.CreateAsync(_ticketId, values)
                : await _controller.EditAsync(_ticketId, _existing.Id, _original, values);
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            SaveProgress.IsActive = false;
            SaveProgress.Visibility = Visibility.Collapsed;
        }

        // Nothing sent and the loaded tasks still current: the operator can
        // fix the form and try again here.
        if (outcome.Status == TicketTaskSubmitStatus.NotSent
            && _controller.Current is { Phase: TicketTasksPhase.Loaded } current && current.TicketId == _ticketId)
        {
            StatusText.Text = outcome.Message;
            return;
        }

        Outcome = outcome;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        DialogResult = false;
    }
}
