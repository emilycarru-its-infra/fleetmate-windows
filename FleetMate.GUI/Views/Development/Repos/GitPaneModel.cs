using System.IO;
using FleetMate.Core.Services.Repos;
using FleetMate.Core.Shared;

namespace FleetMate.GUI.Views.Development.Repos;

/// <summary>What the Repos workspace shows for the selected repository.</summary>
public enum RepoPanel { Changes, History, Files, Insights }

public enum GitStatusKind { Info, Success, Error }

/// <summary>
/// All the live state of one repository's git pane — changes, branches,
/// commits, the selection in each, the commit composer — and the actions on
/// it. Ported from FleetMate for Mac's GitPaneState (itself adapted from
/// MunkiStudio, Apache-2.0). FleetMate's rules hold: commit and push refuse
/// protected branches and the GUI never overrides that; pull is fast-forward
/// only; failures go to the error banner; and nothing destructive runs
/// without the person confirming it (<see cref="Confirm"/>).
/// </summary>
public sealed class GitPaneModel : RepoObservable
{
    private RepoPanel _panel = RepoWorkspacePreferences.ReadEnum("panel", RepoPanel.Changes);
    private GitWorkingCopy? _copy;
    private string? _currentBranch;
    private int _ahead, _behind;
    private IReadOnlyList<GitStatusEntry> _files = Array.Empty<GitStatusEntry>();
    private IReadOnlyList<GitBranch> _branches = Array.Empty<GitBranch>();
    private IReadOnlyList<GitCommit> _commits = Array.Empty<GitCommit>();
    private IReadOnlyList<GraphRow> _graph = Array.Empty<GraphRow>();
    private int _laneCount = 1;
    private IReadOnlyList<string> _fileSelection = Array.Empty<string>();
    private string? _commitSelection;
    private string _diffText = "";
    private string _commitSubject = "", _commitBody = "";
    private bool _amend, _skipHooks, _isBusy;
    private string? _statusMessage;
    private GitStatusKind _statusKind;
    private string _processOutput = "";
    private DateTimeOffset? _lastRefreshedAt;
    private string _filter = "";

    /// <summary>Remembered across launches: the workspace reopens in the mode it was left in.</summary>
    public RepoPanel Panel
    {
        get => _panel;
        set
        {
            if (!Set(ref _panel, value)) return;
            RepoWorkspacePreferences.Write("panel", value.ToString());
            _ = SyncDiffAsync();
        }
    }

    public GitWorkingCopy? Copy => _copy;
    public IReadOnlySet<string> ProtectedBranches { get; private set; } = new HashSet<string>();

    public string? CurrentBranch { get => _currentBranch; private set => Set(ref _currentBranch, value); }
    public int Ahead { get => _ahead; private set => Set(ref _ahead, value); }
    public int Behind { get => _behind; private set => Set(ref _behind, value); }
    public IReadOnlyList<GitStatusEntry> Files { get => _files; private set => Set(ref _files, value); }
    public IReadOnlyList<GitBranch> Branches { get => _branches; private set => Set(ref _branches, value); }
    public IReadOnlyList<GitCommit> Commits { get => _commits; private set => Set(ref _commits, value); }
    public IReadOnlyList<GraphRow> Graph { get => _graph; private set => Set(ref _graph, value); }
    public int LaneCount { get => _laneCount; private set => Set(ref _laneCount, value); }

    /// <summary>Selected change ids (side plus path); Ctrl-click and Shift-click extend it.</summary>
    public IReadOnlyList<string> FileSelection
    {
        get => _fileSelection;
        set
        {
            if (_fileSelection.SequenceEqual(value)) return;
            _fileSelection = value;
            Raise();
            Raise(nameof(PrimaryEntry));
            _ = SyncDiffAsync();
        }
    }

    public string? CommitSelection
    {
        get => _commitSelection;
        set
        {
            if (!Set(ref _commitSelection, value)) return;
            Raise(nameof(FocusedCommit));
            _ = SyncDiffAsync();
        }
    }

    public GitStatusEntry? PrimaryEntry => _fileSelection.Count == 0 ? null : _files.FirstOrDefault(f => f.Id == _fileSelection[0]);
    public GitCommit? FocusedCommit => _commits.FirstOrDefault(c => c.Sha == _commitSelection);
    public string DiffText { get => _diffText; private set => Set(ref _diffText, value); }

    public string CommitSubject { get => _commitSubject; set => Set(ref _commitSubject, value); }
    public string CommitBody { get => _commitBody; set => Set(ref _commitBody, value); }
    public bool Amend { get => _amend; set => Set(ref _amend, value); }
    public bool SkipHooks { get => _skipHooks; set => Set(ref _skipHooks, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public string? StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public GitStatusKind StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }
    public string ProcessOutput { get => _processOutput; private set => Set(ref _processOutput, value); }
    public DateTimeOffset? LastRefreshedAt { get => _lastRefreshedAt; private set => Set(ref _lastRefreshedAt, value); }

    /// <summary>The <c>/</c> filter over the change list or history.</summary>
    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) { Raise(nameof(FilteredFiles)); Raise(nameof(FilteredCommits)); } }
    }

    public IReadOnlyList<GitStatusEntry> FilteredFiles => _filter.Length == 0
        ? _files
        : _files.Where(f => f.RelativePath.Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();

    public IReadOnlyList<GitCommit> FilteredCommits => _filter.Length == 0
        ? _commits
        : _commits.Where(c => c.Subject.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                              || c.Sha.StartsWith(_filter, StringComparison.OrdinalIgnoreCase)).ToList();

    public bool IsOnProtectedBranch => _currentBranch != null && ProtectedBranches.Contains(_currentBranch);
    public bool FocusedFileStaged => PrimaryEntry?.Staged == true;

    // ── Hooks the workspace sets ────────────────────────────────────────

    /// <summary>Runs after each refresh, so the workspace can follow the working tree.</summary>
    public Func<Task> OnRefreshed { get; set; } = () => Task.CompletedTask;

    /// <summary>Unsaved editor buffers in this checkout; a branch switch waits for none.</summary>
    public Func<int> UnsavedEdits { get; set; } = () => 0;

    /// <summary>Where failures go: the workspace's error banner.</summary>
    public Action<string, string> OnError { get; set; } = (_, _) => { };

    /// <summary>
    /// Asks the person before anything destructive: title, message, the
    /// destructive button's label. The view answers with a dialog.
    /// </summary>
    public Func<string, string, string, bool> Confirm { get; set; } = (_, _, _) => false;

    // ── Navigation (j / k, Tab) ─────────────────────────────────────────

    public void MoveSelection(int step)
    {
        switch (_panel)
        {
            case RepoPanel.Changes:
                var ids = FilteredFiles.Select(f => f.Id).ToList();
                FileSelection = Step(ids, _fileSelection.FirstOrDefault(), step) is { } id ? new[] { id } : Array.Empty<string>();
                break;
            case RepoPanel.History:
                CommitSelection = Step(FilteredCommits.Select(c => c.Sha).ToList(), _commitSelection, step);
                break;
        }
    }

    private static string? Step(IReadOnlyList<string> ids, string? current, int step)
    {
        if (ids.Count == 0) return null;
        var index = current == null ? -1 : ids.ToList().IndexOf(current);
        if (index < 0) return ids[0];
        return ids[Math.Clamp(index + step, 0, ids.Count - 1)];
    }

    public void CyclePanel(int step)
    {
        var all = Enum.GetValues<RepoPanel>();
        Panel = all[(Array.IndexOf(all, _panel) + step + all.Length) % all.Length];
    }

    // ── Loading ─────────────────────────────────────────────────────────

    /// <summary>Points the pane at another checkout, clearing everything shown.</summary>
    public void Attach(GitWorkingCopy? copy, IReadOnlySet<string> protectedBranches)
    {
        _copy = copy;
        ProtectedBranches = protectedBranches;
        Files = Array.Empty<GitStatusEntry>();
        Branches = Array.Empty<GitBranch>();
        Commits = Array.Empty<GitCommit>();
        Graph = Array.Empty<GraphRow>();
        _fileSelection = Array.Empty<string>();
        _commitSelection = null;
        DiffText = "";
        CommitSubject = "";
        CommitBody = "";
        Amend = false;
        SkipHooks = false;
        StatusMessage = null;
        ProcessOutput = "";
        LastRefreshedAt = null;
        CurrentBranch = null;
        Ahead = 0;
        Behind = 0;
        Raise(nameof(Copy));
        Raise(nameof(FileSelection));
        Raise(nameof(CommitSelection));
        Raise(nameof(IsOnProtectedBranch));
        Raise(nameof(FilteredFiles));
        Raise(nameof(FilteredCommits));
    }

    public async Task RefreshAsync()
    {
        if (_copy is not { } copy) return;
        try
        {
            var snapshotTask = copy.StatusAsync();
            var branchesTask = copy.BranchListAsync();
            var historyTask = copy.HistoryAsync(200);
            await Task.WhenAll(snapshotTask, branchesTask, historyTask);
            if (!ReferenceEquals(copy, _copy)) return;
            var snapshot = snapshotTask.Result;
            Files = GitStatusEntry.Entries(snapshot);
            CurrentBranch = snapshot.Branch;
            Ahead = snapshot.Ahead;
            Behind = snapshot.Behind;
            Branches = branchesTask.Result;
            Commits = historyTask.Result;
            var (rows, lanes) = CommitGraphBuilder.Build(Commits);
            Graph = rows;
            LaneCount = lanes;
            Raise(nameof(IsOnProtectedBranch));
            Raise(nameof(FilteredFiles));
            Raise(nameof(FilteredCommits));

            var ids = Files.Select(f => f.Id).ToHashSet();
            var kept = _fileSelection.Where(ids.Contains).ToList();
            if (kept.Count == 0 && Files.Count > 0) kept.Add(Files[0].Id);
            _fileSelection = kept;
            Raise(nameof(FileSelection));
            Raise(nameof(PrimaryEntry));
            if (_commitSelection == null || Commits.All(c => c.Sha != _commitSelection))
            {
                _commitSelection = Commits.FirstOrDefault()?.Sha;
                Raise(nameof(CommitSelection));
                Raise(nameof(FocusedCommit));
            }
            await SyncDiffAsync();
            LastRefreshedAt = DateTimeOffset.Now;
            await OnRefreshed();
        }
        catch (RepoException ex)
        {
            Fail("Refresh", ex);
        }
    }

    public async Task SyncDiffAsync()
    {
        if (_copy is not { } copy) { DiffText = ""; return; }
        try
        {
            switch (_panel)
            {
                case RepoPanel.Changes:
                    DiffText = PrimaryEntry is { } entry ? await copy.CombinedDiffAsync(entry.RelativePath) : "";
                    break;
                case RepoPanel.History:
                    DiffText = _commitSelection is { } sha ? await copy.ShowAsync(sha) : "";
                    break;
            }
        }
        catch (RepoException)
        {
            DiffText = "";
        }
    }

    // ── Staging ─────────────────────────────────────────────────────────

    public Task StageAsync(IReadOnlyList<string> paths) =>
        MutateAsync("Stage", () => StageAsync(paths), c => c.StageAsync(paths));

    public Task UnstageAsync(IReadOnlyList<string> paths) =>
        MutateAsync("Unstage", () => UnstageAsync(paths), c => c.UnstageAsync(paths));

    /// <summary>Unstages the selection when all of it is staged, else stages it.</summary>
    public async Task ToggleStageSelectedAsync()
    {
        var selected = _files.Where(f => _fileSelection.Contains(f.Id)).ToList();
        if (selected.Count == 0) return;
        var paths = selected.Select(f => f.RelativePath).Distinct().ToList();
        if (selected.All(f => f.Staged)) await UnstageAsync(paths);
        else await StageAsync(paths);
    }

    public async Task ToggleStageAllAsync()
    {
        var paths = _files.Select(f => f.RelativePath).Distinct().ToList();
        if (paths.Count == 0) return;
        if (_files.All(f => f.Staged)) await UnstageAsync(paths);
        else await StageAsync(paths);
    }

    public async Task UnstageAllAsync()
    {
        var staged = _files.Where(f => f.Staged).Select(f => f.RelativePath).Distinct().ToList();
        if (staged.Count > 0) await UnstageAsync(staged);
    }

    /// <summary>Stage or unstage one chunk through <c>git apply --cached</c>.</summary>
    public Task ApplyChunkAsync(DiffFile file, DiffHunk hunk, bool reverse)
    {
        var patch = file.PatchForHunk(hunk);
        return MutateAsync(reverse ? "Unstage chunk" : "Stage chunk", null, c => c.ApplyPatchAsync(patch, cached: true, reverse: reverse));
    }

    /// <summary>Throws away one chunk of working-tree changes, after the person confirms.</summary>
    public async Task DiscardChunkAsync(DiffFile file, DiffHunk hunk)
    {
        if (!Confirm($"Discard this chunk of {file.DisplayPath}?", "This is irreversible.", "Discard")) return;
        var patch = file.PatchForHunk(hunk);
        await MutateAsync("Discard chunk", null, c => c.ApplyPatchAsync(patch, cached: false, reverse: true));
    }

    /// <summary>Throws away the selected unstaged changes, after the person confirms.</summary>
    public async Task DiscardSelectedAsync()
    {
        var paths = _files.Where(f => _fileSelection.Contains(f.Id) && !f.Staged)
            .Select(f => f.RelativePath).Distinct().OrderBy(p => p).ToList();
        if (paths.Count == 0) return;
        var title = paths.Count == 1 ? $"Discard changes to {paths[0]}?" : $"Discard changes to {paths.Count} files?";
        var message = paths.Count == 1
            ? "This is irreversible. A new, untracked file is deleted."
            : "This is irreversible. New, untracked files are deleted. Files:\n\n" + string.Join("\n", paths);
        if (!Confirm(title, message, "Discard")) return;
        await MutateAsync("Discard", null, c => c.DiscardAsync(paths));
        if (_statusKind != GitStatusKind.Error)
            Note(paths.Count == 1 ? $"Discarded {paths[0]}" : $"Discarded {paths.Count} files", GitStatusKind.Success);
    }

    // ── Commit, branches, network ───────────────────────────────────────

    public async Task<bool> CommitAsync()
    {
        if (_copy is not { } copy || IsBusy) return false;
        IsBusy = true;
        ProcessOutput = "";
        try
        {
            var commit = await copy.CommitAsync(CommitSubject, CommitBody, Amend, !SkipHooks, ProtectedBranches);
            CommitSubject = "";
            CommitBody = "";
            Amend = false;
            SkipHooks = false;
            Note($"Committed {commit.ShortSha}", GitStatusKind.Success);
            IsBusy = false;
            await RefreshAsync();
            return true;
        }
        catch (RepoException ex)
        {
            Fail("Commit", ex);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task CommitAndPushAsync()
    {
        if (await CommitAsync()) await PushAsync();
    }

    /// <summary>Never passes allowProtected: a protected branch is refused, and the refusal is shown.</summary>
    public Task PushAsync()
    {
        var protectedBranches = ProtectedBranches;
        return NetworkAsync("Push", "Pushed", c => c.PushAsync(protectedBranches));
    }

    public Task PullAsync() => NetworkAsync("Pull", "Pulled (fast-forward)", c => c.PullAsync());
    public Task FetchAsync() => NetworkAsync("Fetch", "Fetched", c => c.FetchAsync());

    public async Task SwitchBranchAsync(string name)
    {
        if (UnsavedEdits() > 0)
        {
            OnError("Switch skipped", "Save or revert your unsaved edits first; switching branches changes the files under them.");
            return;
        }
        await NetworkAsync("Switch", $"Switched to {name}", c => c.SwitchBranchAsync(name, create: false));
    }

    /// <summary>Creates a branch at HEAD and switches to it.</summary>
    public Task NewBranchAsync(string name) =>
        NetworkAsync("New branch", $"Switched to new branch {name}", c => c.SwitchBranchAsync(name, create: true));

    public Task TagAsync(GitCommit commit, string name, string? message) =>
        MutateAsync("Add tag", null, c => c.TagAsync(name, commit.Sha, message));

    public Task CreateBranchAsync(GitCommit commit, string name) =>
        MutateAsync("Create branch", null, c => c.CreateBranchAsync(name, commit.Sha));

    public async Task CheckoutAsync(GitCommit commit)
    {
        if (UnsavedEdits() > 0)
        {
            OnError("Checkout skipped", "Save or revert your unsaved edits first.");
            return;
        }
        if (!Confirm($"Check out {commit.ShortSha}?",
                "HEAD will be detached at this commit. Git refuses if it would overwrite your local changes.", "Check Out")) return;
        await MutateAsync("Checkout", null, c => c.CheckoutCommitAsync(commit.Sha));
    }

    public async Task CherryPickAsync(GitCommit commit)
    {
        if (!Confirm($"Cherry-pick {commit.ShortSha} onto {CurrentBranch ?? "HEAD"}?", commit.Subject, "Cherry-Pick")) return;
        await MutateAsync("Cherry-pick", null, c => c.CherryPickAsync(commit.Sha));
    }

    public async Task RevertAsync(GitCommit commit)
    {
        if (!Confirm($"Revert {commit.ShortSha}?", $"A new commit on {CurrentBranch ?? "HEAD"} undoes “{commit.Subject}”.", "Revert")) return;
        await MutateAsync("Revert", null, c => c.RevertAsync(commit.Sha));
    }

    public async Task<string?> PatchAsync(GitCommit commit)
    {
        if (_copy is not { } copy) return null;
        try { return await copy.FormatPatchAsync(commit.Sha); }
        catch (RepoException ex) { Fail("Copy patch", ex); return null; }
    }

    // ── Plumbing ────────────────────────────────────────────────────────

    /// <summary>
    /// A local mutation: run it, refresh, and route an index-lock failure to
    /// the recovery confirmation, which removes the lock only when nothing holds it.
    /// </summary>
    private async Task MutateAsync(string action, Func<Task>? retry, Func<GitWorkingCopy, Task> body)
    {
        if (_copy is not { } copy) return;
        try
        {
            StatusKind = GitStatusKind.Info;
            await body(copy);
        }
        catch (RepoException ex) when (GitIndexLock.Matches(ex.Message) && retry != null)
        {
            var lockPath = GitIndexLock.LockPath(copy.Path);
            var holders = GitIndexLock.Holders(lockPath);
            if (holders.Length > 0)
            {
                OnError($"{action} failed", $"{lockPath} is still held:\n\n{holders}\n\nWait for that process to finish, then try again.");
            }
            else if (Confirm("Git index is locked",
                         $"{action} could not take {lockPath}. Nothing has it open, so a crashed git command most likely left it behind. Delete it and retry?",
                         "Delete Lock File"))
            {
                try
                {
                    GitIndexLock.RemoveStale(lockPath);
                    Note($"Removed index.lock; retrying {action.ToLowerInvariant()}", GitStatusKind.Info);
                    await retry();
                    return;
                }
                catch (Exception lockError) when (lockError is RepoException or IOException or UnauthorizedAccessException)
                {
                    OnError($"{action} failed", lockError.Message);
                }
            }
        }
        catch (RepoException ex)
        {
            Fail(action, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            OnError($"{action} failed", ex.Message);
            StatusKind = GitStatusKind.Error;
        }
        await RefreshAsync();
    }

    /// <summary>A git command whose output is worth showing under the composer.</summary>
    private async Task NetworkAsync(string action, string success, Func<GitWorkingCopy, Task<string>> body)
    {
        if (_copy is not { } copy || IsBusy) return;
        IsBusy = true;
        ProcessOutput = "";
        try
        {
            ProcessOutput = (await body(copy)).Trim();
            Note(success, GitStatusKind.Success);
        }
        catch (RepoException ex)
        {
            Fail(action, ex);
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    private void Fail(string action, RepoException error)
    {
        if (error.Kind == RepoErrorKind.ProtectedBranch)
            OnError($"{action} refused", $"'{error.Subject}' is protected. Create a branch for this work and open a pull request from it.");
        else
            OnError($"{action} failed", error.Message);
        StatusKind = GitStatusKind.Error;
        StatusMessage = null;
    }

    public void Note(string message, GitStatusKind kind)
    {
        StatusMessage = message;
        StatusKind = kind;
    }
}
