using System.Text.Json.Serialization;

namespace FleetMate.Core.Services.Repos;

// ── Identity ────────────────────────────────────────────────────────────

/// <summary>Where a repository is hosted.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RepoProvider>))]
public enum RepoProvider
{
    [JsonStringEnumMemberName("azdo")] AzureDevOps,
    [JsonStringEnumMemberName("github")] GitHub,
    /// <summary>
    /// Any other git host. Such a checkout can be linked and operated on, but
    /// never appears in the catalog.
    /// </summary>
    [JsonStringEnumMemberName("other")] Other,
}

public static class RepoProviderExtensions
{
    /// <summary>The id prefix and CLI spelling: <c>azdo</c>, <c>github</c>, <c>other</c>.</summary>
    public static string Code(this RepoProvider provider) => provider switch
    {
        RepoProvider.AzureDevOps => "azdo",
        RepoProvider.GitHub => "github",
        _ => "other",
    };

    /// <summary>Folder name used by the default clone layout.</summary>
    public static string LayoutFolder(this RepoProvider provider) => provider switch
    {
        RepoProvider.AzureDevOps => "AzDevOps",
        RepoProvider.GitHub => "GitHub",
        _ => "Other",
    };

    public static string Title(this RepoProvider provider) => provider switch
    {
        RepoProvider.AzureDevOps => "Azure DevOps",
        RepoProvider.GitHub => "GitHub",
        _ => "Other",
    };

    public static RepoProvider? ParseCode(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "azdo" or "devops" or "azure" => RepoProvider.AzureDevOps,
        "github" or "gh" => RepoProvider.GitHub,
        "other" => RepoProvider.Other,
        _ => null,
    };
}

/// <summary>
/// The provider-neutral identity of a repository, derived from its remote URL
/// or from the provider's API. Two URLs naming the same repository — https or
/// ssh, any case, with or without <c>.git</c> — produce the same key.
/// <list type="bullet">
/// <item>Azure DevOps: <c>Owner</c> is the organization, <c>Project</c> the project.</item>
/// <item>GitHub: <c>Owner</c> is the user or organization; <c>Project</c> is null.</item>
/// <item>Other: <c>Owner</c> is the host (or parent folder); <c>Name</c> the remaining path.</item>
/// </list>
/// </summary>
public sealed record RepoKey(RepoProvider Provider, string Owner, string? Project, string Name) : IComparable<RepoKey>
{
    /// <summary>
    /// Stable registry id, lowercased: <c>azdo:org/project/repo</c>,
    /// <c>github:owner/repo</c>, <c>other:host/path</c>.
    /// </summary>
    [JsonIgnore]
    public string Id => Provider.Code() + ":" +
        string.Join("/", new[] { Owner, Project, Name }.Where(p => p != null)).ToLowerInvariant();

    /// <summary>What people type and read: <c>Project/Repo</c> or <c>owner/repo</c>.</summary>
    [JsonIgnore]
    public string DisplayName => Provider == RepoProvider.AzureDevOps
        ? $"{Project ?? Owner}/{Name}"
        : $"{Owner}/{Name}";

    /// <summary>The scope a short name is qualified with: the project for Azure DevOps, the owner otherwise.</summary>
    [JsonIgnore]
    public string Scope => Project ?? Owner;

    public int CompareTo(RepoKey? other) => string.CompareOrdinal(Id, other?.Id);

    /// <summary><c>refs/heads/main</c> → <c>main</c>.</summary>
    public static string ShortBranch(string reference) =>
        reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference;
}

// ── Catalog ─────────────────────────────────────────────────────────────

/// <summary>One repository the signed-in user can see on a provider.</summary>
public sealed record CatalogRepo
{
    public required RepoKey Key { get; init; }
    public required string CloneUrl { get; init; }
    public string? SshUrl { get; init; }
    public string? WebUrl { get; init; }

    private readonly string? _defaultBranch;
    /// <summary>Short branch name (<c>main</c>), without <c>refs/heads/</c>.</summary>
    public string? DefaultBranch
    {
        get => _defaultBranch;
        init => _defaultBranch = value == null ? null : RepoKey.ShortBranch(value);
    }

    public bool IsArchived { get; init; }
    public bool IsFork { get; init; }
    public bool? IsPrivate { get; init; }

    [JsonIgnore]
    public string Id => Key.Id;
}

/// <summary>
/// A catalog fetch: what each provider returned, and why any provider failed.
/// A failure in one provider never hides the other's results.
/// </summary>
public sealed class RepoCatalog
{
    public List<CatalogRepo> Repos { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.Now;
}

// ── Status ──────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter<RepoChangeKind>))]
public enum RepoChangeKind
{
    [JsonStringEnumMemberName("changed")] Changed,
    [JsonStringEnumMemberName("renamed")] Renamed,
    [JsonStringEnumMemberName("unmerged")] Unmerged,
    [JsonStringEnumMemberName("untracked")] Untracked,
    [JsonStringEnumMemberName("ignored")] Ignored,
}

/// <summary>One changed path, from <c>git status --porcelain=v2</c>.</summary>
public sealed record RepoFileChange
{
    public required string Path { get; init; }
    /// <summary>The source path of a rename or copy.</summary>
    public string? OriginalPath { get; init; }
    public required RepoChangeKind Kind { get; init; }
    /// <summary>Index (staged) status letter: M, A, D, R, C, T, U, or "." for none.</summary>
    public string IndexStatus { get; init; } = ".";
    /// <summary>Worktree (unstaged) status letter, same alphabet.</summary>
    public string WorktreeStatus { get; init; } = ".";

    public bool Staged => Kind != RepoChangeKind.Untracked && Kind != RepoChangeKind.Ignored && IndexStatus != ".";
    public bool Unstaged => Kind != RepoChangeKind.Untracked && Kind != RepoChangeKind.Ignored && WorktreeStatus != ".";
}

/// <summary>One entry of <c>git worktree list --porcelain</c>.</summary>
public sealed record RepoWorktree(
    string Path, string? Head, string? Branch,
    bool IsDetached, bool IsBare, bool IsLocked, bool IsPrunable);

/// <summary>Branch header and changes from <c>git status --porcelain=v2 --branch</c>.</summary>
public sealed class GitStatusSnapshot
{
    public string? HeadOid { get; set; }
    /// <summary>Null when HEAD is detached.</summary>
    public string? Branch { get; set; }
    public string? Upstream { get; set; }
    public int Ahead { get; set; }
    public int Behind { get; set; }
    public List<RepoFileChange> Changes { get; set; } = new();

    public int StagedCount => Changes.Count(c => c.Staged);
    public int UnstagedCount => Changes.Count(c => c.Unstaged);
    public int UntrackedCount => Changes.Count(c => c.Kind == RepoChangeKind.Untracked);
    public int ConflictedCount => Changes.Count(c => c.Kind == RepoChangeKind.Unmerged);
    public bool IsClean => Changes.All(c => c.Kind == RepoChangeKind.Ignored);
}

/// <summary>Everything <c>fleetmate repos status</c> reports for one repository.</summary>
public sealed class RepoStatus
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Path { get; init; } = "";
    public string? Branch { get; init; }
    public string? Upstream { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public int Staged { get; init; }
    public int Unstaged { get; init; }
    public int Untracked { get; init; }
    public int Conflicted { get; init; }
    public bool IsClean { get; init; }
    public List<RepoFileChange> Changes { get; init; } = new();
    public List<RepoWorktree> Worktrees { get; init; } = new();
    /// <summary>
    /// The repository's agent instructions (<c>AGENTS.md</c>), when it has one.
    /// Agents read it before working in the repository.
    /// </summary>
    public string? AgentsFile { get; init; }
    /// <summary>When HEAD was last committed; null for an empty repository.</summary>
    public DateTimeOffset? LastCommitAt { get; init; }
    public string? Error { get; init; }

    /// <summary>Staged, unstaged and untracked paths together.</summary>
    [JsonIgnore]
    public int ChangedCount => Staged + Unstaged + Untracked;

    public static RepoStatus From(string id, string displayName, string path, GitStatusSnapshot? snapshot,
        IEnumerable<RepoWorktree>? worktrees, string? agentsFile, DateTimeOffset? lastCommitAt = null, string? error = null) => new()
    {
        Id = id,
        DisplayName = displayName,
        Path = path,
        Branch = snapshot?.Branch,
        Upstream = snapshot?.Upstream,
        Ahead = snapshot?.Ahead ?? 0,
        Behind = snapshot?.Behind ?? 0,
        Staged = snapshot?.StagedCount ?? 0,
        Unstaged = snapshot?.UnstagedCount ?? 0,
        Untracked = snapshot?.UntrackedCount ?? 0,
        Conflicted = snapshot?.ConflictedCount ?? 0,
        IsClean = snapshot?.IsClean ?? false,
        Changes = snapshot?.Changes ?? new(),
        Worktrees = worktrees?.ToList() ?? new(),
        AgentsFile = agentsFile,
        LastCommitAt = lastCommitAt,
        Error = error,
    };
}

// ── History, search, results ────────────────────────────────────────────

public sealed record RepoCommit(string Sha, string ShortSha, string Author, string Email, DateTimeOffset? Date, string Subject);

public sealed record RepoGrepMatch(string Path, int Line, int Column, string Text);

/// <summary>The outcome of one git operation on one repository, for batch commands.</summary>
public sealed record RepoOperationResult(string Id, string DisplayName, string Operation, bool Succeeded, string Output, string? Error);

// ── Errors ──────────────────────────────────────────────────────────────

public enum RepoErrorKind
{
    NotFound,
    Ambiguous,
    NotLocal,
    NotAGitRepository,
    ProtectedBranch,
    PathOutsideRepository,
    DestinationExists,
    GitFailed,
    NothingToCommit,
    InvalidArgument,
}

/// <summary>A repository operation that was refused or failed, with a message fit to show as it is.</summary>
public sealed class RepoException : Exception
{
    public RepoErrorKind Kind { get; }
    /// <summary>The argument, path or branch the error is about.</summary>
    public string? Subject { get; }
    public IReadOnlyList<string> Candidates { get; }

    private RepoException(RepoErrorKind kind, string message, string? subject = null, IReadOnlyList<string>? candidates = null)
        : base(message)
    {
        Kind = kind;
        Subject = subject;
        Candidates = candidates ?? Array.Empty<string>();
    }

    public static RepoException NotFound(string argument) => new(RepoErrorKind.NotFound,
        $"No repository matches '{argument}'. Run 'fleetmate repos catalog' to refresh the list.", argument);

    public static RepoException Ambiguous(string argument, IReadOnlyList<string> candidates) => new(RepoErrorKind.Ambiguous,
        $"'{argument}' matches several repositories; qualify it as project/name or owner/name:\n  " + string.Join("\n  ", candidates),
        argument, candidates);

    public static RepoException NotLocal(string name) => new(RepoErrorKind.NotLocal,
        $"{name} has no local checkout. Clone it with 'fleetmate repos clone' or link one with 'fleetmate repos link'.", name);

    public static RepoException NotAGitRepository(string path) => new(RepoErrorKind.NotAGitRepository,
        $"{path} is not a git checkout.", path);

    public static RepoException ProtectedBranch(string branch) => new(RepoErrorKind.ProtectedBranch,
        $"Refusing to commit or push on '{branch}'. Work on a branch and open a pull request, or pass --allow-main.", branch);

    public static RepoException PathOutsideRepository(string path) => new(RepoErrorKind.PathOutsideRepository,
        $"{path} is outside the repository.", path);

    public static RepoException DestinationExists(string path) => new(RepoErrorKind.DestinationExists,
        $"{path} already exists.", path);

    public static RepoException GitFailed(string command, string message) => new(RepoErrorKind.GitFailed,
        $"git {command} failed: {message}", command);

    public static RepoException NothingToCommit() => new(RepoErrorKind.NothingToCommit, "Nothing to commit.");

    public static RepoException InvalidArgument(string message) => new(RepoErrorKind.InvalidArgument, message);
}
