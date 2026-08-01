using FleetMate.Core.Shared;

namespace FleetMate.Core.Models.Projects;

/// <summary>
/// Everything the in-app pull request viewer shows, provider-agnostic — Azure
/// DevOps and GitHub both reduce to this shape.
/// </summary>
public sealed class PullRequestDetail
{
    /// <summary>Markdown from GitHub, HTML from Azure DevOps.</summary>
    public string? Body { get; init; }

    public List<PullRequestCommit> Commits { get; init; } = new();
    public List<PullRequestComment> Comments { get; init; } = new();
    public List<DiffFile> Files { get; init; } = new();

    /// <summary>
    /// True when the file list was capped. Surfaced in the UI rather than
    /// silently showing a short list — a diff that quietly omits files is worse
    /// than one that admits it.
    /// </summary>
    public bool Truncated { get; init; }

    public int Insertions => Files.Sum(f => f.Insertions);
    public int Deletions => Files.Sum(f => f.Deletions);

    /// <summary>Comments that are actual conversation, not vote/status noise.</summary>
    public IEnumerable<PullRequestComment> Conversation => Comments.Where(c => !c.IsSystem);
}

public sealed class PullRequestCommit
{
    /// <summary>Full SHA.</summary>
    public string Id { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
    public string? AuthorName { get; init; }
    public DateTime? Date { get; init; }

    public string ShortSha => Id.Length > 8 ? Id[..8] : Id;

    /// <summary>First line of the message — the rest is body text.</summary>
    public string Subject
    {
        get
        {
            var newline = Message.IndexOfAny(new[] { '\n', '\r' });
            return newline < 0 ? Message : Message[..newline];
        }
    }
}

public sealed class PullRequestComment
{
    public string Id { get; init; } = string.Empty;
    public string AuthorName { get; init; } = "unknown";

    /// <summary>Markdown from GitHub, HTML from Azure DevOps.</summary>
    public string Body { get; init; } = string.Empty;

    public DateTime? Date { get; init; }

    /// <summary>
    /// Vote and status noise — "X approved the pull request". Rendered quieter
    /// than a real comment, because mixing them into the conversation buries
    /// what people actually said.
    /// </summary>
    public bool IsSystem { get; init; }
}
