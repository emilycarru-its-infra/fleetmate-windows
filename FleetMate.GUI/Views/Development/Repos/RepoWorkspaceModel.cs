using System.IO;
using System.Text;
using FleetMate.Core.Services.Repos;

namespace FleetMate.GUI.Views.Development.Repos;

/// <summary>
/// A file open in the editor. A reference type, so typing does not publish a
/// model change per keystroke: the editor writes <see cref="Text"/> and the
/// model only hears that the document became dirty.
/// </summary>
public sealed class RepoEditorDocument
{
    public RepoEditorDocument(string path, string text, bool isReadOnly, bool byteOrderMark = false, int? revealLine = null)
    {
        Path = path;
        SavedText = text;
        Text = text;
        IsReadOnly = isReadOnly;
        ByteOrderMark = byteOrderMark;
        RevealLine = revealLine;
    }

    /// <summary>Repository-relative, with forward slashes.</summary>
    public string Path { get; }
    /// <summary>What is on disk, as last read or saved.</summary>
    public string SavedText { get; set; }
    /// <summary>What the editor holds now.</summary>
    public string Text { get; set; }
    /// <summary>Too large or not text: shown, never written.</summary>
    public bool IsReadOnly { get; }
    /// <summary>The file started with a UTF-8 byte-order mark, which a save keeps.</summary>
    public bool ByteOrderMark { get; }
    /// <summary>Line to scroll to and select when the editor next shows this document.</summary>
    public int? RevealLine { get; set; }
}

/// <summary>
/// The Repos workspace: tracked checkouts on the left; for the selected one,
/// the git pane (<see cref="GitPaneModel"/>), FleetMate's file browser and
/// editor, and Insights. Everything goes through the same
/// <see cref="RepoManager"/> and registry file as <c>fleetmate repos</c>.
/// </summary>
public sealed class RepoWorkspaceModel : RepoObservable
{
    /// <summary>
    /// Raised after Settings links, unlinks, clones or (un)tracks a repository,
    /// so the workspace reloads its list at once.
    /// </summary>
    public static event Action? RegistryChanged;

    public static void NotifyRegistryChanged() => RegistryChanged?.Invoke();

    public RepoManager Manager { get; }
    public GitPaneModel Git { get; } = new();
    public RepoInsightsModel Insights { get; } = new();

    private IReadOnlyList<RepoRecord> _tracked = Array.Empty<RepoRecord>();
    private IReadOnlyDictionary<string, RepoStatus> _statuses = new Dictionary<string, RepoStatus>();
    private bool _isRefreshing;
    private IReadOnlySet<string> _fetching = new HashSet<string>();
    private string? _selectedId;
    private IReadOnlyList<string> _files = Array.Empty<string>();
    private IReadOnlyList<RepoFileNode> _tree = Array.Empty<RepoFileNode>();
    private IReadOnlySet<string> _expandedFolders = new HashSet<string>();
    private string _fileFilter = "";
    private string? _grepQuery;
    private IReadOnlyList<RepoGrepMatch> _grepResults = Array.Empty<RepoGrepMatch>();
    private bool _isSearching;
    private RepoEditorDocument? _document;
    private bool _isDirty;
    private int _revealToken;
    private string? _notice;

    /// <summary>Unsaved documents by absolute path, across repositories.</summary>
    private readonly Dictionary<string, RepoEditorDocument> _unsaved = new(StringComparer.OrdinalIgnoreCase);

    public RepoWorkspaceModel(RepoManager? manager = null)
    {
        Manager = manager ?? new RepoManager();
        Git.OnRefreshed = WorkingTreeChangedAsync;
        Git.UnsavedEdits = () => SelectedPath is { } root ? UnsavedCount(root) : 0;
        Git.OnError = Report;
    }

    /// <summary>Failures for the view's error banner: title, message.</summary>
    public event Action<string, string>? ErrorReported;

    // ── Repository list ─────────────────────────────────────────────────

    public IReadOnlyList<RepoRecord> Tracked { get => _tracked; private set => Set(ref _tracked, value); }
    public IReadOnlyDictionary<string, RepoStatus> Statuses { get => _statuses; private set => Set(ref _statuses, value); }
    public bool IsRefreshing { get => _isRefreshing; private set => Set(ref _isRefreshing, value); }
    public IReadOnlySet<string> Fetching { get => _fetching; private set => Set(ref _fetching, value); }

    public string? SelectedId
    {
        get => _selectedId;
        set
        {
            if (_selectedId == value) return;
            var previousRoot = SelectedPath;
            _selectedId = value;
            Raise();
            Raise(nameof(Selected));
            Raise(nameof(SelectedPath));
            Raise(nameof(SelectedStatus));
            SelectionChanged(previousRoot);
        }
    }

    public RepoRecord? Selected => _tracked.FirstOrDefault(r => r.Id == _selectedId);
    public string? SelectedPath => Selected?.Local?.Path;
    public RepoStatus? SelectedStatus => _selectedId != null ? _statuses.GetValueOrDefault(_selectedId) : null;

    private GitWorkingCopy? CopyOrNull => SelectedPath is { } path ? new GitWorkingCopy(path) : null;

    /// <summary>Rereads the registry: one JSON file and the catalog cache, no git.</summary>
    public void ReloadRecords()
    {
        try
        {
            Tracked = Manager.Records().Where(r => r.IsTracked)
                .OrderBy(r => r.Key.DisplayName, NaturalComparer.Instance).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Report("Could not read the repository registry", ex.Message);
        }
        if (_selectedId != null && _tracked.All(r => r.Id != _selectedId)) SelectedId = null;
        if (_selectedId == null && _tracked.Count > 0)
        {
            var remembered = RepoWorkspacePreferences.Read("selected");
            SelectedId = _tracked.FirstOrDefault(r => r.Id == remembered)?.Id ?? _tracked[0].Id;
        }
        Raise(nameof(Selected));
    }

    /// <summary>Local status of every tracked repository: no network.</summary>
    public async Task RefreshStatusesAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            var results = await Manager.StatusAsync(_tracked);
            Statuses = results.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
            Raise(nameof(SelectedStatus));
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public async Task FetchAsync(IReadOnlyList<string> ids)
    {
        var records = _tracked.Where(r => ids.Contains(r.Id)).ToList();
        if (records.Count == 0) return;
        Fetching = _fetching.Union(ids).ToHashSet();
        var results = await Manager.RunAsync(RepoBatchOperation.Fetch, records);
        Fetching = _fetching.Except(ids).ToHashSet();
        var failures = results.Where(r => !r.Succeeded).ToList();
        if (failures.Count > 0)
            Report("Fetch failed", string.Join("\n", failures.Select(f => $"{f.DisplayName}: {f.Error ?? "unknown error"}")));
        await RefreshStatusesAsync();
        if (ids.Contains(_selectedId)) await Git.RefreshAsync();
    }

    // ── Selection ───────────────────────────────────────────────────────

    private void SelectionChanged(string? previousRoot)
    {
        Files = Array.Empty<string>();
        Tree = Array.Empty<RepoFileNode>();
        ExpandedFolders = new HashSet<string>();
        ClearGrep();
        ParkCurrentDocument(previousRoot);
        SetDocument(null, false);
        if (_selectedId != null) RepoWorkspacePreferences.Write("selected", _selectedId);
        Git.Attach(CopyOrNull, Selected is { } record ? Manager.ProtectedBranches(record) : new HashSet<string>());
        _ = LoadSelectedAsync();
    }

    public async Task LoadSelectedAsync()
    {
        if (CopyOrNull is not { } copy || _selectedId is not { } id) return;
        try
        {
            var paths = await copy.ListFilesAsync();
            if (id != _selectedId) return;
            Files = paths;
            Tree = RepoFileTree.Build(paths);
        }
        catch (RepoException ex)
        {
            Report("Could not list files", ex.Message);
        }
        await Git.RefreshAsync();
    }

    /// <summary>
    /// After git changed the working tree (commit, discard, pull, branch
    /// switch): the file list, an unedited open file, and the list row.
    /// </summary>
    private async Task WorkingTreeChangedAsync()
    {
        if (CopyOrNull is not { } copy) return;
        try
        {
            var paths = await copy.ListFilesAsync();
            Files = paths;
            Tree = RepoFileTree.Build(paths);
            ReloadOpenDocumentFromDisk();
        }
        catch (RepoException) { }
        if (Selected is { } record)
        {
            var status = await Manager.StatusAsync(record);
            Statuses = new Dictionary<string, RepoStatus>(_statuses) { [record.Id] = status };
            Raise(nameof(SelectedStatus));
        }
    }

    // ── Files and search ────────────────────────────────────────────────

    public IReadOnlyList<string> Files { get => _files; private set => Set(ref _files, value); }
    public IReadOnlyList<RepoFileNode> Tree { get => _tree; private set => Set(ref _tree, value); }
    public IReadOnlySet<string> ExpandedFolders { get => _expandedFolders; set => Set(ref _expandedFolders, value); }

    /// <summary>The toolbar search field's text while Repos shows: it filters the file tree.</summary>
    public string FileFilter { get => _fileFilter; set => Set(ref _fileFilter, value ?? ""); }

    public string? GrepQuery { get => _grepQuery; private set => Set(ref _grepQuery, value); }
    public IReadOnlyList<RepoGrepMatch> GrepResults { get => _grepResults; private set => Set(ref _grepResults, value); }
    public bool IsSearching { get => _isSearching; private set => Set(ref _isSearching, value); }

    public void ToggleFolder(string path)
    {
        var next = new HashSet<string>(_expandedFolders);
        if (!next.Remove(path)) next.Add(path);
        ExpandedFolders = next;
    }

    /// <summary>Searches every file's contents for <paramref name="query"/> (literal, ignoring case).</summary>
    public async Task GrepAsync(string query)
    {
        var pattern = query.Trim();
        if (CopyOrNull is not { } copy || pattern.Length == 0) return;
        IsSearching = true;
        try
        {
            GrepResults = await copy.GrepAsync(pattern, ignoreCase: true, fixedStrings: true, limit: 500);
            GrepQuery = pattern;
        }
        catch (RepoException ex)
        {
            Report("Search failed", ex.Message);
        }
        finally
        {
            IsSearching = false;
        }
    }

    public void ClearGrep()
    {
        GrepQuery = null;
        GrepResults = Array.Empty<RepoGrepMatch>();
    }

    // ── Editor ──────────────────────────────────────────────────────────

    public RepoEditorDocument? Document { get => _document; private set => Set(ref _document, value); }
    public bool IsDirty { get => _isDirty; private set => Set(ref _isDirty, value); }
    /// <summary>Bumped to scroll the open document to its reveal line again.</summary>
    public int RevealToken { get => _revealToken; private set => Set(ref _revealToken, value); }
    public string? Notice { get => _notice; private set => Set(ref _notice, value); }

    private void SetDocument(RepoEditorDocument? document, bool dirty)
    {
        Document = document;
        IsDirty = dirty;
    }

    public void Open(string path, int? line = null)
    {
        if (CopyOrNull is not { } copy) return;
        if (_document is { } current && current.Path == path)
        {
            current.RevealLine = line;
            RevealToken++;
            return;
        }
        ParkCurrentDocument(copy.Path);
        var absolute = System.IO.Path.Combine(copy.Path, path.Replace('/', '\\'));
        if (_unsaved.Remove(absolute, out var parked))
        {
            parked.RevealLine = line;
            SetDocument(parked, true);
            return;
        }
        try
        {
            var data = copy.ReadFile(path);
            if (RepoTextFile.Decode(data) is { } text)
            {
                SetDocument(new RepoEditorDocument(path, text, data.Length > RepoTextFile.EditableLimit,
                    RepoTextFile.HasByteOrderMark(data), line), false);
            }
            else
            {
                SetDocument(new RepoEditorDocument(path,
                    $"This file is not text ({data.Length:N0} bytes). Reveal it in Explorer to open it with another app.", true), false);
            }
        }
        catch (Exception ex) when (ex is RepoException or IOException or UnauthorizedAccessException)
        {
            Report($"Could not open {path}", ex.Message);
        }
    }

    public void EditorTextChanged()
    {
        if (_document is not { } document) return;
        IsDirty = document.Text != document.SavedText;
    }

    public void Save()
    {
        if (CopyOrNull is not { } copy || _document is not { IsReadOnly: false } document || !IsDirty) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(document.Text);
            if (document.ByteOrderMark) bytes = Encoding.UTF8.GetPreamble().Concat(bytes).ToArray();
            copy.WriteFile(document.Path, bytes);
            document.SavedText = document.Text;
            IsDirty = false;
            Flash($"Saved {System.IO.Path.GetFileName(document.Path)}");
            _ = Git.RefreshAsync();
        }
        catch (Exception ex) when (ex is RepoException or IOException or UnauthorizedAccessException)
        {
            Report("Save failed", ex.Message);
        }
    }

    /// <summary>
    /// After git rewrote the working tree (discard, pull, branch switch), an
    /// open file with no unsaved edits shows what is now on disk.
    /// </summary>
    private void ReloadOpenDocumentFromDisk()
    {
        if (_document is not { } document || IsDirty || CopyOrNull is not { } copy) return;
        SetDocument(null, false);
        if (File.Exists(System.IO.Path.Combine(copy.Path, document.Path.Replace('/', '\\')))) Open(document.Path);
    }

    /// <summary>Throws away unsaved edits to the open file (after the view confirms).</summary>
    public void Revert()
    {
        if (_document is not { } document) return;
        document.Text = document.SavedText;
        SetDocument(null, false);
        Open(document.Path);
    }

    public void CloseEditor()
    {
        ParkCurrentDocument(SelectedPath);
        SetDocument(null, false);
    }

    /// <summary>Whether <paramref name="path"/> in the selected repository has edits not yet saved.</summary>
    public bool HasUnsavedEdits(string path)
    {
        if (IsDirty && _document?.Path == path) return true;
        return SelectedPath is { } root && _unsaved.ContainsKey(System.IO.Path.Combine(root, path.Replace('/', '\\')));
    }

    /// <summary>
    /// Unsaved edits are never dropped and never block with a prompt: moving
    /// to another file keeps them in memory, and reopening the file brings them back.
    /// </summary>
    private void ParkCurrentDocument(string? root)
    {
        if (!IsDirty || _document is not { } document || root == null) return;
        _unsaved[System.IO.Path.Combine(root, document.Path.Replace('/', '\\'))] = document;
        IsDirty = false;
    }

    public int UnsavedCount(string root) =>
        _unsaved.Keys.Count(k => k.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) + (IsDirty ? 1 : 0);

    // ── Feedback ────────────────────────────────────────────────────────

    public void Report(string title, string message) => ErrorReported?.Invoke(title, message);

    private async void Flash(string message)
    {
        Notice = message;
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (Notice == message) Notice = null;
    }
}

/// <summary>
/// What the Insights panel shows and the statistics behind it. The numbers come
/// from the same code as <c>fleetmate repos stats</c>, so the app and agents agree.
/// </summary>
public sealed class RepoInsightsModel : RepoObservable
{
    public enum InsightsScope { Repository, All }

    public enum InsightsPeriod { Days30, Days90, Months6, Year1, All }

    public enum InsightsGranularity { Automatic, Day, Week, Month }

    private InsightsScope _scope = RepoWorkspacePreferences.ReadEnum("insights.scope", InsightsScope.Repository);
    private InsightsPeriod _period = RepoWorkspacePreferences.ReadEnum("insights.period", InsightsPeriod.Days90);
    private InsightsGranularity _granularity = RepoWorkspacePreferences.ReadEnum("insights.granularity", InsightsGranularity.Automatic);
    private RepoStatsReport? _report;
    private RepoStatsSummary? _summary;
    private bool _isLoading;
    private string? _error;
    private string? _shownKey;

    public InsightsScope Scope { get => _scope; set { if (Set(ref _scope, value)) RepoWorkspacePreferences.Write("insights.scope", value.ToString()); } }
    public InsightsPeriod Period { get => _period; set { if (Set(ref _period, value)) RepoWorkspacePreferences.Write("insights.period", value.ToString()); } }
    public InsightsGranularity Granularity { get => _granularity; set { if (Set(ref _granularity, value)) RepoWorkspacePreferences.Write("insights.granularity", value.ToString()); } }
    public RepoStatsReport? Report { get => _report; private set => Set(ref _report, value); }
    public RepoStatsSummary? Summary { get => _summary; private set => Set(ref _summary, value); }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public string? Error { get => _error; private set => Set(ref _error, value); }
    public string? ShownKey { get => _shownKey; private set => Set(ref _shownKey, value); }

    public static string Title(InsightsPeriod period) => period switch
    {
        InsightsPeriod.Days30 => "30 Days",
        InsightsPeriod.Days90 => "90 Days",
        InsightsPeriod.Months6 => "6 Months",
        InsightsPeriod.Year1 => "1 Year",
        _ => "All Time",
    };

    /// <summary>The <c>--since</c> spelling of a period, for the CLI hint and the range.</summary>
    public static string SinceArgument(InsightsPeriod period) => period switch
    {
        InsightsPeriod.Days30 => "30d",
        InsightsPeriod.Days90 => "90d",
        InsightsPeriod.Months6 => "6m",
        InsightsPeriod.Year1 => "1y",
        _ => "all",
    };

    public static string Title(InsightsGranularity granularity) => granularity switch
    {
        InsightsGranularity.Day => "Daily",
        InsightsGranularity.Week => "Weekly",
        InsightsGranularity.Month => "Monthly",
        _ => "Automatic",
    };

    private static RepoStatsBucket? Bucket(InsightsGranularity granularity) => granularity switch
    {
        InsightsGranularity.Day => RepoStatsBucket.Day,
        InsightsGranularity.Week => RepoStatsBucket.Week,
        InsightsGranularity.Month => RepoStatsBucket.Month,
        _ => null,
    };

    public string Key(RepoRecord? selected, IReadOnlyList<RepoRecord> tracked)
    {
        var target = _scope == InsightsScope.All ? string.Join(",", tracked.Select(r => r.Id)) : selected?.Id ?? "";
        return string.Join("|", _scope, _period, _granularity, target);
    }

    public async Task LoadAsync(RepoManager manager, RepoRecord? selected, IReadOnlyList<RepoRecord> tracked)
    {
        var key = Key(selected, tracked);
        IsLoading = true;
        try
        {
            var since = _period == InsightsPeriod.All ? null : RepoStatsRange.ParseSince(SinceArgument(_period));
            var bucket = Bucket(_granularity);
            if (_scope == InsightsScope.Repository)
            {
                if (selected == null) { Report = null; ShownKey = key; return; }
                try
                {
                    var result = await manager.StatsAsync(selected, since, bucket: bucket, top: 12);
                    if (key != Key(selected, tracked)) return;
                    Report = result;
                    Error = null;
                }
                catch (RepoException ex)
                {
                    Report = null;
                    Error = ex.Message;
                }
            }
            else
            {
                var result = await manager.StatsSummaryAsync(tracked, since, bucket: bucket, top: 12);
                if (key != Key(selected, tracked)) return;
                Summary = result;
                Error = null;
            }
            ShownKey = key;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
