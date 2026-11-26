using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using FleetMate.Core.Config;
using Microsoft.Win32;

namespace FleetMate.Core.Services.Repos;

/// <summary>Property-change plumbing for the Repos models, shared by the WPF and WinUI apps.</summary>
public abstract class RepoObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The Repos workspace's remembered choices — panel, sidebar sort, grouping,
/// filter, collapsed groups, Insights period — beside FleetMate's other
/// per-user preferences in the registry. Both apps read and write the same values.
/// </summary>
public static class RepoWorkspacePreferences
{
    /// <summary>Tests point this at a throwaway key so they never touch the person's own choices.</summary>
    internal static string? KeyPathOverride { get; set; }

    private static string KeyPath => KeyPathOverride ?? AppEdition.Current.UserRegistryPath;

    /// <summary>Set by <see cref="UseMemoryStore"/>: choices live for this run only.</summary>
    private static Dictionary<string, string>? _memory;

    /// <summary>
    /// Demo mode: remember choices for this run only, so a demo never changes
    /// the person's own sidebar, selection or Insights period.
    /// </summary>
    public static void UseMemoryStore() => _memory ??= new Dictionary<string, string>(StringComparer.Ordinal);

    public static string? Read(string name)
    {
        if (_memory != null) return _memory.GetValueOrDefault(name);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue("repos." + name) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public static void Write(string name, string? value)
    {
        if (_memory != null)
        {
            if (value == null) _memory.Remove(name);
            else _memory[name] = value;
            return;
        }
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (value == null) key.DeleteValue("repos." + name, throwOnMissingValue: false);
            else key.SetValue("repos." + name, value);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // A preference that cannot be stored is simply not remembered.
        }
    }

    public static T ReadEnum<T>(string name, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(Read(name), out var value) ? value : fallback;
}

/// <summary>Explorer, the default app and path display for the Repos views of both apps.</summary>
public static class RepoShell
{
    /// <summary>Opens Explorer with <paramref name="path"/> selected, or the folder itself.</summary>
    public static void Reveal(string path)
    {
        try
        {
            var start = File.Exists(path)
                ? new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + path } }
                : new ProcessStartInfo("explorer.exe") { ArgumentList = { path } };
            start.UseShellExecute = false;
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
        }
    }

    /// <summary>Opens <paramref name="path"/> with the app Windows associates with it; throws <see cref="Win32Exception"/> when none.</summary>
    public static void OpenWithDefaultApp(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    /// <summary><c>C:\Users\me\Developer\x</c> → <c>~\Developer\x</c>.</summary>
    public static string Abbreviate(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home + "\\", StringComparison.OrdinalIgnoreCase) ? "~" + path[home.Length..] : path;
    }

    public static string Relative(DateTimeOffset? date) => Development.DevelopmentFigures.Relative(date, DateTimeOffset.Now);
}

/// <summary>
/// The words the Repos views show, shared by both apps so a row reads the same
/// on either: the sidebar's status line, a change's folder, a commit's byline,
/// the new-clones bar and the empty states.
/// </summary>
public static class RepoWorkspaceText
{
    /// <summary>The sidebar row's second line: branch, sync and change count, or the failure.</summary>
    public static string SidebarMetadata(RepoRecord record, RepoStatus? status)
    {
        if (status is null) return record.Key.Scope;
        if (status.Error is { } error) return "⚠ " + RepoStatusError.Summarize(error);
        var parts = new List<string> { "⎇ " + (status.Branch ?? "detached") };
        var sync = (status.Ahead > 0 ? $"↑{status.Ahead}" : "") + (status.Behind > 0 ? (status.Ahead > 0 ? " " : "") + $"↓{status.Behind}" : "");
        if (sync.Length > 0) parts.Add(sync);
        if (status.ChangedCount > 0) parts.Add($"{status.ChangedCount} changed");
        else if (status.IsClean) parts.Add("clean");
        return string.Join("   ", parts);
    }

    /// <summary>Where the checkout is, and git's full message when status failed.</summary>
    public static string SidebarTooltip(RepoRecord record, RepoStatus? status)
    {
        var where = record.Local is { } local ? RepoShell.Abbreviate(local.Path) : record.Key.DisplayName;
        return status?.Error is { } error ? $"{where}\n\n{error.Trim()}" : where;
    }

    /// <summary>The folder a change sits in, with forward slashes; empty at the root.</summary>
    public static string ChangeFolder(GitStatusEntry entry)
    {
        var slash = entry.RelativePath.LastIndexOf('/');
        return slash < 0 ? "" : entry.RelativePath[..slash];
    }

    public static string ChangeTooltip(GitStatusEntry entry) =>
        entry.OriginalPath is { } from ? $"{from} → {entry.RelativePath}" : entry.RelativePath;

    public static string CommitByline(GitCommit commit) =>
        $"{commit.ShortSha} · {commit.Author} · {RepoShell.Relative(commit.Date)}";

    /// <summary>A ref badge's label: the branch HEAD is on reads "HEAD → name".</summary>
    public static string RefLabel(RepoGitRef reference) =>
        reference.Kind is GitRefKind.Tag or GitRefKind.RemoteBranch || !reference.IsHead ? reference.Name : "HEAD → " + reference.Name;

    /// <summary>"3 new repositories found in ~\Developer".</summary>
    public static string NewClonesSummary(IReadOnlyList<DiscoveredCheckout> clones, IReadOnlyList<string>? scanRoots) =>
        $"{clones.Count} new {(clones.Count == 1 ? "repository" : "repositories")} found in {NewClonesPlace(clones, scanRoots)}";

    /// <summary>The watched folder the new clones are in, or "your watched folders" when they span several.</summary>
    public static string NewClonesPlace(IReadOnlyList<DiscoveredCheckout> clones, IReadOnlyList<string>? scanRoots)
    {
        if (scanRoots is null) return "your watched folders";
        var places = clones.Select(c => scanRoots.FirstOrDefault(r =>
        {
            try
            {
                var root = RepoSettings.Normalize(r);
                return c.Path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        })).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return places.Count == 1 && places[0] != null ? places[0]! : "your watched folders";
    }

    /// <summary>The watched folders, or null when the settings cannot be read.</summary>
    public static IReadOnlyList<string>? ScanRoots(RepoManager manager)
    {
        try { return manager.Settings().ScanRoots; }
        catch (Exception) { return null; }
    }

    /// <summary>The left list's empty state for the open panel, or null when it has rows.</summary>
    public static string? ListEmpty(RepoWorkspaceModel model, int visibleFileRows)
    {
        var git = model.Git;
        return git.Panel switch
        {
            RepoPanel.Changes when git.FilteredFiles.Count == 0 =>
                git.Files.Count == 0 ? "No changes. The working tree matches HEAD." : "No changes match the filter.",
            RepoPanel.History when git.FilteredCommits.Count == 0 =>
                git.Commits.Count == 0 ? "No commits yet." : "No commits match the filter.",
            RepoPanel.Files when model.GrepQuery != null && model.GrepResults.Count == 0 && !model.IsSearching =>
                $"Nothing in this repository contains “{model.GrepQuery}”.",
            RepoPanel.Files when model.GrepQuery == null && model.Tree.Count == 0 => "Loading…",
            RepoPanel.Files when model.GrepQuery == null && visibleFileRows == 0 => "No file names match.",
            _ => null,
        };
    }

    /// <summary>The Files header: "Files", or the search being shown.</summary>
    public static string FilesTitle(RepoWorkspaceModel model) =>
        model.GrepQuery != null ? $"Search: {model.GrepQuery}" : "Files";

    /// <summary>Beside the Files title: the match or file count, or how to search contents.</summary>
    public static string FilesHint(RepoWorkspaceModel model)
    {
        if (model.GrepQuery != null) return $"{model.GrepResults.Count} match{(model.GrepResults.Count == 1 ? "" : "es")}";
        return model.FileFilter.Trim().Length > 0 ? "Press Enter to search contents" : $"{model.Files.Count:N0} files";
    }

    /// <summary>The editor header: the path, and whether it is edited or read only.</summary>
    public static string EditorTitle(RepoWorkspaceModel model) => model.Document is not { } document
        ? "Editor"
        : document.Path + (model.IsDirty ? "  · Edited" : "") + (document.IsReadOnly ? "  · Read only" : "");

    /// <summary>The composer's status line: the last result, or when the pane last refreshed.</summary>
    public static string ComposerStatus(GitPaneModel git) => git.StatusMessage is { } message
        ? (git.StatusKind == GitStatusKind.Success ? "✓ " : git.StatusKind == GitStatusKind.Error ? "⚠ " : "") + message
        : git.LastRefreshedAt is { } at ? $"Refreshed {RepoShell.Relative(at)}" : "";

    /// <summary>The Push button's hint: refused on a protected branch.</summary>
    public static string PushTooltip(GitPaneModel git) => git.IsOnProtectedBranch
        ? $"'{git.CurrentBranch}' is protected: pushing it is refused. Push a branch and open a pull request."
        : "git push (P)";

    /// <summary>The ahead/behind arrows beside the branch name.</summary>
    public static string SyncLabel(GitPaneModel git) =>
        ((git.Ahead > 0 ? $"↑{git.Ahead} " : "") + (git.Behind > 0 ? $"↓{git.Behind}" : "")).Trim();

    /// <summary>The branch menu's line for a branch: its name, and the upstream it tracks.</summary>
    public static string BranchTitle(GitBranch branch) =>
        branch.UpstreamName is { } upstream ? $"{branch.Name}   →  {upstream}" : branch.Name;

    /// <summary>Whether the selected checkout has an AGENTS.md for agents to read.</summary>
    public static bool HasAgentsFile(RepoWorkspaceModel model) =>
        model.SelectedStatus?.AgentsFile != null
        || (model.SelectedPath is { } root && File.Exists(Path.Combine(root, "AGENTS.md")));

    /// <summary>The <c>fleetmate repos stats</c> command the Insights strip shows, for the current scope.</summary>
    public static string InsightsCommand(RepoWorkspaceModel model)
    {
        var since = RepoInsightsModel.SinceArgument(model.Insights.Period);
        var cli = model.Insights.Scope == RepoInsightsModel.InsightsScope.All
            ? $"fleetmate repos stats --since {since}"
            : $"fleetmate repos stats {model.Selected?.Key.DisplayName ?? "<repo>"} --since {since}";
        return cli + " --ref " + RepoInsightsModel.HistoryReference;
    }

    /// <summary>The Insights caption: what history is read, and the command that prints the same numbers.</summary>
    public static string InsightsCaption(RepoWorkspaceModel model) =>
        "origin/main · Merge commits excluded · " + InsightsCommand(model);

    /// <summary>Settings › Repositories: where new clones go and how deep the watched folders are scanned.</summary>
    public static string LocationsFooter(int scanDepth) =>
        @"New clones go to <clone root>\AzDevOps\<Project>\<Repo> or <clone root>\GitHub\<owner>\<repo>. " +
        $"FleetMate looks for clones in the watched folders, {scanDepth} levels deep, and links each one by its origin.";

    /// <summary>What Find Existing Clones did.</summary>
    public static string DiscoverySummary(IReadOnlyList<DiscoveryResult> results)
    {
        var linked = results.Count(r => r.Action == DiscoveryAction.Linked);
        var existing = results.Count(r => r.Action == DiscoveryAction.AlreadyLinked);
        var duplicates = results.Count(r => r.Action == DiscoveryAction.Duplicate);
        var parts = new List<string> { $"Linked {linked} new", $"{existing} already linked" };
        if (duplicates > 0) parts.Add($"{duplicates} second copies left alone");
        return string.Join(" · ", parts) + ". Turn on Track for the ones you work in.";
    }

    /// <summary>The catalog's counts, and when it was last read from the providers.</summary>
    public static string CatalogFooter(IReadOnlyList<RepoRecord> records, DateTimeOffset? fetchedAt) =>
        $"{records.Count} repositories, {records.Count(r => r.IsLocal)} on this PC, {records.Count(r => r.IsTracked)} tracked." +
        (fetchedAt is { } at ? $" Catalog updated {RepoShell.Relative(at)}." : " Refresh to list what the providers hold.");

    /// <summary>The catalog list's empty state.</summary>
    public static string CatalogEmpty(int recordCount, string query) => recordCount == 0
        ? "No repositories yet. Refresh the catalog, find existing clones, or link a folder."
        : $"No repositories match “{query}”.";

    /// <summary>The Delete Local Clone confirmation when nothing would be lost.</summary>
    public static string SafeDeleteMessage(string name, string path) =>
        $"Move “{name}” to the Recycle Bin?\n\n{path}\n\nEverything in it is committed and pushed. FleetMate stops tracking it.";
}
