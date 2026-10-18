using FleetMate.Core.Services.Repos;

namespace FleetMate.Core.Services.Terminal;

/// <summary>
/// The starting workspace FleetMate writes from the repositories the person
/// has checked out: one section per host (Azure DevOps, GitHub, other), a
/// session per repository named <c>Project/Repo</c> or <c>owner/repo</c>,
/// ordered by project or owner. Nothing about any organization is built in;
/// it is all read from the registry the Repos tab and <c>fleetmate repos</c> share.
/// </summary>
public static class TerminalWorkspaceBaseline
{
    /// <summary>The generated workspace's name, and the mark that it came from Repos.</summary>
    public const string Name = "Repos";
    public const string Source = "repos";

    /// <summary>With this many checkouts or fewer, every one is included.</summary>
    public const int IncludeAllUpTo = 8;
    /// <summary>Beyond that, a checkout is active when its HEAD moved (commit, checkout, merge, pull, rebase) within this many days.</summary>
    public const int ActiveWithinDays = 14;

    public sealed record Checkout(RepoProvider Provider, string Scope, string DisplayName, string Path, DateTime? LastActivity);

    /// <summary>All checkouts when there are few; otherwise the active ones, or the most recent few when none is.</summary>
    public static IReadOnlyList<Checkout> ActiveCheckouts(IReadOnlyList<Checkout> checkouts, DateTime now)
    {
        if (checkouts.Count <= IncludeAllUpTo) return checkouts;
        var cutoff = now.AddDays(-ActiveWithinDays);
        var active = checkouts.Where(c => (c.LastActivity ?? DateTime.MinValue) >= cutoff).ToList();
        if (active.Count > 0) return active;
        return checkouts.OrderByDescending(c => c.LastActivity ?? DateTime.MinValue).Take(IncludeAllUpTo).ToList();
    }

    public static string SectionTitle(RepoProvider provider) => provider switch
    {
        RepoProvider.AzureDevOps => "Azure DevOps",
        RepoProvider.GitHub => "GitHub",
        _ => "Other",
    };

    /// <summary>The baseline workspace, or null when nothing is checked out.</summary>
    public static TerminalWorkspace? Make(IReadOnlyList<Checkout> checkouts, string command, DateTime now)
    {
        var chosen = ActiveCheckouts(checkouts, now);
        if (chosen.Count == 0) return null;
        var workspace = new TerminalWorkspace { Name = Name, Source = Source, Show = true };
        Add(chosen, workspace, command);
        return workspace;
    }

    /// <summary>
    /// Add the checkouts not already in <paramref name="workspace"/> (matched
    /// by folder) to their host's section, leaving every existing session and
    /// section as the person arranged it. Returns how many were added.
    /// </summary>
    public static int AddMissing(IReadOnlyList<Checkout> checkouts, TerminalWorkspace workspace, string command, DateTime now)
    {
        var present = workspace.Sections.SelectMany(s => s.Sessions)
            .Where(s => s.Directory != null).Select(s => Normalized(s.Directory!)).ToHashSet();
        var missing = ActiveCheckouts(checkouts, now).Where(c => !present.Contains(Normalized(c.Path))).ToList();
        Add(missing, workspace, command);
        return missing.Count;
    }

    private static void Add(IEnumerable<Checkout> checkouts, TerminalWorkspace workspace, string command)
    {
        var ordered = checkouts
            .OrderBy(c => c.Provider switch { RepoProvider.AzureDevOps => 0, RepoProvider.GitHub => 1, _ => 2 })
            .ThenBy(c => c.Scope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase);
        foreach (var checkout in ordered)
        {
            var title = SectionTitle(checkout.Provider);
            var section = workspace.Sections.FirstOrDefault(s => s.Name == title);
            if (section == null) workspace.Sections.Add(section = new TerminalWorkspaceSection { Name = title });
            section.Sessions.Add(new TerminalWorkspaceSession
            {
                Name = checkout.DisplayName,
                Command = command,
                Directory = TerminalWorkspaceStore.Abbreviate(checkout.Path),
            });
        }
    }

    private static string Normalized(string path) =>
        TerminalWorkspaceStore.Expand(path).Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

    /// <summary>
    /// When the checkout's HEAD last moved: the modification time of its HEAD
    /// reflog, which git appends on every commit, checkout, merge, pull and
    /// rebase. Follows a worktree's <c>.git</c> file to its git directory.
    /// </summary>
    public static DateTime? LastActivity(string checkout)
    {
        try
        {
            var dotGit = System.IO.Path.Combine(checkout, ".git");
            var gitDir = dotGit;
            if (File.Exists(dotGit))
            {
                var line = File.ReadLines(dotGit).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
                if (line == null) return null;
                var target = line["gitdir:".Length..].Trim();
                gitDir = System.IO.Path.IsPathRooted(target) ? target : System.IO.Path.GetFullPath(System.IO.Path.Combine(checkout, target));
            }
            else if (!Directory.Exists(dotGit)) return null;
            foreach (var file in new[] { System.IO.Path.Combine(gitDir, "logs", "HEAD"), System.IO.Path.Combine(gitDir, "HEAD") })
                if (File.Exists(file)) return File.GetLastWriteTimeUtc(file);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The registry's tracked checkouts that exist on disk (every registered one when none is tracked), with their activity.</summary>
    public static IReadOnlyList<Checkout> Checkouts(RepoRegistryDocument document)
    {
        var tracked = document.TrackedEntries;
        var entries = tracked.Count > 0 ? tracked : document.Entries;
        return entries
            .Where(e => Directory.Exists(e.Path))
            .Select(e => new Checkout(e.Key.Provider, e.Key.Scope, e.Key.DisplayName, e.Path, LastActivity(e.Path)))
            .ToList();
    }
}
