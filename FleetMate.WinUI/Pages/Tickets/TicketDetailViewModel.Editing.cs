using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using Serilog;

namespace FleetMate.WinUI.Pages.Tickets;

/// <summary>
/// Editing the open ticket, as on the Mac: field picks are held for Save
/// and Discard, assignment is held with them, and the description, parent
/// and comments are sent on their own. The rules are Core's
/// <see cref="TicketPendingEdits"/>, <see cref="TicketEditor"/> and
/// <see cref="TicketNotifyRecipients"/>; nothing is sent in demo mode.
/// </summary>
public sealed partial class TicketDetailViewModel
{
    private TicketReader? _reader;
    private TdxService? _service;
    private Func<TdxPerson?> _me = () => null;
    private TicketPendingEdits? _edits;
    private Task<Choices>? _choices;
    private int? _shownTicketId;
    private readonly Dictionary<int, TicketCommentDraft> _drafts = new();
    private readonly Dictionary<int, string> _replyDrafts = new();

    /// <summary>The service edits go to, and the signed-in person; null in demo mode, where nothing is sent.</summary>
    public void Attach(TdxService? service, Func<TdxPerson?> me)
    {
        _service = service;
        _me = me;
        OnPropertyChanged(nameof(CanEdit));
    }

    /// <summary>The pane can send changes: a service is there and not in demo mode.</summary>
    public bool CanEdit => _service != null;

    /// <summary>An edit landed; the page puts the ticket in the cache and redraws the list.</summary>
    public event Action<TdxTicket>? Edited;

    /// <summary>Something to tell the person: what was saved, or why it was not.</summary>
    public event Action<string, bool>? Reported;

    private void Report(string message, bool error = false) => Reported?.Invoke(message, error);

    // MARK: - Switching tickets

    /// <summary>
    /// Another ticket takes the pane: its unsent comment is stashed and comes
    /// back with it, and unsaved picks belong to their ticket and are dropped,
    /// as on the Mac. The same ticket again keeps both.
    /// </summary>
    private void SwitchTo(TdxTicket? ticket)
    {
        if (ticket?.Id == _shownTicketId) return;
        StashDraft();
        _edits = ticket == null ? null : new TicketPendingEdits(ticket);
        _shownTicketId = ticket?.Id;
        IsEditingDescription = false;
        var draft = ticket != null ? _drafts.GetValueOrDefault(ticket.Id) ?? TicketCommentDraft.Empty : TicketCommentDraft.Empty;
        _restoringDraft = true;
        CommentText = draft.Text;
        IsPrivate = draft.IsPrivate;
        _restoringDraft = false;
        _pendingNotifyIds = draft.NotifyIds.ToHashSet();
        IsComposeOpen = false;
        ReplyingTo = null;
        Recipients.Clear();
        RaiseEdits();
    }

    /// <summary>A newer copy of the open ticket arrived: the held picks follow it, the pickers and recipients redraw.</summary>
    private void Followed(TdxTicket ticket)
    {
        if (_edits?.TicketId == ticket.Id) _edits.Rebase(ticket); else _edits = new TicketPendingEdits(ticket);
        BuildRecipients(ticket);
        RaiseEdits();
        _ = FillPickersAsync(ticket);
    }

    private void StashDraft()
    {
        if (_shownTicketId is not { } previous) return;
        var draft = CurrentDraft();
        if (draft.IsEmpty) _drafts.Remove(previous); else _drafts[previous] = draft;
    }

    private TicketCommentDraft CurrentDraft() =>
        new(CommentText, IsPrivate, Recipients.Where(r => r.IsChecked).Select(r => r.Recipient.Id).ToArray());

    // MARK: - Field pickers

    public FieldPicker StatusPicker { get; } = new(TicketDetailFields.Status);
    public FieldPicker PriorityPicker { get; } = new(TicketDetailFields.Priority);
    public FieldPicker ClassificationPicker { get; } = new(TicketDetailFields.Classification);
    public FieldPicker FormPicker { get; } = new(TicketDetailFields.Form);
    public FieldPicker ServicePicker { get; } = new(TicketDetailFields.Service);
    public FieldPicker TypePicker { get; } = new(TicketDetailFields.Type);
    public FieldPicker GroupPicker { get; } = new(TicketDetailFields.Group);

    private IEnumerable<FieldPicker> Pickers =>
        [StatusPicker, PriorityPicker, ClassificationPicker, FormPicker, ServicePicker, TypePicker, GroupPicker];

    private sealed record Choices(
        IReadOnlyDictionary<int, string> Statuses, IReadOnlyDictionary<int, string> Priorities,
        IReadOnlyDictionary<int, string> Types, IReadOnlyDictionary<int, string> Forms,
        IReadOnlyDictionary<int, string> Services, IReadOnlyDictionary<int, string> Groups);

    private static readonly Choices None = new(
        new Dictionary<int, string>(), new Dictionary<int, string>(), new Dictionary<int, string>(),
        new Dictionary<int, string>(), new Dictionary<int, string>(), new Dictionary<int, string>());

    /// <summary>TeamDynamix's choices, read once. A list that cannot be read leaves the ticket's own value as the only choice.</summary>
    private Task<Choices> LoadChoicesAsync()
    {
        if (_service is not { } service) return Task.FromResult(None);
        return _choices ??= Load();

        async Task<Choices> Load()
        {
            try
            {
                var statuses = service.GetStatusesAsync();
                var priorities = service.GetPrioritiesAsync();
                var types = service.GetTypesAsync();
                var forms = service.GetFormsAsync();
                var services = service.GetServicesAsync();
                var groups = service.GetGroupsAsync();
                await Task.WhenAll(statuses, priorities, types, forms, services, groups);
                return new(statuses.Result, priorities.Result, types.Result, forms.Result, services.Result, groups.Result);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[tickets] Could not read the ticket field choices");
                _choices = null;
                return None;
            }
        }
    }

    /// <summary>
    /// Fill each picker with TeamDynamix's choices and select the ticket's
    /// value, or the held pick. The ticket's own value is always a choice, so
    /// a picker never reads blank.
    /// </summary>
    private async Task FillPickersAsync(TdxTicket ticket)
    {
        var choices = await LoadChoicesAsync();
        if (Ticket?.Id != ticket.Id) return;
        var classes = TdxClassification.All.ToDictionary(c => c.Key, c => c.Value);
        StatusPicker.Fill(choices.Statuses, ticket.StatusId, ticket.StatusName);
        PriorityPicker.Fill(choices.Priorities, ticket.PriorityId, ticket.PriorityName);
        ClassificationPicker.Fill(classes, ticket.Classification, ticket.ClassificationName ?? TdxClassification.Name(ticket.Classification));
        FormPicker.Fill(choices.Forms, ticket.FormId, ticket.FormName);
        ServicePicker.Fill(choices.Services, ticket.ServiceId, ticket.ServiceName);
        TypePicker.Fill(choices.Types, ticket.TypeId, ticket.TypeName);
        GroupPicker.Fill(choices.Groups, ticket.ResponsibleGroupId, ticket.ResponsibleGroupName);
        foreach (var picker in Pickers)
        {
            picker.CanPick = CanEdit;
            picker.Picked = OnPicked;
            if (_edits is { } edits && edits.TicketId == ticket.Id && edits.IsEdited(picker.Field))
                picker.Show(edits.Value(picker.Field) as int?, picker.Field == TicketDetailFields.Group ? edits.GroupName : null);
            picker.IsEdited = _edits?.IsEdited(picker.Field) == true;
        }
    }

    private void OnPicked(FieldPicker picker, int value)
    {
        if (Ticket is not { } ticket) return;
        if (_edits?.TicketId != ticket.Id) _edits = new TicketPendingEdits(ticket);
        _edits.Set(picker.Field, value);
        picker.IsEdited = _edits.IsEdited(picker.Field);
        RaiseEdits();
    }

    // MARK: - Save and Discard

    public bool HasEdits => _edits is { HasEdits: true } edits && edits.TicketId == Ticket?.Id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool IsSaving { get; private set; }

    public bool CanSave => HasEdits && !IsSaving;

    private void RaiseEdits()
    {
        OnPropertyChanged(nameof(HasEdits));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ShownResponsible));
        OnPropertyChanged(nameof(ResponsibleIsPending));
        OnPropertyChanged(nameof(CanAssignToMe));
        OnPropertyChanged(nameof(CanUnassign));
    }

    /// <summary>Save the held picks in one update; Ctrl+Enter does the same.</summary>
    [RelayCommand]
    public async Task SaveAsync()
    {
        if (IsSaving || Ticket is not { } ticket || _edits is not { HasEdits: true } edits || edits.TicketId != ticket.Id) return;
        if (_service is not { } service)
        {
            Report("Demo data can't be changed.");
            return;
        }
        IsSaving = true;
        try
        {
            Report($"Saving #{ticket.Id}…");
            var outcome = await TicketEditor.SaveAsync(service, ticket, edits);
            if (outcome.Updated is not { } updated)
            {
                Report(outcome.Message, error: true);
                return;
            }
            Apply(updated);
            Report(outcome.Message);
        }
        finally
        {
            IsSaving = false;
            RaiseEdits();
        }
    }

    /// <summary>Drop the held picks and show the ticket as TeamDynamix holds it.</summary>
    [RelayCommand]
    public void Discard()
    {
        _edits?.Clear();
        if (Ticket is { } ticket) _ = FillPickersAsync(ticket);
        RaiseEdits();
    }

    private void Apply(TdxTicket updated)
    {
        if (Ticket?.Id != updated.Id) return;
        Ticket = updated;
        Followed(updated);
        Edited?.Invoke(updated);
    }

    // MARK: - Responsible

    /// <summary>The responsible as Save will leave it.</summary>
    public string ShownResponsible
    {
        get
        {
            if (Ticket is not { } ticket) return "-";
            var edits = _edits?.TicketId == ticket.Id ? _edits : null;
            var uid = edits != null ? edits.ResponsibleUid : ticket.ResponsibleUid is { } u && u != Guid.Empty ? u : null;
            var name = edits != null ? edits.ResponsibleName : ticket.ResponsibleFullName;
            return uid == null ? TicketBoardLayout.Unassigned : name ?? "-";
        }
    }

    public bool ResponsibleIsPending => _edits?.TicketId == Ticket?.Id && _edits?.IsResponsibleEdited == true;

    public string ResponsibleToolTip => ResponsibleIsPending ? "Not saved yet. Save sends it with the other changes." : "";

    private Guid? HeldResponsibleUid => Ticket is not { } ticket ? null
        : _edits?.TicketId == ticket.Id ? _edits.ResponsibleUid
        : ticket.ResponsibleUid is { } u && u != Guid.Empty ? u : null;

    public bool CanAssignToMe => CanEdit && Ticket != null && (_me()?.Uid is not { } me || me != HeldResponsibleUid);

    public bool CanUnassign => CanEdit && HeldResponsibleUid != null;

    [RelayCommand]
    private async Task AssignToMeAsync()
    {
        if (_service is not { } service || Ticket is null) return;
        var me = _me() ?? await service.GetMeAsync();
        if (me?.Uid is not { } uid || uid == Guid.Empty)
        {
            Report("Could not resolve your TeamDynamix person. Sign in to TeamDynamix first.", error: true);
            return;
        }
        HoldResponsible(uid, me.FullName ?? me.DisplayName);
    }

    [RelayCommand]
    private void Unassign() => HoldResponsible(null, null);

    private void HoldResponsible(Guid? uid, string? name)
    {
        if (Ticket is not { } ticket) return;
        if (_edits?.TicketId != ticket.Id) _edits = new TicketPendingEdits(ticket);
        _edits.SetResponsible(uid, name);
        RaiseEdits();
        OnPropertyChanged(nameof(ResponsibleToolTip));
    }

    /// <summary>The Reassign search's notify choice, kept with this ticket's held edits.</summary>
    public bool NotifyNewResponsible
    {
        get => _edits?.NotifyNewResponsible ?? true;
        set
        {
            if (Ticket is not { } ticket) return;
            if (_edits?.TicketId != ticket.Id) _edits = new TicketPendingEdits(ticket);
            _edits.NotifyNewResponsible = value;
            OnPropertyChanged();
        }
    }

    public const int MinimumSearch = 2;

    /// <summary>People and groups who can own tickets, current responsible first marked.</summary>
    public async Task<IReadOnlyList<ReassignCandidate>> SearchResponsiblesAsync(string query, CancellationToken ct)
    {
        if (_service is not { } service || query.Trim().Length < MinimumSearch) return Array.Empty<ReassignCandidate>();
        var found = await service.SearchEligibleResponsiblesAsync(query.Trim(), ct);
        var ticket = Ticket;
        var currentGroup = _edits?.TicketId == ticket?.Id ? _edits?.Value(TicketDetailFields.Group) as int? ?? ticket?.ResponsibleGroupId : ticket?.ResponsibleGroupId;
        return TdxEligibleAssignment.Ordered(found)
            .Where(a => a.ResponsibleUpdates() != null)
            .Select(a => new ReassignCandidate(a, a.IsCurrent(HeldResponsibleUid, currentGroup)))
            .ToList();
    }

    /// <summary>A person picked in Reassign becomes responsible; a group takes the ticket on its own, as TeamDynamix's picker does.</summary>
    public void PickResponsible(ReassignCandidate candidate)
    {
        if (Ticket is not { } ticket) return;
        if (_edits?.TicketId != ticket.Id) _edits = new TicketPendingEdits(ticket);
        if (candidate.Assignment.Uid is { } uid) HoldResponsible(uid, candidate.Assignment.DisplayName);
        else if (candidate.Assignment.GroupId is { } groupId)
        {
            _edits.AssignToGroup(groupId, candidate.Assignment.DisplayName);
            GroupPicker.Show(groupId, candidate.Assignment.DisplayName);
            GroupPicker.IsEdited = true;
            RaiseEdits();
        }
    }

    // MARK: - Description

    [ObservableProperty]
    public partial bool IsEditingDescription { get; set; }

    [ObservableProperty]
    public partial string DescriptionDraft { get; set; } = "";

    [RelayCommand]
    private void EditDescription()
    {
        DescriptionDraft = Description;
        IsEditingDescription = true;
    }

    [RelayCommand]
    private void CancelDescription() => IsEditingDescription = false;

    [RelayCommand]
    private async Task SaveDescriptionAsync()
    {
        if (Ticket is not { } ticket) return;
        if (_service is not { } service)
        {
            Report("Demo data can't be changed.");
            return;
        }
        var outcome = await TicketEditor.SaveDescriptionAsync(service, ticket, DescriptionDraft);
        if (outcome == null)
        {
            IsEditingDescription = false;
            return;
        }
        if (outcome.Updated is not { } updated)
        {
            Report(outcome.Message, error: true);
            return;
        }
        IsEditingDescription = false;
        Apply(updated);
        Report(outcome.Message);
    }

    // MARK: - Parent

    /// <summary>Set the parent from "#123", or clear it when empty; the result is the hint to show, or null when done.</summary>
    public async Task<string?> SetParentAsync(string text)
    {
        if (Ticket is not { } ticket) return null;
        var (parentId, error) = TicketEditor.ParseParent(text, ticket.Id);
        if (error != null) return error;
        if (_service is not { } service) return "Demo data can't be changed.";
        Report(parentId == null ? "Clearing the parent…" : $"Setting parent to #{parentId}…");
        var outcome = await TicketEditor.SetParentAsync(service, ticket.Id, parentId);
        if (outcome.Updated is { } updated) Apply(updated);
        Report(outcome.Message, !outcome.Succeeded);
        return null;
    }

    /// <summary>Create a parent with this ticket's queue fields; the result is the hint, or null when done.</summary>
    public async Task<string?> CreateParentAsync(string title, Action<TdxTicket> created)
    {
        if (Ticket is not { } child) return null;
        if (string.IsNullOrWhiteSpace(title)) return "Enter a title.";
        if (_service is not { } service) return "Demo data can't be changed.";
        Report("Creating the parent ticket…");
        var (parent, outcome) = await TicketEditor.CreateParentAsync(service, child, title);
        if (parent != null) created(parent);
        if (outcome.Updated is { } updated) Apply(updated);
        Report(outcome.Message, !outcome.Succeeded);
        return null;
    }

    // MARK: - Compose

    private bool _restoringDraft;
    private HashSet<string> _pendingNotifyIds = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPost), nameof(AddCommentLabel))]
    public partial string CommentText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsPrivate { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddCommentLabel))]
    public partial bool IsComposeOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPost))]
    public partial bool IsPosting { get; private set; }

    public bool CanPost => !IsPosting && !string.IsNullOrWhiteSpace(CommentText);

    /// <summary>"Add Comment (Draft)" while closed over unsent text, as on the Mac.</summary>
    public string AddCommentLabel => TicketsPageSync.AddCommentLabel(IsComposeOpen, CommentText);

    /// <summary>Everyone the comment can notify: the group, requestor, responsible, contacts and people in attributes.</summary>
    public ObservableCollection<NotifyChoice> Recipients { get; } = new();

    private void BuildRecipients(TdxTicket ticket)
    {
        var ticked = Recipients.Count > 0
            ? Recipients.Where(r => r.IsChecked).Select(r => r.Recipient.Id).ToHashSet()
            : _pendingNotifyIds;
        Recipients.Clear();
        foreach (var recipient in TicketNotifyRecipients.Build(ticket, null, null, null))
            Recipients.Add(new NotifyChoice(recipient, ticked.Contains(recipient.Id)));
        OnPropertyChanged(nameof(HasRecipients));
    }

    public bool HasRecipients => Recipients.Count > 0;

    [RelayCommand]
    private void ToggleCompose() => IsComposeOpen = !IsComposeOpen;

    /// <summary>Quote in New Comment: the entry quoted after any text already there. A private entry keeps the reply private.</summary>
    public void Quote(TicketFeedItem entry)
    {
        var quoted = TicketFeedView.Quote(entry.Author, entry.Body);
        CommentText = string.IsNullOrWhiteSpace(CommentText) ? quoted : CommentText.TrimEnd() + "\n\n" + quoted;
        if (entry.Entry.IsPrivate) IsPrivate = true;
        IsComposeOpen = true;
    }

    /// <summary>
    /// Post the comment. Only a success clears the box: the comment is the
    /// person's work, and losing it to a refused post is worse than the failure.
    /// </summary>
    [RelayCommand]
    public async Task PostCommentAsync()
    {
        if (!CanPost || Ticket is not { } ticket) return;
        if (_service is not { } service)
        {
            Report("Demo data can't be changed.");
            return;
        }
        var submitted = CurrentDraft();
        var notify = TicketNotifyRecipients.SelectedEmails(Recipients.Select(r => r.Recipient), submitted.NotifyIds.ToHashSet());
        IsPosting = true;
        try
        {
            Report("Adding comment…");
            Report(await TicketEditor.PostCommentAsync(service, ticket.Id, submitted.Text.Trim(), submitted.IsPrivate, notify));
            if (Ticket?.Id != ticket.Id)
            {
                if (_drafts.GetValueOrDefault(ticket.Id) is { } saved && saved.Matches(submitted)) _drafts.Remove(ticket.Id);
                return;
            }
            if (CurrentDraft().Matches(submitted))
            {
                _drafts.Remove(ticket.Id);
                CommentText = "";
                IsPrivate = false;
                foreach (var r in Recipients) r.IsChecked = false;
                IsComposeOpen = false;
            }
            await RefreshFeedAsync(ticket.Id);
        }
        catch (Exception ex)
        {
            Report(ex is TdxCommentException ? ex.Message : $"Error: {ex.Message}", error: true);
        }
        finally
        {
            IsPosting = false;
        }
    }

    // MARK: - Replies

    /// <summary>The thread whose reply composer is open; TeamDynamix threads are one level deep.</summary>
    [ObservableProperty]
    public partial TicketFeedItem? ReplyingTo { get; set; }

    [ObservableProperty]
    public partial string ReplyText { get; set; } = "";

    partial void OnReplyingToChanged(TicketFeedItem? oldValue, TicketFeedItem? newValue)
    {
        if (oldValue != null) FeedReplyDrafts.Keep(_replyDrafts, oldValue.Entry.Id, ReplyText);
        ReplyText = newValue != null ? _replyDrafts.GetValueOrDefault(newValue.Entry.Id) ?? "" : "";
    }

    public void OpenReply(TicketFeedItem thread) => ReplyingTo = thread;

    public void CancelReply() => ReplyingTo = null;

    public async Task PostReplyAsync()
    {
        if (ReplyingTo is not { } thread || string.IsNullOrWhiteSpace(ReplyText) || Ticket is not { } ticket) return;
        if (_service is not { } service)
        {
            Report("Demo data can't be changed.");
            return;
        }
        var text = ReplyText;
        try
        {
            await service.ReplyToFeedEntryAsync(thread.Entry.Id, text);
            FeedReplyDrafts.Posted(_replyDrafts, thread.Entry.Id, text);
            if (ReferenceEquals(ReplyingTo, thread) && ReplyText == text)
            {
                _replyDrafts.Remove(thread.Entry.Id);
                ReplyingTo = null;
            }
            Report("Reply added");
            await RefreshFeedAsync(ticket.Id);
        }
        catch (Exception ex)
        {
            Report(ex is TdxFeedReplyException ? ex.Message : $"Couldn't reply: {ex.Message}", error: true);
        }
    }

    /// <summary>Only the signed-in person's own replies can be deleted, as on the Mac.</summary>
    public bool CanDelete(TicketFeedItem reply) =>
        CanEdit && reply.IsReply && TdxFeedReplyRequest.CanDelete(reply.Entry.CreatedUid, _me()?.Uid);

    public async Task DeleteReplyAsync(TicketFeedItem thread, TicketFeedItem reply)
    {
        if (_service is not { } service || Ticket is not { } ticket) return;
        try
        {
            await service.DeleteFeedReplyAsync(thread.Entry.Id, reply.Entry.Id);
            Report("Reply deleted");
            await RefreshFeedAsync(ticket.Id);
        }
        catch (Exception ex)
        {
            Report(ex is TdxFeedReplyException ? ex.Message : $"Couldn't delete the reply: {ex.Message}", error: true);
        }
    }

    /// <summary>Read the open ticket's activity again; it paints only if no other open started meanwhile.</summary>
    private async Task RefreshFeedAsync(int ticketId)
    {
        if (_reader is not { } reader || _loads.Current is not { } load || load.TicketId != ticketId) return;
        var feed = await reader.Feed(ticketId);
        if (!_loads.IsCurrent(load)) return;
        _feed = feed;
        ShowFeed();
    }
}

/// <summary>One detail field picker: TeamDynamix's choices, the ticket's value, and whether a pick is held.</summary>
public sealed partial class FieldPicker : ObservableObject
{
    private bool _filling;

    public FieldPicker(string field) => Field = field;

    public string Field { get; }
    public string Label => TicketDetailFields.Label(Field);

    /// <summary>Called with a pick the person made, not one shown from the ticket.</summary>
    public Action<FieldPicker, int>? Picked { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    public partial IReadOnlyList<KeyValuePair<int, string>> Choices { get; private set; } = Array.Empty<KeyValuePair<int, string>>();

    [ObservableProperty]
    public partial object? SelectedValue { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    public partial bool CanPick { get; set; }

    /// <summary>A pick is held for Save; the picker says "not saved yet".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingText))]
    public partial bool IsEdited { get; set; }

    public string PendingText => IsEdited ? "Not saved yet" : "";

    public bool IsEnabled => CanPick && Choices.Count > 0;

    partial void OnSelectedValueChanged(object? value)
    {
        if (!_filling && value is int picked) Picked?.Invoke(this, picked);
    }

    public void Fill(IReadOnlyDictionary<int, string> options, int? current, string? currentName)
    {
        _filling = true;
        Choices = TicketDetailFields.Choices(options, current, currentName, Label);
        SelectedValue = current is int id && id > 0 ? id : null;
        _filling = false;
    }

    /// <summary>Show a held value, adding it as a choice when the list does not offer it.</summary>
    public void Show(int? value, string? name)
    {
        _filling = true;
        if (value is int id && Choices.All(c => c.Key != id))
            Choices = TicketDetailFields.Choices(Choices.ToDictionary(c => c.Key, c => c.Value), id, name, Label);
        SelectedValue = value;
        _filling = false;
    }
}

/// <summary>One person a comment can notify, with its box.</summary>
public sealed partial class NotifyChoice : ObservableObject
{
    public NotifyChoice(NotifyRecipient recipient, bool isChecked)
    {
        Recipient = recipient;
        _isChecked = isChecked;
    }

    public NotifyRecipient Recipient { get; }
    public string Label => Recipient.Label;
    public string Emails => string.Join(", ", Recipient.Emails);

    private bool _isChecked;

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }
}

/// <summary>One person or group in the Reassign search.</summary>
public sealed class ReassignCandidate
{
    public ReassignCandidate(TdxEligibleAssignment assignment, bool isCurrent)
    {
        Assignment = assignment;
        IsCurrent = isCurrent;
    }

    public TdxEligibleAssignment Assignment { get; }
    public bool IsCurrent { get; }
    public string DisplayName => Assignment.DisplayName;
    public string Subtitle => Assignment.Subtitle;
    public string Glyph => Assignment.IsUser ? "" : "";
    public Microsoft.UI.Xaml.Visibility CurrentVisibility => IsCurrent ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string SpokenName => (Assignment.IsUser ? DisplayName : $"{DisplayName}, group") + (IsCurrent ? ", current responsible" : "");
}
