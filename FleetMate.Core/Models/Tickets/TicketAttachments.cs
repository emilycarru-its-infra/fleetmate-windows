namespace FleetMate.Core.Models.Tickets;

/// <summary>
/// How a ticket's attachments are listed and handled: their labels, a file
/// name that is safe to write, and which ones may be opened directly.
/// </summary>
public static class TicketAttachments
{
    /// <summary>
    /// Types that open in a viewer rather than run. Anything else (a script,
    /// an installer, an unknown type) can only be saved, because an attachment
    /// comes from whoever filed the ticket and opening it must never run it.
    /// </summary>
    private static readonly HashSet<string> OpenableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff",
        ".pdf", ".txt", ".log", ".csv",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".rtf",
        ".mp4", ".mov", ".m4a", ".mp3", ".wav",
    };

    public static string DisplayName(TdxAttachment attachment) =>
        string.IsNullOrWhiteSpace(attachment.Name) ? "Attachment" : attachment.Name.Trim();

    /// <summary>The name with any path or characters Windows rejects removed.</summary>
    public static string SafeFileName(TdxAttachment attachment)
    {
        var name = Path.GetFileName(DisplayName(attachment).Replace('\\', '/'));
        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        name = name.Trim().TrimEnd('.');
        return string.IsNullOrEmpty(name) ? "Attachment" : name;
    }

    public static bool CanOpen(TdxAttachment attachment) =>
        OpenableExtensions.Contains(Path.GetExtension(SafeFileName(attachment)));

    /// <summary>"240 KB · Uploader · Mar 4, 2026", leaving out what is unknown.</summary>
    public static string Subtitle(TdxAttachment attachment)
    {
        var parts = new List<string>();
        if (attachment.Size > 0) parts.Add(SizeLabel(attachment.Size));
        if (!string.IsNullOrWhiteSpace(attachment.CreatedFullName)) parts.Add(attachment.CreatedFullName.Trim());
        if (attachment.CreatedDate > DateTime.MinValue)
        {
            var created = attachment.CreatedDate.Kind == DateTimeKind.Utc ? attachment.CreatedDate.ToLocalTime() : attachment.CreatedDate;
            parts.Add(created.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture));
        }
        return string.Join(" · ", parts);
    }

    public static string SizeLabel(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };
}
