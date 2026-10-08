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

    /// <summary>
    /// Characters Windows rejects in a file name. Listed here rather than taken
    /// from <see cref="Path.GetInvalidFileNameChars"/>, which on other systems
    /// omits ':' (an alternate data stream on NTFS) and the rest.
    /// </summary>
    private static readonly char[] InvalidChars =
        "<>:\"/\\|?*".ToCharArray().Concat(Enumerable.Range(0, 32).Select(i => (char)i)).ToArray();

    /// <summary>Names Windows reserves for devices, with or without an extension.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// The exact name the file is written under, and so the only name its type
    /// is judged on. Any path is dropped, characters Windows rejects (':' among
    /// them, so no alternate stream can be named) become '_', and trailing dots
    /// and spaces go, because Windows strips them on write and "a.exe ." would
    /// otherwise be judged as one thing and saved as another.
    /// </summary>
    public static string SafeFileName(TdxAttachment attachment)
    {
        var name = DisplayName(attachment).Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        name = new string(name.Select(c => InvalidChars.Contains(c) ? '_' : c).ToArray());
        name = name.Trim().TrimEnd('.', ' ');
        if (string.IsNullOrEmpty(name)) return "Attachment";

        var stem = name.Split('.')[0].TrimEnd(' ');
        return ReservedNames.Contains(stem) ? "_" + name : name;
    }

    /// <summary>
    /// Deny by default: only a final extension on the allow list opens, judged
    /// on the name actually written. No extension, or one not listed, means
    /// save only.
    /// </summary>
    public static bool CanOpen(TdxAttachment attachment)
    {
        var name = SafeFileName(attachment);
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return false;
        return OpenableExtensions.Contains(name[dot..]);
    }

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
