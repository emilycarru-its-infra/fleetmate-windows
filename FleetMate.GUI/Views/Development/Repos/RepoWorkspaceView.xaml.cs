using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FleetMate.Core.Services.Repos;
using FleetMate.Core.Services.Terminal;
using FleetMate.GUI.Views.Manage;
using FleetMate.GUI.Views.Shared;
using FleetMate.GUI.Views.Terminal;
using FleetMate.Core.Shared;

namespace FleetMate.GUI.Views.Development.Repos;

/// <summary>One change in the Changes list. A path with staged and unstaged edits has a row for each side.</summary>
public sealed record ChangeRow(GitStatusEntry Entry, string Root)
{
    public string Id => Entry.Id;
    public bool Staged => Entry.Staged;
    public string Letter => Entry.Letter;
    public string FileName => System.IO.Path.GetFileName(Entry.RelativePath);
    public string Folder
    {
        get
        {
            var folder = System.IO.Path.GetDirectoryName(Entry.RelativePath.Replace('/', '\\'));
            return string.IsNullOrEmpty(folder) ? "" : "  " + folder.Replace('\\', '/');
        }
    }
    public string Side => Entry.Staged ? "staged" : "";
    public string StageLabel => Entry.Staged ? "Unstage" : "Stage";
    public bool CanDiscard => !Entry.Staged;
    public string Tooltip => Entry.OriginalPath is { } from ? $"{from} → {Entry.RelativePath}" : Entry.RelativePath;
    public string AbsolutePath => System.IO.Path.Combine(Root, Entry.RelativePath.Replace('/', '\\'));

    public Brush LetterBrush => Entry.Kind switch
    {
        GitEntryKind.Added or GitEntryKind.Untracked => RepoBrushes.Added,
        GitEntryKind.Deleted or GitEntryKind.Conflicted => RepoBrushes.Removed,
        GitEntryKind.Renamed or GitEntryKind.Copied => RepoBrushes.Series[3],
        _ => RepoBrushes.Series[0],
    };
}

/// <summary>A branch, tag or HEAD badge on a commit.</summary>
public sealed record RefBadge(string Name, Brush Background, Brush Border, Brush Foreground);

/// <summary>One commit in History, with its slice of the lane graph.</summary>
public sealed record HistoryRow(GitCommit Commit, GraphRow? Graph, int LaneCount)
{
    public string Sha => Commit.Sha;
    public string Subject => Commit.Subject;
    public string Byline => $"{Commit.ShortSha} · {Commit.Author} · {RepoShell.Relative(Commit.Date)}";

    public IReadOnlyList<RefBadge> Refs => Commit.Refs
        .Where(r => r.Kind != GitRefKind.Head)
        .Select(r => r.Kind switch
        {
            GitRefKind.Tag => new RefBadge(r.Name, Brushes.Transparent, RepoBrushes.Series[3], RepoBrushes.Series[3]),
            GitRefKind.RemoteBranch => new RefBadge(r.Name, Brushes.Transparent, RepoBrushes.Secondary, RepoBrushes.Secondary),
            _ => new RefBadge(r.IsHead ? "HEAD → " + r.Name : r.Name, RepoBrushes.Accent, RepoBrushes.Accent, Brushes.White),
        })
        .ToList();
}

/// <summary>One visible line of the file tree.</summary>
public sealed record FileRow(RepoFileRow Row, bool IsUnsaved)
{
    public string Path => Row.Node.Path;
    public string Name => Row.Node.Name;
    public bool IsFolder => Row.Node.IsFolder;
    public Thickness Indent => new(Row.Depth * 14, 1, 0, 1);
    public string Chevron => Row.IsExpanded ? "" : "";
    public double ChevronOpacity => IsFolder ? 1 : 0;
    public string Icon => IsFolder ? "" : "";
    public Brush IconBrush => IsFolder ? RepoBrushes.Accent : RepoBrushes.Secondary;
    public Visibility UnsavedVisibility => IsUnsaved ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>One content-search match, grouped under its file.</summary>
public sealed record GrepRow(RepoGrepMatch Match)
{
    public string Path => Match.Path;
    public string LineLabel => Match.Line.ToString();
    public string Text => Match.Text.Trim();
}

/// <summary>
/// The Repos workspace's detail for the selected repository: the git pane
/// ported from FleetMate for Mac (header with branch menu, Fetch, Pull, Push;
/// commit composer above the change list; structured diff with per-chunk
/// Stage, Unstage and Discard; History with the lane graph and commit
/// actions), plus the Files panel's tree, content search and editor, and
/// Insights. Lazygit-style keys work while the pane has focus outside a text
/// field. Destructive steps — discarding files or a chunk, checking out,
/// deleting a stale index lock — ask first.
/// </summary>
public partial class RepoWorkspaceView : UserControl
{
    private RepoWorkspaceModel? _model;
    private bool _syncing;
    private readonly DispatcherTimer _errorTimer = new() { Interval = TimeSpan.FromSeconds(12) };

    /// <summary>
    /// The filter the toolbar search field drives while Repos shows: it filters
    /// the file tree, and Enter searches every file's contents.
    /// </summary>
    public TextBox SearchBox { get; } = new() { Visibility = Visibility.Collapsed };

    public RepoWorkspaceView()
    {
        InitializeComponent();
        AgentContextMenu.Attach(FilesList, o => o is FileRow row && _model?.Selected is { } record ? AgentContexts.File(row.Path, record) : null);
        AgentContextMenu.Attach(GrepList, o => o is GrepRow row && _model?.Selected is { } record ? AgentContexts.File(row.Path, record, row.Match.Line) : null);
        ((Grid)Content).Children.Add(SearchBox);
        SearchBox.TextChanged += (_, _) => { if (_model != null) _model.FileFilter = SearchBox.Text; };
        _errorTimer.Tick += (_, _) => { _errorTimer.Stop(); ErrorBanner.Visibility = Visibility.Collapsed; };
        PreviewKeyDown += OnKey;
        Editor.TextEdited += () => _model?.EditorTextChanged();
        Editor.SaveRequested += () => _model?.Save();
        DiffView.StageChunk = (file, hunk) => _model?.Git.ApplyChunkAsync(file, hunk, reverse: false) ?? Task.CompletedTask;
        DiffView.UnstageChunk = (file, hunk) => _model?.Git.ApplyChunkAsync(file, hunk, reverse: true) ?? Task.CompletedTask;
        DiffView.DiscardChunk = (file, hunk) => _model?.Git.DiscardChunkAsync(file, hunk) ?? Task.CompletedTask;
        Insights.OpenFile = path => OpenInEditor(path);
        BuildHelp();
    }

    /// <summary>Runs the content search for the toolbar field's Enter.</summary>
    public async void SubmitSearch()
    {
        if (_model == null || SearchBox.Text.Trim().Length == 0) return;
        _model.Git.Panel = RepoPanel.Files;
        await _model.GrepAsync(SearchBox.Text);
    }

    public void Attach(RepoWorkspaceModel model)
    {
        _model = model;
        Insights.Attach(model);
        model.Git.Confirm = Confirm;
        model.ErrorReported += ShowError;
        model.PropertyChanged += OnModelChanged;
        model.Git.PropertyChanged += OnGitChanged;
        RenderAll();
    }

    // ── Model to view ───────────────────────────────────────────────────

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RepoWorkspaceModel.SelectedId):
            case nameof(RepoWorkspaceModel.Tracked):
                RenderAll();
                break;
            case nameof(RepoWorkspaceModel.SelectedStatus):
                RenderHeader();
                break;
            case nameof(RepoWorkspaceModel.Tree):
            case nameof(RepoWorkspaceModel.ExpandedFolders):
            case nameof(RepoWorkspaceModel.FileFilter):
            case nameof(RepoWorkspaceModel.GrepQuery):
            case nameof(RepoWorkspaceModel.GrepResults):
            case nameof(RepoWorkspaceModel.IsSearching):
                RenderFiles();
                break;
            case nameof(RepoWorkspaceModel.Document):
                RenderEditor(reload: true);
                RenderFiles();
                break;
            case nameof(RepoWorkspaceModel.IsDirty):
            case nameof(RepoWorkspaceModel.Notice):
                RenderEditor(reload: false);
                break;
            case nameof(RepoWorkspaceModel.RevealToken):
                Editor.Reveal();
                break;
        }
    }

    private void OnGitChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(GitPaneModel.Panel):
                RenderPanel();
                break;
            case nameof(GitPaneModel.Files):
            case nameof(GitPaneModel.FilteredFiles):
            case nameof(GitPaneModel.FileSelection):
                RenderChanges();
                RenderHeader();
                break;
            case nameof(GitPaneModel.Commits):
            case nameof(GitPaneModel.FilteredCommits):
            case nameof(GitPaneModel.CommitSelection):
                RenderHistory();
                break;
            case nameof(GitPaneModel.DiffText):
                RenderDiff();
                break;
            case nameof(GitPaneModel.CurrentBranch):
            case nameof(GitPaneModel.Ahead):
            case nameof(GitPaneModel.Behind):
            case nameof(GitPaneModel.IsOnProtectedBranch):
                RenderHeader();
                RenderComposer();
                break;
            case nameof(GitPaneModel.CommitSubject):
            case nameof(GitPaneModel.CommitBody):
            case nameof(GitPaneModel.Amend):
            case nameof(GitPaneModel.SkipHooks):
            case nameof(GitPaneModel.IsBusy):
            case nameof(GitPaneModel.StatusMessage):
            case nameof(GitPaneModel.StatusKind):
            case nameof(GitPaneModel.ProcessOutput):
            case nameof(GitPaneModel.LastRefreshedAt):
                RenderComposer();
                break;
        }
    }

    private void RenderAll()
    {
        var hasSelection = _model?.Selected != null;
        NoSelection.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        Workspace.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        RenderHeader();
        RenderPanel();
        RenderComposer();
        RenderChanges();
        RenderHistory();
        RenderFiles();
        RenderDiff();
        RenderEditor(reload: true);
    }

    private void RenderHeader()
    {
        if (_model == null) return;
        var git = _model.Git;
        BranchLabel.Text = git.CurrentBranch ?? "(detached)";
        DirtyDot.Visibility = git.Files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SyncLabel.Text = (git.Ahead > 0 ? $"↑{git.Ahead} " : "") + (git.Behind > 0 ? $"↓{git.Behind}" : "");
        BranchButton.ToolTip = "Switch branch, or create one at HEAD";
        PushButton.ToolTip = git.IsOnProtectedBranch
            ? $"'{git.CurrentBranch}' is protected: pushing it is refused. Push a branch and open a pull request."
            : "git push (P)";
        var hasAgents = _model.SelectedStatus?.AgentsFile != null
                        || (_model.SelectedPath is { } root && File.Exists(System.IO.Path.Combine(root, "AGENTS.md")));
        AgentsButton.Visibility = hasAgents ? Visibility.Visible : Visibility.Collapsed;
        NoAgentsLabel.Visibility = hasAgents ? Visibility.Collapsed : Visibility.Visible;
        RevealButton.ToolTip = _model.SelectedPath is { } path ? $"Reveal {RepoShell.Abbreviate(path)} in Explorer" : null;
    }

    private void RenderPanel()
    {
        if (_model == null) return;
        var panel = _model.Git.Panel;
        static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        Insights.Visibility = Show(panel == RepoPanel.Insights);
        PanelGrid.Visibility = Show(panel != RepoPanel.Insights);
        Composer.Visibility = Show(panel == RepoPanel.Changes);
        FilesHeader.Visibility = Show(panel == RepoPanel.Files);
        ChangesList.Visibility = Show(panel == RepoPanel.Changes);
        HistoryList.Visibility = Show(panel == RepoPanel.History);
        FilterBar.Visibility = Show(panel is RepoPanel.Changes or RepoPanel.History && _model.Git.Filter.Length > 0 || FilterBar.IsKeyboardFocusWithin);
        DiffView.Visibility = Show(panel is RepoPanel.Changes or RepoPanel.History);
        EditorPane.Visibility = Show(panel == RepoPanel.Files);
        RenderFiles();
        RenderDiff();
        RenderListEmpty();
    }

    private void RenderComposer()
    {
        if (_model == null) return;
        var git = _model.Git;
        _syncing = true;
        if (SubjectBox.Text != git.CommitSubject) SubjectBox.Text = git.CommitSubject;
        if (BodyBox.Text != git.CommitBody) BodyBox.Text = git.CommitBody;
        AmendBox.IsChecked = git.Amend;
        SkipHooksBox.IsChecked = git.SkipHooks;
        _syncing = false;
        ProtectedLabel.Text = $"🔒 {git.CurrentBranch} is protected";
        ProtectedLabel.Visibility = git.IsOnProtectedBranch ? Visibility.Visible : Visibility.Collapsed;
        var canCommit = git.CommitSubject.Trim().Length > 0 && !git.IsBusy;
        CommitButton.IsEnabled = canCommit;
        CommitPushButton.IsEnabled = canCommit;
        StageAllButton.IsEnabled = git.Files.Count > 0;
        BusyRing.Visibility = git.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        if (git.StatusMessage is { } message)
        {
            StatusText.Text = (git.StatusKind == GitStatusKind.Success ? "✓ " : git.StatusKind == GitStatusKind.Error ? "⚠ " : "") + message;
            StatusText.Foreground = git.StatusKind switch
            {
                GitStatusKind.Success => RepoBrushes.Success,
                GitStatusKind.Error => RepoBrushes.Warning,
                _ => RepoBrushes.Secondary,
            };
        }
        else
        {
            StatusText.Text = git.LastRefreshedAt is { } at ? $"Refreshed {RepoShell.Relative(at)}" : "";
            StatusText.Foreground = RepoBrushes.Secondary;
        }
        OutputText.Text = git.ProcessOutput;
        OutputPanel.Visibility = git.ProcessOutput.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderChanges()
    {
        if (_model == null) return;
        var root = _model.SelectedPath ?? "";
        var rows = _model.Git.FilteredFiles.Select(e => new ChangeRow(e, root)).ToList();
        _syncing = true;
        try
        {
            ChangesList.ItemsSource = rows;
            ChangesList.SelectedItems.Clear();
            foreach (var row in rows.Where(r => _model.Git.FileSelection.Contains(r.Id))) ChangesList.SelectedItems.Add(row);
        }
        finally
        {
            _syncing = false;
        }
        RenderListEmpty();
    }

    private void RenderHistory()
    {
        if (_model == null) return;
        var git = _model.Git;
        var graph = git.Commits.Select((c, i) => (c.Sha, Row: i < git.Graph.Count ? git.Graph[i] : null)).ToDictionary(x => x.Sha, x => x.Row);
        var filtered = git.Filter.Length > 0;
        var rows = git.FilteredCommits.Select(c => new HistoryRow(c, filtered ? null : graph.GetValueOrDefault(c.Sha), git.LaneCount)).ToList();
        _syncing = true;
        try
        {
            HistoryList.ItemsSource = rows;
            HistoryList.SelectedItem = rows.FirstOrDefault(r => r.Sha == git.CommitSelection);
            if (HistoryList.SelectedItem != null && HistoryList.Visibility == Visibility.Visible) HistoryList.ScrollIntoView(HistoryList.SelectedItem);
        }
        finally
        {
            _syncing = false;
        }
        RenderListEmpty();
    }

    private void RenderFiles()
    {
        if (_model == null) return;
        var showFiles = _model.Git.Panel == RepoPanel.Files;
        var searching = _model.GrepQuery != null;
        FilesList.Visibility = showFiles && !searching ? Visibility.Visible : Visibility.Collapsed;
        GrepList.Visibility = showFiles && searching ? Visibility.Visible : Visibility.Collapsed;
        ShowFilesButton.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        SearchRing.Visibility = _model.IsSearching ? Visibility.Visible : Visibility.Collapsed;
        FilesTitle.Text = searching ? $"Search: {_model.GrepQuery}" : "Files";
        var filter = _model.FileFilter.Trim();
        FilesHint.Text = searching
            ? $"{_model.GrepResults.Count} match{(_model.GrepResults.Count == 1 ? "" : "es")}"
            : filter.Length > 0 ? "Press Enter to search contents" : $"{_model.Files.Count:N0} files";
        if (!showFiles) return;

        if (searching)
        {
            var view = CollectionViewSource.GetDefaultView(_model.GrepResults.Select(m => new GrepRow(m)).ToList());
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GrepRow.Path)));
            GrepList.ItemsSource = view;
        }
        else
        {
            var nodes = RepoFileTree.Filter(_model.Tree, filter);
            var expanded = filter.Length > 0 ? RepoFileTree.FolderPaths(nodes) : _model.ExpandedFolders;
            var rows = RepoFileTree.VisibleRows(nodes, expanded)
                .Select(r => new FileRow(r, !r.Node.IsFolder && _model.HasUnsavedEdits(r.Node.Path))).ToList();
            _syncing = true;
            FilesList.ItemsSource = rows;
            FilesList.SelectedItem = rows.FirstOrDefault(r => r.Path == _model.Document?.Path);
            _syncing = false;
        }
        RenderListEmpty();
    }

    private void RenderListEmpty()
    {
        if (_model == null) return;
        string? text = _model.Git.Panel switch
        {
            RepoPanel.Changes when _model.Git.FilteredFiles.Count == 0 =>
                _model.Git.Files.Count == 0 ? "No changes. The working tree matches HEAD." : "No changes match the filter.",
            RepoPanel.History when _model.Git.FilteredCommits.Count == 0 =>
                _model.Git.Commits.Count == 0 ? "No commits yet." : "No commits match the filter.",
            RepoPanel.Files when _model.GrepQuery != null && _model.GrepResults.Count == 0 && !_model.IsSearching =>
                $"Nothing in this repository contains “{_model.GrepQuery}”.",
            RepoPanel.Files when _model.GrepQuery == null && _model.Tree.Count == 0 => "Loading…",
            RepoPanel.Files when _model.GrepQuery == null && FilesList.Items.Count == 0 => "No file names match.",
            _ => null,
        };
        ListEmpty.Text = text ?? "";
        ListEmpty.Visibility = text != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderDiff()
    {
        if (_model == null) return;
        var git = _model.Git;
        switch (git.Panel)
        {
            case RepoPanel.Changes:
                DiffView.Show(git.DiffText, workingTree: true, anyStaged: git.FocusedFileStaged,
                    git.Files.Count == 0 ? "Nothing to commit." : "Select a change to see its diff.");
                break;
            case RepoPanel.History:
                DiffView.Show(git.DiffText, workingTree: false, anyStaged: false, "Select a commit to see what it changed.");
                break;
        }
    }

    private void RenderEditor(bool reload)
    {
        if (_model == null) return;
        var document = _model.Document;
        if (reload) Editor.Show(document);
        EditorEmpty.Visibility = document == null ? Visibility.Visible : Visibility.Collapsed;
        Editor.Visibility = document == null ? Visibility.Hidden : Visibility.Visible;
        EditorTitle.Text = document == null
            ? "Editor"
            : document.Path + (_model.IsDirty ? "  · Edited" : "") + (document.IsReadOnly ? "  · Read only" : "");
        RevertButton.IsEnabled = _model.IsDirty;
        CloseButton.Visibility = document == null ? Visibility.Collapsed : Visibility.Visible;
        RevertButton.Visibility = CloseButton.Visibility;
        SaveButton.IsEnabled = _model.IsDirty && document is { IsReadOnly: false };
        NoticeText.Text = _model.Notice ?? "";
    }

    // ── Errors and confirmations ────────────────────────────────────────

    private void ShowError(string title, string message)
    {
        ErrorTitle.Text = title;
        ErrorText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
        _errorTimer.Stop();
        _errorTimer.Start();
    }

    private void OnDismissError(object sender, RoutedEventArgs e)
    {
        _errorTimer.Stop();
        ErrorBanner.Visibility = Visibility.Collapsed;
    }

    private bool Confirm(string title, string message, string action)
    {
        var owner = Window.GetWindow(this);
        var text = $"{message}\n\nChoose OK to {action.ToLowerInvariant()}, or Cancel to leave everything as it is.";
        var result = owner != null
            ? MessageBox.Show(owner, text, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
            : MessageBox.Show(text, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        return result == MessageBoxResult.OK;
    }

    // ── Header actions ──────────────────────────────────────────────────

    private void OnBranchMenu(object sender, RoutedEventArgs e)
    {
        if (_model == null) return;
        var menu = new ContextMenu { PlacementTarget = BranchButton, Placement = PlacementMode.Bottom };
        foreach (var branch in _model.Git.Branches)
        {
            var title = branch.UpstreamName is { } upstream ? $"{branch.Name}   →  {upstream}" : branch.Name;
            var item = new MenuItem { Header = title, IsCheckable = false, IsChecked = branch.IsCurrent };
            var name = branch.Name;
            item.Click += async (_, _) => { if (!branch.IsCurrent) await _model.Git.SwitchBranchAsync(name); };
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var create = new MenuItem { Header = "New Branch…" };
        create.Click += async (_, _) =>
        {
            var name = TextPromptDialog.Ask(Window.GetWindow(this), "New Branch",
                $"Create a branch at HEAD ({_model.Git.CurrentBranch ?? "detached"}) and switch to it:", "feature/");
            if (name != null) await _model.Git.NewBranchAsync(name);
        };
        menu.Items.Add(create);
        menu.IsOpen = true;
    }

    private async void OnFetch(object sender, RoutedEventArgs e) { if (_model != null) await _model.Git.FetchAsync(); }
    private async void OnPull(object sender, RoutedEventArgs e) { if (_model != null) await _model.Git.PullAsync(); }
    private async void OnPush(object sender, RoutedEventArgs e) { if (_model != null) await _model.Git.PushAsync(); }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_model == null) return;
        _model.ReloadRecords();
        await _model.RefreshStatusesAsync();
        await _model.LoadSelectedAsync();
    }

    private void OnHelp(object sender, RoutedEventArgs e) => HelpPopup.IsOpen = !HelpPopup.IsOpen;

    private void OnOpenAgents(object sender, RoutedEventArgs e) => OpenInEditor("AGENTS.md");

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if (_model?.SelectedPath is { } path) RepoShell.Reveal(path);
    }

    /// <summary>Opens an agent session in the checkout, in the app's terminal panel.</summary>
    private void OnOpenTerminal(object sender, RoutedEventArgs e)
    {
        if (_model?.Selected is not { Local: { } local } record) return;
        if (Window.GetWindow(this) is not MainWindow window) return;
        // The terminal panel's own default: the configured agent, or the shell
        // when the built-in default agent is not installed.
        var settings = ((App)Application.Current).Config.Terminal;
        var agent = settings.AgentCommandIsBuiltInDefault && AgentCommands.FindInstalled(settings.AgentCommand) == null
            ? AgentCommands.Shell
            : settings.AgentCommand;
        var command = AgentCommands.Resolve(agent, AgentCommands.FindInstalled);
        var label = string.IsNullOrWhiteSpace(agent) ? AgentCommands.Shell : agent.Split(' ')[0];
        var location = new RepoLocation(record.Key.Name, local.Path, null);
        window.SetTerminalVisible(true, takeFocus: false);
        window.Terminal.OpenSession(new TerminalLaunch($"{label} · {record.Key.Name}", command, local.Path, location));
    }

    private void OpenInEditor(string relativePath)
    {
        if (_model == null) return;
        _model.Git.Panel = RepoPanel.Files;
        _model.Open(relativePath);
    }

    // ── Composer ────────────────────────────────────────────────────────

    private void OnSubjectChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncing && _model != null) _model.Git.CommitSubject = SubjectBox.Text;
    }

    private void OnBodyChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncing && _model != null) _model.Git.CommitBody = BodyBox.Text;
    }

    private void OnAmendClicked(object sender, RoutedEventArgs e)
    {
        if (_model != null) _model.Git.Amend = AmendBox.IsChecked == true;
    }

    private void OnSkipHooksClicked(object sender, RoutedEventArgs e)
    {
        if (_model != null) _model.Git.SkipHooks = SkipHooksBox.IsChecked == true;
    }

    private async void OnStageAll(object sender, RoutedEventArgs e)
    {
        if (_model == null) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) await _model.Git.UnstageAllAsync();
        else await _model.Git.ToggleStageAllAsync();
    }

    private async void OnCommit(object sender, RoutedEventArgs e) { if (_model != null) await _model.Git.CommitAsync(); }
    private async void OnCommitAndPush(object sender, RoutedEventArgs e) { if (_model != null) await _model.Git.CommitAndPushAsync(); }

    private void OnCopyOutput(object sender, RoutedEventArgs e) => RepoShell.Copy(OutputText.Text);

    // ── Changes list ────────────────────────────────────────────────────

    private void OnChangesSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _model == null) return;
        _model.Git.FileSelection = ChangesList.SelectedItems.OfType<ChangeRow>().Select(r => r.Id).ToList();
    }

    private static ChangeRow? ChangeOf(object sender) => (sender as FrameworkElement)?.DataContext as ChangeRow;

    private async void OnStageCheckbox(object sender, RoutedEventArgs e)
    {
        if (_model == null || ChangeOf(sender) is not { } row) return;
        if (row.Staged) await _model.Git.UnstageAsync(new[] { row.Entry.RelativePath });
        else await _model.Git.StageAsync(new[] { row.Entry.RelativePath });
    }

    private void OnChangeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChangesList.SelectedItem is ChangeRow row && row.Entry.Kind != GitEntryKind.Deleted) OpenInEditor(row.Entry.RelativePath);
    }

    private void OnChangeOpen(object sender, RoutedEventArgs e)
    {
        if (ChangeOf(sender) is { } row) OpenInEditor(row.Entry.RelativePath);
    }

    private void OnChangeOpenExternal(object sender, RoutedEventArgs e)
    {
        if (ChangeOf(sender) is not { } row || !File.Exists(row.AbsolutePath)) return;
        try { Process.Start(new ProcessStartInfo(row.AbsolutePath) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception ex) { ShowError("Could not open the file", ex.Message); }
    }

    private void OnChangeReveal(object sender, RoutedEventArgs e)
    {
        if (ChangeOf(sender) is { } row) RepoShell.Reveal(File.Exists(row.AbsolutePath) ? row.AbsolutePath : row.Root);
    }

    private void OnChangeCopyPath(object sender, RoutedEventArgs e)
    {
        if (ChangeOf(sender) is { } row) RepoShell.Copy(row.AbsolutePath);
    }

    private void OnChangeCopyRelative(object sender, RoutedEventArgs e)
    {
        if (ChangeOf(sender) is { } row) RepoShell.Copy(row.Entry.RelativePath);
    }

    private async void OnChangeToggleStage(object sender, RoutedEventArgs e)
    {
        if (_model == null || ChangeOf(sender) is not { } row) return;
        if (!_model.Git.FileSelection.Contains(row.Id)) _model.Git.FileSelection = new[] { row.Id };
        await _model.Git.ToggleStageSelectedAsync();
    }

    private async void OnChangeDiscard(object sender, RoutedEventArgs e)
    {
        if (_model == null || ChangeOf(sender) is not { } row) return;
        if (!_model.Git.FileSelection.Contains(row.Id)) _model.Git.FileSelection = new[] { row.Id };
        await _model.Git.DiscardSelectedAsync();
    }

    // ── History ─────────────────────────────────────────────────────────

    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _model == null) return;
        _model.Git.CommitSelection = (HistoryList.SelectedItem as HistoryRow)?.Sha;
    }

    private static GitCommit? CommitOf(object sender) => ((sender as FrameworkElement)?.DataContext as HistoryRow)?.Commit;

    private async void OnCommitTag(object sender, RoutedEventArgs e)
    {
        if (_model == null || CommitOf(sender) is not { } commit) return;
        var owner = Window.GetWindow(this);
        var name = TextPromptDialog.Ask(owner, "Add Tag", $"Tag {commit.ShortSha} “{commit.Subject}” as:", "v");
        if (name == null) return;
        var message = TextPromptDialog.Ask(owner, "Tag Message", "Message for an annotated tag (Cancel for a lightweight tag):", "");
        await _model.Git.TagAsync(commit, name, message);
    }

    private async void OnCommitBranch(object sender, RoutedEventArgs e)
    {
        if (_model == null || CommitOf(sender) is not { } commit) return;
        var name = TextPromptDialog.Ask(Window.GetWindow(this), "Create Branch",
            $"Create a branch at {commit.ShortSha} (without switching to it):", "feature/");
        if (name != null) await _model.Git.CreateBranchAsync(commit, name);
    }

    private async void OnCommitCheckout(object sender, RoutedEventArgs e)
    {
        if (_model != null && CommitOf(sender) is { } commit) await _model.Git.CheckoutAsync(commit);
    }

    private async void OnCommitCherryPick(object sender, RoutedEventArgs e)
    {
        if (_model != null && CommitOf(sender) is { } commit) await _model.Git.CherryPickAsync(commit);
    }

    private async void OnCommitRevert(object sender, RoutedEventArgs e)
    {
        if (_model != null && CommitOf(sender) is { } commit) await _model.Git.RevertAsync(commit);
    }

    private void OnCommitCopyHash(object sender, RoutedEventArgs e)
    {
        if (CommitOf(sender) is { } commit) RepoShell.Copy(commit.Sha);
    }

    private void OnCommitCopySubject(object sender, RoutedEventArgs e)
    {
        if (CommitOf(sender) is { } commit) RepoShell.Copy(commit.Subject);
    }

    private async void OnCommitCopyPatch(object sender, RoutedEventArgs e)
    {
        if (_model != null && CommitOf(sender) is { } commit && await _model.Git.PatchAsync(commit) is { Length: > 0 } patch)
            RepoShell.Copy(patch);
    }

    // ── Files ───────────────────────────────────────────────────────────

    private void OnFileClicked(object sender, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(FilesList, (DependencyObject)e.OriginalSource) as ListViewItem;
        if (item?.DataContext is FileRow row) Activate(row);
    }

    private void OnFileKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && FilesList.SelectedItem is FileRow row)
        {
            Activate(row);
            e.Handled = true;
        }
    }

    private void Activate(FileRow row)
    {
        if (_model == null || _syncing) return;
        if (row.IsFolder)
        {
            if (_model.FileFilter.Trim().Length == 0) _model.ToggleFolder(row.Path);
        }
        else
        {
            _model.Open(row.Path);
        }
    }

    private void OnGrepSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_model != null && GrepList.SelectedItem is GrepRow row) _model.Open(row.Match.Path, row.Match.Line);
    }

    private void OnShowFiles(object sender, RoutedEventArgs e) => _model?.ClearGrep();

    // ── Editor ──────────────────────────────────────────────────────────

    private void OnSave(object sender, RoutedEventArgs e) => _model?.Save();

    private void OnCloseEditor(object sender, RoutedEventArgs e) => _model?.CloseEditor();

    private void OnRevertEdits(object sender, RoutedEventArgs e)
    {
        if (_model?.Document is not { } document || !_model.IsDirty) return;
        if (Confirm("Discard your unsaved edits?", $"{document.Path} goes back to what is saved on disk.", "Discard Edits")) _model.Revert();
    }

    // ── Filter (/) ──────────────────────────────────────────────────────

    private void ShowFilter()
    {
        FilterBar.Visibility = Visibility.Visible;
        PanelFilterBox.Focus();
        PanelFilterBox.SelectAll();
    }

    private void OnPanelFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (_model != null) _model.Git.Filter = PanelFilterBox.Text.Trim();
    }

    private void OnPanelFilterKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        OnClearFilter(sender, e);
        e.Handled = true;
    }

    private void OnClearFilter(object sender, RoutedEventArgs e)
    {
        PanelFilterBox.Text = "";
        FilterBar.Visibility = Visibility.Collapsed;
        Focus();
    }

    // ── Keys ────────────────────────────────────────────────────────────

    /// <summary>
    /// Lazygit-style keys while the pane has focus outside a text field:
    /// 1–4 panels, j/k move, Space stages, a stages all, c commit message,
    /// C commit and push, P push, p pull, f fetch, r refresh, o open, d discard
    /// (asks first), ? help, / filter, Tab next panel. Ctrl+Enter commits.
    /// </summary>
    private async void OnKey(object sender, KeyEventArgs e)
    {
        if (_model?.Selected == null) return;
        var git = _model.Git;
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && git.Panel == RepoPanel.Changes)
        {
            e.Handled = true;
            await git.CommitAsync();
            return;
        }
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or PasswordBox) return;
        // Space and Enter on a focused button press the button.
        if (Keyboard.FocusedElement is ButtonBase && e.Key is Key.Space or Key.Enter) return;
        if (Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return;
        var shift = Keyboard.Modifiers == ModifierKeys.Shift;
        var handled = true;
        switch (e.Key)
        {
            case Key.D1 when !shift: git.Panel = RepoPanel.Changes; break;
            case Key.D2 when !shift: git.Panel = RepoPanel.History; break;
            case Key.D3 when !shift: git.Panel = RepoPanel.Files; break;
            case Key.D4 when !shift: git.Panel = RepoPanel.Insights; break;
            case Key.J or Key.Down when !shift && git.Panel is RepoPanel.Changes or RepoPanel.History: git.MoveSelection(1); break;
            case Key.K or Key.Up when !shift && git.Panel is RepoPanel.Changes or RepoPanel.History: git.MoveSelection(-1); break;
            case Key.Space when git.Panel == RepoPanel.Changes: await git.ToggleStageSelectedAsync(); break;
            case Key.A when !shift && git.Panel == RepoPanel.Changes: await git.ToggleStageAllAsync(); break;
            case Key.C when shift: await git.CommitAndPushAsync(); break;
            case Key.C:
                git.Panel = RepoPanel.Changes;
                SubjectBox.Focus();
                break;
            case Key.P when shift: await git.PushAsync(); break;
            case Key.P: await git.PullAsync(); break;
            case Key.F when !shift: await git.FetchAsync(); break;
            case Key.R when !shift: await git.RefreshAsync(); break;
            case Key.O when !shift && git.PrimaryEntry is { } entry && git.Panel == RepoPanel.Changes: OpenInEditor(entry.RelativePath); break;
            case Key.D when !shift && git.Panel == RepoPanel.Changes: await git.DiscardSelectedAsync(); break;
            case Key.OemQuestion when shift: HelpPopup.IsOpen = !HelpPopup.IsOpen; break;
            case Key.OemQuestion when git.Panel is RepoPanel.Changes or RepoPanel.History: ShowFilter(); break;
            case Key.Tab: git.CyclePanel(shift ? -1 : 1); break;
            case Key.Escape when HelpPopup.IsOpen: HelpPopup.IsOpen = false; break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }

    private void BuildHelp()
    {
        void Column(Grid grid, int column, string title, (string Key, string What)[] entries)
        {
            var panel = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 20, 0, 0, 0) };
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
            foreach (var (key, what) in entries)
            {
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var keyText = new TextBlock { Text = key, FontFamily = new FontFamily("Consolas"), MinWidth = 86 };
                DockPanel.SetDock(keyText, Dock.Left);
                row.Children.Add(keyText);
                row.Children.Add(new TextBlock { Text = what, Opacity = 0.8 });
                panel.Children.Add(row);
            }
            Grid.SetColumn(panel, column);
            grid.Children.Add(panel);
        }
        var grid = new Grid();
        for (var i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Column(grid, 0, "Navigation", new[]
        {
            ("1 / 2 / 3 / 4", "Changes / History / Files / Insights"), ("Tab", "Next panel"), ("Shift+Tab", "Previous panel"),
            ("j / ↓", "Down"), ("k / ↑", "Up"), ("/", "Filter"), ("Esc", "Close filter or help"),
        });
        Column(grid, 1, "Changes", new[]
        {
            ("Space", "Stage or unstage"), ("a", "Stage or unstage all"), ("d", "Discard (asks first)"), ("o", "Open in editor"),
            ("c", "Write the commit message"), ("Ctrl+Enter", "Commit"), ("C", "Commit and push"),
        });
        Column(grid, 2, "Remote", new[]
        {
            ("f", "Fetch"), ("p", "Pull (fast-forward only)"), ("P", "Push"), ("r", "Refresh"), ("?", "This help"),
        });
        HelpContent.Children.Add(grid);
        HelpContent.Children.Add(new TextBlock
        {
            Text = "Commits and pushes on main, master and the repository's default branch are refused. Keys work while the pane has focus outside a text field.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = 11,
            Margin = new Thickness(0, 10, 0, 0),
        });
    }
}
