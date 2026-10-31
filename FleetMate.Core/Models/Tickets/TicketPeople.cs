using System.Net;

namespace FleetMate.Core.Models.Tickets;

/// <summary>A TeamDynamix request that failed, said in terms of what was being done.</summary>
public sealed class TdxRequestException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public TdxRequestException(string what, string detail, Exception? inner = null, HttpStatusCode? statusCode = null)
        : base($"Could not {what}: {detail}", inner)
    {
        StatusCode = statusCode;
    }
}

/// <summary>A person held in a ticket's custom attribute, labelled by that attribute.</summary>
public sealed record AttributePerson(string Label, TdxPerson Person);

/// <summary>
/// Someone a comment can notify: one person, or a group expanded to the
/// members who take its notifications. <see cref="Id"/> is the person's email
/// in lower case, or <c>group:{id}</c>.
/// </summary>
public sealed class NotifyRecipient
{
    public NotifyRecipient(string id, string name, IEnumerable<string> roles, IEnumerable<string> emails)
    {
        Id = id;
        Name = name;
        Roles = roles.ToList();
        Emails = emails.ToList();
    }

    public string Id { get; }
    public string Name { get; }
    public List<string> Roles { get; }
    public IReadOnlyList<string> Emails { get; }

    /// <summary>"Name (Requestor, Contact)".</summary>
    public string Label => Roles.Count == 0 ? Name : $"{Name} ({string.Join(", ", Roles)})";
}

/// <summary>Everyone a ticket comment can notify.</summary>
public static class TicketNotifyRecipients
{
    /// <summary>
    /// The responsible group first, then the requestor, responsible, contacts
    /// and people held in custom attributes, merged by email so a person who
    /// is both requestor and contact appears once with both roles.
    /// </summary>
    public static List<NotifyRecipient> Build(
        TdxTicket ticket,
        IEnumerable<TdxPerson>? groupMembers,
        IEnumerable<TdxPerson>? contacts,
        IEnumerable<AttributePerson>? attributePeople)
    {
        var recipients = new List<NotifyRecipient>();

        if (ticket.ResponsibleGroupId is > 0 && !string.IsNullOrWhiteSpace(ticket.ResponsibleGroupName))
        {
            var emails = (groupMembers ?? [])
                .Where(m => m.IsGroupNotified != false)
                .Select(m => Email(m.PrimaryEmail))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (emails.Count > 0)
                recipients.Add(new NotifyRecipient($"group:{ticket.ResponsibleGroupId}",
                    ticket.ResponsibleGroupName!, ["Responsible Group"], emails));
        }

        void Add(string? name, string? rawEmail, string role)
        {
            if (Email(rawEmail) is not { } email) return;
            var key = email.ToLowerInvariant();
            if (recipients.FirstOrDefault(r => r.Id == key) is { } existing)
            {
                if (!existing.Roles.Contains(role)) existing.Roles.Add(role);
                return;
            }
            var display = string.IsNullOrWhiteSpace(name) ? email : name!.Trim();
            recipients.Add(new NotifyRecipient(key, display, [role], [email]));
        }

        Add(ticket.RequestorName, ticket.RequestorEmail, "Requestor");
        Add(ticket.ResponsibleFullName, ticket.ResponsibleEmail, "Responsible");
        foreach (var person in contacts ?? [])
            Add(person.DisplayName, person.PrimaryEmail, "Contact");
        foreach (var entry in attributePeople ?? [])
            Add(entry.Person.DisplayName, entry.Person.PrimaryEmail, entry.Label);

        return recipients;
    }

    /// <summary>The emails of the ticked recipients, each address once.</summary>
    public static List<string> SelectedEmails(IEnumerable<NotifyRecipient> recipients, ICollection<string> selectedIds) =>
        recipients
            .Where(r => selectedIds.Contains(r.Id))
            .SelectMany(r => r.Emails)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The ticket's custom attributes that hold a person.</summary>
    public static IEnumerable<TdxAttribute> PersonAttributes(TdxTicket ticket) =>
        (ticket.Attributes ?? []).Where(a => a.PersonUid != null);

    private static string? Email(string? value) =>
        value?.Trim() is { Length: > 0 } trimmed && trimmed.Contains('@') ? trimmed : null;
}
