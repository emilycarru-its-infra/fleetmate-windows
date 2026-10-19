using FleetMate.Core.Services.Repos;

namespace FleetMate.Core.Services.Terminal;

/// <summary>
/// The workspace FleetMate keeps from the repositories the person tracks:
/// one section per host (Azure DevOps, GitHub, Other), a session per
/// repository labelled <c>Project/Repo</c> or <c>owner/repo</c>, ordered by
/// project or owner, kept in step as repositories are tracked, hidden or
/// removed. Nothing about any organization is built in; it is all read from
/// the registry the Repos tab and <c>fleetmate repos</c> share.
/// </summary>
public static class TerminalWorkspaceBaseline
{
    /// <summary>The generated workspace's name, and the mark that it came from Repos.</summary>
    public const string Name = "Repos";
    public const string Source = "repos";

    /// <summary>
    /// Tracked checkouts are always all included: tracking is how the person
    /// says which repositories they work in. When none is tracked, every
    /// registered checkout is a candidate, and with this many or fewer they all go in.
    /// </summary>
    public const int IncludeAllUpTo = 8;
    /// <summary>Beyond that, an untracked checkout counts as active when its HEAD moved (commit, checkout, merge, pull, rebase) within this many days.</summary>
    public const int ActiveWithinDays = 14;

    public sealed record Checkout(RepoProvider Provider, string Scope, string DisplayName, string Path, DateTime? LastActivity, bool Tracked = false);

    /// <summary>What <see cref="Sync"/> changed, as normalized folders.</summary>
    public sealed record Change(IReadOnlyList<string> Added, IReadOnlyList<string> Removed)
    {
        public bool IsEmpty => Added.Count == 0 && Removed.Count == 0;
    }

    /// <summary>Every checkout when all are tracked or there are few; otherwise the active ones, or the most recent few when none is.</summary>
    public static IReadOnlyList<Checkout> ActiveCheckouts(IReadOnlyList<Checkout> checkouts, DateTime now)
    {
        if (checkouts.Count <= IncludeAllUpTo || checkouts.All(c => c.Tracked)) return checkouts;
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
        workspace.Generated = chosen.Select(c => Normalized(c.Path)).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        return workspace;
    }

    /// <summary>
    /// Bring the Repos workspace in step with the checkouts, the way the
    /// person left it otherwise. A checkout never placed before is added to its
    /// host's section. A session FleetMate placed whose repository is no longer
    /// tracked, is hidden or is gone is taken out, and its section with it only
    /// when this emptied it. Everything else stays as arranged: renamed, moved,
    /// reordered and re-pointed sessions, sessions the person added, and the
    /// absence of one they removed.
    /// </summary>
    public static Change Sync(TerminalWorkspace workspace, IReadOnlyList<Checkout> checkouts, string command, DateTime now)
    {
        MigrateNames(workspace);
        var wanted = ActiveCheckouts(checkouts, now);
        var wantedPaths = wanted.Select(c => Normalized(c.Path)).ToHashSet();
        // A workspace from before Generated existed: what it holds was placed.
        var placed = (workspace.Generated
            ?? workspace.Sections.SelectMany(s => s.Sessions).Where(s => s.Directory != null).Select(s => s.Directory!).ToList())
            .Select(Normalized).ToHashSet();

        var stale = placed.Where(p => !wantedPaths.Contains(p)).ToHashSet();
        var removed = new List<string>();
        var emptied = new List<TerminalWorkspaceSection>();
        foreach (var section in workspace.Sections)
        {
            var before = section.Sessions.Count;
            section.Sessions.RemoveAll(session =>
            {
                if (session.Directory is not { } directory || !stale.Contains(Normalized(directory))) return false;
                removed.Add(Normalized(directory));
                return true;
            });
            if (before > 0 && section.Sessions.Count == 0) emptied.Add(section);
        }
        // Take out sections this removal emptied, never one the person left empty.
        foreach (var section in emptied) workspace.Sections.Remove(section);
        placed.ExceptWith(stale);

        var present = workspace.Sections.SelectMany(s => s.Sessions)
            .Where(s => s.Directory != null).Select(s => Normalized(s.Directory!)).ToHashSet();
        var fresh = wanted.Where(c => !placed.Contains(Normalized(c.Path)) && !present.Contains(Normalized(c.Path)))
            .GroupBy(c => Normalized(c.Path)).Select(g => g.First()).ToList();
        Add(fresh, workspace, command);
        var added = fresh.Select(c => Normalized(c.Path)).ToList();
        placed.UnionWith(added);
        // A folder the person already had in the workspace counts as placed too.
        placed.UnionWith(present.Where(wantedPaths.Contains));
        workspace.Generated = placed.OrderBy(p => p, StringComparer.Ordinal).ToList();
        return new Change(added, removed);
    }

    /// <summary>
    /// Sessions written before Label existed carried the repository's name as
    /// their own, which would hold back the program's title. Where it is still
    /// exactly the generated <c>Project/Repo</c>, it becomes the label.
    /// </summary>
    public static void MigrateNames(TerminalWorkspace workspace)
    {
        if (workspace.Generated != null) return;
        foreach (var session in workspace.Sections.SelectMany(s => s.Sessions))
        {
            if (session.Label != null || session.Name is not { } name || !name.Contains('/') || session.Directory is not { } directory) continue;
            var leaf = name.Split('/').Last();
            var folder = System.IO.Path.GetFileName(TerminalWorkspaceStore.Expand(directory).TrimEnd('\\', '/'));
            if (!string.Equals(leaf, folder, StringComparison.OrdinalIgnoreCase)) continue;
            session.Label = name;
            session.Name = null;
        }
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
        if (workspace.Generated != null)
            workspace.Generated = workspace.Generated.Concat(missing.Select(c => Normalized(c.Path))).Distinct()
                .OrderBy(p => p, StringComparer.Ordinal).ToList();
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
                Label = checkout.DisplayName,
                Command = command,
                Directory = TerminalWorkspaceStore.Abbreviate(checkout.Path),
            });
        }
    }

    /// <summary>A folder as compared: home expanded, backslashes, no trailing separator, any case.</summary>
    public static string Normalized(string path) =>
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

    /// <summary>The registry's tracked checkouts that exist on disk (every registered one when none is tracked), with their activity. Repositories hidden from the Repos sidebar are left out.</summary>
    public static IReadOnlyList<Checkout> Checkouts(RepoRegistryDocument document)
    {
        var shown = document.Entries.Where(e => !e.Hidden).ToList();
        var tracked = shown.Where(e => e.Tracked).ToList();
        var entries = tracked.Count > 0 ? tracked : shown;
        return entries
            .Select(e => (Entry: e, Path: RepoSettings.Expand(e.Path)))
            .Where(e => Directory.Exists(e.Path))
            .Select(e => new Checkout(e.Entry.Key.Provider, e.Entry.Key.Scope, e.Entry.Key.DisplayName, e.Path, LastActivity(e.Path), e.Entry.Tracked))
            .ToList();
    }

    /// <summary>
    /// Keep the Repos workspace in <paramref name="file"/> in step with the
    /// checkouts: written whenever the file has none (an existing file, even an
    /// empty one, gets it too) at index 0, then synced. Nothing is written once
    /// the person deleted it. Returns whether the file changed.
    /// </summary>
    public static bool SyncBaseline(TerminalWorkspaceFile file, IReadOnlyList<Checkout> checkouts, string command, DateTime now)
    {
        if (file.BaselineIndex is { } index)
        {
            var workspace = file.Workspaces[index];
            var before = TerminalWorkspaceStore.Serialize(workspace);
            Sync(workspace, checkouts, command, now);
            return TerminalWorkspaceStore.Serialize(workspace) != before;
        }
        if (file.GenerateFromRepos == false || Make(checkouts, command, now) is not { } baseline) return false;
        file.Workspaces.Insert(0, baseline);
        return true;
    }
}
