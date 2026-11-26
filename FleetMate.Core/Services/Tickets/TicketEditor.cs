using FleetMate.Core.Models.Tickets;

namespace FleetMate.Core.Services.Tickets;

/// <summary>What an edit to a ticket came to: the ticket as TeamDynamix now holds it, and what to tell the person.</summary>
/// <param name="GroupChanged">A save sent a new group, which brings new members to People and the notify list.</param>
public sealed record TicketEditOutcome(TdxTicket? Updated, string Message, bool Succeeded, bool GroupChanged = false)
{
    public static TicketEditOutcome Failed(string message) => new(null, message, false);
}

/// <summary>
/// The detail pane's writes, shared by FleetMate.GUI and the WinUI shell:
/// Save of the held field picks, the description, and a ticket's parent.
/// Each reads or checks what it must first, so nothing is sent twice and a
/// value already in place is not written again.
/// </summary>
public static class TicketEditor
{
    /// <summary>
    /// Send the held picks in one update, against the ticket as TeamDynamix
    /// holds it now. As on the Mac, the new responsible is emailed only when
    /// the box is ticked and Save really changes who is responsible. Picks
    /// made while the save ran are newer work and stay held.
    /// </summary>
    public static async Task<TicketEditOutcome> SaveAsync(TdxService service, TdxTicket ticket, TicketPendingEdits edits)
    {
        try
        {
            var latest = await service.GetTicketAsync(ticket.Id) ?? ticket;
            var patch = edits.Patch(latest);
            var notify = patch != null && edits.ShouldNotify(latest);
            var updated = patch == null ? latest : await service.UpdateTicketAsync(ticket.Id, patch, notifyNewResponsible: notify);
            if (updated == null) return TicketEditOutcome.Failed("TeamDynamix did not accept the changes. See the log for the response.");
            if (edits.Patch(updated) == null) edits.Clear();
            return new TicketEditOutcome(updated,
                patch == null ? $"#{ticket.Id} already had these values"
                : notify ? $"Saved #{ticket.Id} and notified the new responsible"
                : $"Saved #{ticket.Id}",
                true,
                patch?.ContainsKey(TicketDetailFields.Group) == true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return TicketEditOutcome.Failed($"Could not save #{ticket.Id}: {ex.Message}");
        }
    }

    /// <summary>Save an edited description; null when the text is unchanged and nothing needs sending.</summary>
    public static async Task<TicketEditOutcome?> SaveDescriptionAsync(TdxService service, TdxTicket ticket, string edited)
    {
        var update = TicketDetailFields.DescriptionChange(TicketFeedView.PlainText(ticket.Description), edited);
        if (update == null) return null;
        var updated = await service.UpdateTicketAsync(ticket.Id, update);
        return updated == null
            ? TicketEditOutcome.Failed("TeamDynamix did not accept the description. See the log for the response.")
            : new TicketEditOutcome(updated, $"Saved the description of #{ticket.Id}", true);
    }

    /// <summary>
    /// Read a parent typed as "#123", "123" or nothing (clear). The error says
    /// why the text cannot be a parent; null when it can.
    /// </summary>
    public static (int? ParentId, string? Error) ParseParent(string? text, int childId)
    {
        var trimmed = (text ?? "").Trim().TrimStart('#');
        if (trimmed.Length == 0) return (null, null);
        if (!int.TryParse(trimmed, out var parsed) || parsed <= 0) return (null, "Enter a ticket number.");
        if (parsed == childId) return (null, "A ticket can't be its own parent.");
        return (parsed, null);
    }

    /// <summary>Make <paramref name="parentId"/> the ticket's parent, or clear it with null.</summary>
    public static async Task<TicketEditOutcome> SetParentAsync(TdxService service, int ticketId, int? parentId)
    {
        var updated = await service.SetParentAsync(ticketId, parentId);
        return updated == null
            ? TicketEditOutcome.Failed("TeamDynamix did not accept the parent. See the log for the response.")
            : new TicketEditOutcome(updated, parentId == null ? "Parent cleared" : $"Parent set to #{parentId}", true);
    }

    /// <summary>
    /// TeamDynamix has no route that creates a parent: create a ticket with
    /// the child's queue fields, then make it the child's parent. The created
    /// ticket comes back even when linking it fails, so it can be listed.
    /// </summary>
    public static async Task<(TdxTicket? Parent, TicketEditOutcome Outcome)> CreateParentAsync(TdxService service, TdxTicket child, string title)
    {
        var parent = await service.CreateTicketAsync(ParentTicketRequest.For(child, title.Trim()));
        if (parent == null) return (null, TicketEditOutcome.Failed("TeamDynamix did not create the parent. See the log for the response."));
        var updated = await service.SetParentAsync(child.Id, parent.Id);
        return updated == null
            ? (parent, TicketEditOutcome.Failed($"Created #{parent.Id}, but could not set it as the parent."))
            : (parent, new TicketEditOutcome(updated, $"Created parent #{parent.Id}", true));
    }

    /// <summary>
    /// Post a comment and say what happened: how many files went with it and
    /// how many people were notified. Throws <see cref="TdxCommentException"/>
    /// when TeamDynamix refuses it, so the text stays in the composer.
    /// </summary>
    public static async Task<string> PostCommentAsync(TdxService service, int ticketId, string text, bool isPrivate,
        IReadOnlyList<string> notify, int attachedFiles = 0)
    {
        await service.AddCommentAsync(ticketId, text, isPrivate, notify: notify.ToList());
        var files = attachedFiles == 0 ? "" : attachedFiles == 1 ? " with 1 file" : $" with {attachedFiles} files";
        return notify.Count == 0
            ? $"Comment added{files}"
            : $"Comment added{files}; notified {notify.Count} {(notify.Count == 1 ? "person" : "people")}";
    }
}
