using System.Globalization;

namespace FleetMate.Core.Models.Tickets;

/// <summary>A file chosen for a comment and not yet uploaded.</summary>
public sealed record PendingCommentAttachment(string Path, string Name, long Size)
{
    public string SizeLabel => TicketAttachments.SizeLabel(Size);

    public static PendingCommentAttachment FromFile(string path)
    {
        var info = new FileInfo(path);
        return new PendingCommentAttachment(info.FullName, info.Name, info.Exists ? info.Length : 0);
    }
}

/// <summary>
/// How a comment's files reach the ticket. TDX cannot link a file to a feed
/// entry, so every file is uploaded to the ticket first and the comment is
/// posted only when all of them landed. Files that failed stay on the draft so
/// Post can be pressed again without posting the text twice.
/// </summary>
public static class CommentAttachments
{
    /// <summary>
    /// Add <paramref name="paths"/> to <paramref name="draft"/>, skipping
    /// folders, missing files and files already on it. Returns how many were added.
    /// </summary>
    public static int Add(List<PendingCommentAttachment> draft, IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            var file = PendingCommentAttachment.FromFile(path);
            if (draft.Any(d => string.Equals(d.Path, file.Path, StringComparison.OrdinalIgnoreCase))) continue;
            draft.Add(file);
            added++;
        }
        return added;
    }

    /// <summary>"Pasted Image 2026-03-04 at 14.05.09.png", the way a screenshot is named.</summary>
    public static string PastedImageName(DateTime when) =>
        $"Pasted Image {when.ToString("yyyy-MM-dd 'at' HH.mm.ss", CultureInfo.InvariantCulture)}.png";

    /// <summary>
    /// Upload each file in turn. Returns the files that did not land, with the
    /// reason for each, so the caller keeps exactly those on the draft.
    /// </summary>
    public static async Task<List<(PendingCommentAttachment File, string Error)>> UploadAllAsync(
        IReadOnlyList<PendingCommentAttachment> files,
        Func<PendingCommentAttachment, Task> upload)
    {
        var failed = new List<(PendingCommentAttachment, string)>();
        foreach (var file in files)
        {
            try
            {
                await upload(file);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add((file, ex is IOException or UnauthorizedAccessException ? $"{file.Name}: {ex.Message}" : ex.Message));
            }
        }
        return failed;
    }

    /// <summary>
    /// The status line after an upload pass had failures. <paramref name="post"/>
    /// names what the files came with, "comment" or "reply".
    /// </summary>
    public static string FailureMessage(IReadOnlyList<(PendingCommentAttachment File, string Error)> failed, bool hasText,
        string post = "comment")
    {
        var lead = failed.Count == 1 ? "A file was not attached" : $"{failed.Count} files were not attached";
        var tail = hasText ? $", so the {post} was not posted. Post again to retry." : ". Post again to retry.";
        return lead + tail + " " + string.Join(" ", failed.Select(f => f.Error.TrimEnd('.') + "."));
    }
}
