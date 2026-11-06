namespace FleetMate.Core.Models.Tickets;

/// <summary>
/// The detail pane's unsaved field picks for one ticket, as FleetMate for Mac
/// holds them: Status, Priority, Classification, Form, Service, Type, Group
/// and Requestor, and the responsible person that Assign to me, Reassign and
/// Unassign choose, wait here until Save sends them as one PATCH, or Discard
/// drops them. A pick that puts a field back to the ticket's own value is no
/// longer an edit, so Save and Discard go away again.
/// </summary>
public sealed class TicketPendingEdits
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);
    private TdxTicket _baseline;

    // Assign to me, Reassign and Unassign. Held apart from the pickable
    // fields because clearing the responsible is a real change, where a blank
    // pick in a picker is not.
    private bool _responsibleHeld;
    private Guid? _responsibleUid;
    private string? _responsibleName;

    public TicketPendingEdits(TdxTicket ticket)
    {
        _baseline = ticket ?? throw new ArgumentNullException(nameof(ticket));
    }

    /// <summary>The ticket these edits belong to. They never follow the operator to another one.</summary>
    public int TicketId => _baseline.Id;

    /// <summary>True while at least one field differs from the ticket as loaded.</summary>
    public bool HasEdits => _values.Count > 0 || _responsibleHeld;

    /// <summary>
    /// The fields with an unsaved value, in the order TDX names them, with the
    /// responsible person last.
    /// </summary>
    public IReadOnlyList<string> Fields
    {
        get
        {
            var fields = TicketDetailFields.Pickable.Where(_values.ContainsKey).ToList();
            if (_responsibleHeld) fields.Add(TicketDetailFields.Responsible);
            return fields;
        }
    }

    /// <summary>True while Assign to me, Reassign or Unassign holds an unsaved responsible.</summary>
    public bool IsResponsibleEdited => _responsibleHeld;

    /// <summary>The responsible person as the pane should show it: the pending one, else the ticket's own.</summary>
    public Guid? ResponsibleUid => _responsibleHeld ? _responsibleUid : Person(_baseline.ResponsibleUid);

    /// <summary>The responsible person's name as the pane should show it; null when nobody is responsible.</summary>
    public string? ResponsibleName => _responsibleHeld
        ? _responsibleName
        : Person(_baseline.ResponsibleUid) == null ? null : _baseline.ResponsibleFullName;

    /// <summary>
    /// The name of a group Reassign chose, for the Group picker to show when
    /// its own list does not offer that group.
    /// </summary>
    public string? GroupName { get; private set; }

    /// <summary>
    /// Whether Save asks TeamDynamix to email the new responsible person. As on
    /// the Mac it starts ticked for every ticket, and only matters when Save
    /// changes the responsible person.
    /// </summary>
    public bool NotifyNewResponsible { get; set; } = true;

    /// <summary>
    /// The requestor's name for a pending Requestor pick, so the pane can
    /// show who was picked before the ticket is saved.
    /// </summary>
    public string? RequestorName { get; private set; }

    /// <summary>
    /// Hold <paramref name="value"/> for <paramref name="field"/>. A blank pick
    /// (no id, an empty uid) is ignored, since no picker offers one; a value
    /// equal to the ticket's own clears that field's edit.
    /// </summary>
    /// <returns>True when the set of pending edits changed.</returns>
    public bool Set(string field, object? value, string? requestorName = null)
    {
        if (!TicketDetailFields.Pickable.Contains(field))
            throw new ArgumentOutOfRangeException(nameof(field), field, "Not a pickable ticket field");

        if (TicketDetailFields.Change(_baseline, field, value) == null)
        {
            if (IsBlank(value)) return false;
            if (field == TicketDetailFields.Requestor) RequestorName = null;
            if (field == TicketDetailFields.Group) GroupName = null;
            return _values.Remove(field);
        }

        var changed = !_values.TryGetValue(field, out var held) || !Equals(held, value);
        _values[field] = value!;
        if (field == TicketDetailFields.Requestor) RequestorName = requestorName;
        return changed;
    }

    /// <summary>
    /// Hold <paramref name="uid"/> as the responsible person: Assign to me or a
    /// person picked in Reassign, or null for Unassign. Choosing the ticket's
    /// own responsible again clears the edit.
    /// </summary>
    /// <returns>True when the set of pending edits changed.</returns>
    public bool SetResponsible(Guid? uid, string? name)
    {
        var person = Person(uid);
        if (person == Person(_baseline.ResponsibleUid))
        {
            var was = _responsibleHeld;
            ForgetResponsible();
            return was;
        }

        var changed = !_responsibleHeld || _responsibleUid != person;
        _responsibleHeld = true;
        _responsibleUid = person;
        _responsibleName = person == null ? null : name;
        return changed;
    }

    /// <summary>
    /// Hold a group picked in Reassign. As TDX's own picker does, the group
    /// takes the ticket on its own: it becomes the responsible group and the
    /// responsible person is cleared.
    /// </summary>
    /// <returns>True when the set of pending edits changed.</returns>
    public bool AssignToGroup(int groupId, string? groupName)
    {
        if (groupId <= 0) return false;
        var changed = Set(TicketDetailFields.Group, groupId);
        GroupName = IsEdited(TicketDetailFields.Group) ? groupName : null;
        return SetResponsible(null, null) | changed;
    }

    /// <summary>
    /// Whether Save should pass notifyNewResponsible: the box is ticked and the
    /// PATCH gives the ticket a different responsible person than
    /// <paramref name="latest"/> holds, as the Mac decides it.
    /// </summary>
    public bool ShouldNotify(TdxTicket? latest = null) =>
        NotifyNewResponsible && _responsibleHeld && _responsibleUid != Person((latest ?? _baseline).ResponsibleUid);

    /// <summary>The field's value as the pane should show it: the pick, else the ticket's own.</summary>
    public object? Value(string field) =>
        _values.TryGetValue(field, out var value) ? value : TicketDetailFields.Current(_baseline, field);

    /// <summary>True when <paramref name="field"/> holds an unsaved pick.</summary>
    public bool IsEdited(string field) => _values.ContainsKey(field);

    /// <summary>
    /// The one PATCH Save sends: every pending field that still differs from
    /// <paramref name="latest"/>, the ticket as TeamDynamix holds it now, as
    /// the Mac compares against a fresh read. Null when nothing differs.
    /// </summary>
    public Dictionary<string, object?>? Patch(TdxTicket? latest = null)
    {
        var basis = latest ?? _baseline;
        if (basis.Id != _baseline.Id)
            throw new ArgumentException("The ticket is not the one these edits were made on.", nameof(latest));

        var patch = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in TicketDetailFields.Pickable.Where(_values.ContainsKey))
        {
            if (TicketDetailFields.Change(basis, field, _values[field]) is { } change)
                foreach (var (key, value) in change) patch[key] = value;
        }
        // TDX clears the responsible on an empty UID, as the board drop does.
        if (_responsibleHeld && _responsibleUid != Person(basis.ResponsibleUid))
            patch[TicketDetailFields.Responsible] = _responsibleUid?.ToString() ?? "";
        return patch.Count == 0 ? null : patch;
    }

    /// <summary>
    /// Follow a newer copy of the same ticket, after another change saved
    /// (a description, an assignment) or a re-read of the open ticket. The
    /// picks stay; any the ticket now already holds stop being edits.
    /// </summary>
    public void Rebase(TdxTicket ticket)
    {
        if (ticket.Id != _baseline.Id)
            throw new ArgumentException("The ticket is not the one these edits were made on.", nameof(ticket));
        _baseline = ticket;
        foreach (var field in _values.Keys.ToList())
        {
            if (TicketDetailFields.Change(ticket, field, _values[field]) == null)
            {
                _values.Remove(field);
                if (field == TicketDetailFields.Requestor) RequestorName = null;
                if (field == TicketDetailFields.Group) GroupName = null;
            }
        }
        if (_responsibleHeld && _responsibleUid == Person(ticket.ResponsibleUid)) ForgetResponsible();
    }

    /// <summary>Drop every pick: Discard, a successful Save, or another ticket opened.</summary>
    public void Clear()
    {
        _values.Clear();
        RequestorName = null;
        GroupName = null;
        ForgetResponsible();
        NotifyNewResponsible = true;
    }

    private void ForgetResponsible()
    {
        _responsibleHeld = false;
        _responsibleUid = null;
        _responsibleName = null;
    }

    // An empty UID is how TDX says nobody is responsible.
    private static Guid? Person(Guid? uid) => uid is { } u && u != Guid.Empty ? u : null;

    private static bool IsBlank(object? value) => value switch
    {
        null => true,
        int id => id <= 0,
        Guid uid => uid == Guid.Empty,
        _ => false,
    };
}
