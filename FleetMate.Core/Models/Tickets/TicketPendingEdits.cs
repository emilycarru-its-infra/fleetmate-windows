namespace FleetMate.Core.Models.Tickets;

/// <summary>
/// The detail pane's unsaved field picks for one ticket, as FleetMate for Mac
/// holds them: Status, Priority, Classification, Form, Service, Type, Group
/// and Requestor wait here until Save sends them as one PATCH, or Discard
/// drops them. A pick that puts a field back to the ticket's own value is no
/// longer an edit, so Save and Discard go away again.
/// </summary>
public sealed class TicketPendingEdits
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);
    private TdxTicket _baseline;

    public TicketPendingEdits(TdxTicket ticket)
    {
        _baseline = ticket ?? throw new ArgumentNullException(nameof(ticket));
    }

    /// <summary>The ticket these edits belong to. They never follow the operator to another one.</summary>
    public int TicketId => _baseline.Id;

    /// <summary>True while at least one field differs from the ticket as loaded.</summary>
    public bool HasEdits => _values.Count > 0;

    /// <summary>The fields with an unsaved value, in the order TDX names them.</summary>
    public IReadOnlyList<string> Fields =>
        TicketDetailFields.Pickable.Where(_values.ContainsKey).ToList();

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
            return _values.Remove(field);
        }

        var changed = !_values.TryGetValue(field, out var held) || !Equals(held, value);
        _values[field] = value!;
        if (field == TicketDetailFields.Requestor) RequestorName = requestorName;
        return changed;
    }

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
        foreach (var field in Fields)
        {
            if (TicketDetailFields.Change(basis, field, _values[field]) is { } change)
                foreach (var (key, value) in change) patch[key] = value;
        }
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
            }
        }
    }

    /// <summary>Drop every pick: Discard, a successful Save, or another ticket opened.</summary>
    public void Clear()
    {
        _values.Clear();
        RequestorName = null;
    }

    private static bool IsBlank(object? value) => value switch
    {
        null => true,
        int id => id <= 0,
        Guid uid => uid == Guid.Empty,
        _ => false,
    };
}
