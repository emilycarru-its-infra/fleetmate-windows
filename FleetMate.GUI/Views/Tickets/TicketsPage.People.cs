using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FleetMate.Core.Models.Tickets;
using Serilog;

namespace FleetMate.GUI.Views.Tickets;

// The People section of the detail pane: the ticket's contacts, with add and
// remove, and its responsible group. Contacts, group members and people held
// in custom attributes are also who a comment can notify.
public partial class TicketsPage
{
    private List<TdxPerson> _ticketContacts = new();
    private List<TdxPerson> _groupMembers = new();
    private List<AttributePerson> _attributePeople = new();
    private readonly Dictionary<int, List<TdxPerson>> _groupMemberCache = new();

    /// <summary>The ticket the people above were loaded for.</summary>
    private int? _ticketPeopleTicketId;
    private bool _peopleExpanded;
    private CancellationTokenSource? _contactSearchCts;

    /// <summary>
    /// Load contacts, responsible group members and attribute people for the
    /// open ticket. Each source fails on its own: a group this session cannot
    /// read still leaves the contacts usable.
    /// </summary>
    private async Task LoadTicketPeopleAsync(TdxTicket ticket)
    {
        if (_tdxService == null) return;
        var ticketId = ticket.Id;
        if (_ticketPeopleTicketId != ticketId)
        {
            _ticketContacts = new();
            _groupMembers = new();
            _attributePeople = new();
            ShowPeopleError(null);
            UpdatePeopleSection(ticket);
        }

        var contactsTask = _tdxService.GetTicketContactsAsync(ticketId);
        var membersTask = GroupMembersAsync(ticket.ResponsibleGroupId ?? 0);
        var attributesTask = Task.WhenAll(TicketNotifyRecipients.PersonAttributes(ticket).Select(async attribute =>
        {
            try
            {
                var person = await _tdxService.GetPersonAsync(attribute.PersonUid!.Value);
                return new AttributePerson(string.IsNullOrWhiteSpace(attribute.Name) ? "Contact" : attribute.Name!, person);
            }
            catch (TdxRequestException ex)
            {
                Log.Debug(ex, "A person-valued ticket attribute could not be resolved");
                return null;
            }
        }));

        List<TdxPerson>? contacts = null;
        string? error = null;
        try { contacts = await contactsTask; }
        catch (TdxRequestException ex) { error = ex.Message; }
        var members = await membersTask;
        var fromAttributes = (await attributesTask).OfType<AttributePerson>().ToList();

        if (_selectedTicket?.Id != ticketId) return;
        _ticketPeopleTicketId = ticketId;
        _ticketContacts = contacts ?? _ticketContacts;
        _groupMembers = members;
        _attributePeople = fromAttributes;
        ShowPeopleError(error);
        UpdatePeopleSection(_selectedTicket);
        UpdateNotifyOptions(_selectedTicket);
    }

    private async Task<List<TdxPerson>> GroupMembersAsync(int groupId)
    {
        if (groupId <= 0 || _tdxService == null) return new();
        if (_groupMemberCache.TryGetValue(groupId, out var cached)) return cached;
        try
        {
            var members = await _tdxService.GetGroupMembersAsync(groupId);
            _groupMemberCache[groupId] = members;
            return members;
        }
        catch (TdxRequestException ex)
        {
            Log.Debug(ex, "Responsible group members could not be read");
            return new();
        }
    }

    private void UpdatePeopleSection(TdxTicket ticket)
    {
        ContactsList.ItemsSource = _ticketContacts.ToList();
        NoContactsText.Visibility = _ticketContacts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (!string.IsNullOrWhiteSpace(ticket.ResponsibleGroupName))
        {
            ResourcesSection.Visibility = Visibility.Visible;
            ResourcesGroupText.Text = ticket.ResponsibleGroupName;
            var names = _groupMembers.Select(m => m.DisplayName).ToList();
            ResourcesGroupRow.ToolTip = names.Count > 0 ? string.Join(", ", names) : null;
        }
        else
        {
            ResourcesSection.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowPeopleError(string? message)
    {
        PeopleErrorText.Text = message ?? "";
        PeopleErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPeopleToggleClicked(object sender, RoutedEventArgs e)
    {
        _peopleExpanded = !_peopleExpanded;
        PeopleBody.Visibility = _peopleExpanded ? Visibility.Visible : Visibility.Collapsed;
        PeopleChevron.Text = _peopleExpanded ? "▾" : "▸";
        PeopleToggleButton.ToolTip = _peopleExpanded ? "Hide people" : "Show the ticket's contacts and group";
    }

    private async void OnContactSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_tdxService == null) return;
        _contactSearchCts?.Cancel();
        var cts = _contactSearchCts = new CancellationTokenSource();
        var query = ContactSearchBox.Text?.Trim() ?? "";

        if (query.Length < 2)
        {
            ContactResultsList.ItemsSource = null;
            ContactSearchPopup.IsOpen = false;
            return;
        }

        // One lookup per pause in typing, not one per keystroke.
        try { await Task.Delay(300, cts.Token); }
        catch (TaskCanceledException) { return; }

        ContactSearchStatusText.Text = "Searching…";
        ContactSearchPopup.IsOpen = true;
        var people = await _tdxService.SearchPeopleAsync(query, 15);
        if (cts.IsCancellationRequested) return;

        var pickable = people.Where(p => p.Uid is { } u && u != Guid.Empty).ToList();
        ContactResultsList.ItemsSource = pickable;
        ContactSearchStatusText.Text = pickable.Count == 0 ? "No matching people." : "Pick a person to add.";
    }

    private void OnContactSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && ContactResultsList.Items.Count > 0)
        {
            ContactSearchPopup.IsOpen = true;
            ContactResultsList.SelectedIndex = 0;
            (ContactResultsList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && ContactSearchPopup.IsOpen)
        {
            ContactSearchPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private async void OnContactResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ContactResultsList.SelectedItem is TdxPerson person)
        {
            e.Handled = true;
            await AddContactAsync(person);
        }
        else if (e.Key == Key.Escape)
        {
            ContactSearchPopup.IsOpen = false;
            ContactSearchBox.Focus();
            e.Handled = true;
        }
    }

    private async void OnContactResultPicked(object sender, MouseButtonEventArgs e)
    {
        if (ContactResultsList.SelectedItem is TdxPerson person) await AddContactAsync(person);
    }

    private async Task AddContactAsync(TdxPerson person)
    {
        ContactSearchPopup.IsOpen = false;
        ContactSearchBox.Text = "";
        if (_tdxService == null || _selectedTicket is not { } ticket || person.Uid is not { } uid) return;
        if (_ticketContacts.Any(c => c.Uid == uid)) return;

        ContactAddProgress.Visibility = Visibility.Visible;
        ContactAddProgress.IsActive = true;
        try
        {
            await _tdxService.AddTicketContactAsync(ticket.Id, uid);
            ShowPeopleError(null);
            List<TdxPerson> contacts;
            try { contacts = await _tdxService.GetTicketContactsAsync(ticket.Id); }
            catch (TdxRequestException) { contacts = _ticketContacts.Append(person).ToList(); }
            if (_selectedTicket?.Id != ticket.Id) return;
            _ticketContacts = contacts;
            UpdatePeopleSection(_selectedTicket);
            UpdateNotifyOptions(_selectedTicket);
        }
        catch (TdxRequestException ex)
        {
            if (_selectedTicket?.Id == ticket.Id) ShowPeopleError($"Couldn't add {person.DisplayName}. {ex.Message}");
        }
        finally
        {
            ContactAddProgress.IsActive = false;
            ContactAddProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnRemoveContactClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TdxPerson person }) return;
        if (_tdxService == null || _selectedTicket is not { } ticket || person.Uid is not { } uid) return;
        try
        {
            await _tdxService.RemoveTicketContactAsync(ticket.Id, uid);
            if (_selectedTicket?.Id != ticket.Id) return;
            ShowPeopleError(null);
            _ticketContacts = _ticketContacts.Where(c => c.Uid != uid).ToList();
            UpdatePeopleSection(_selectedTicket);
            UpdateNotifyOptions(_selectedTicket);
        }
        catch (TdxRequestException ex)
        {
            if (_selectedTicket?.Id == ticket.Id) ShowPeopleError($"Couldn't remove {person.DisplayName}. {ex.Message}");
        }
    }
}
