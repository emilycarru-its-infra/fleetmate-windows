using System.ComponentModel;
using FleetMate.Core.Services.Repos;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace FleetMate.WinUI.Pages.Development.Repos;

/// <summary>
/// The Repos workspace's detail for the selected repository: the git pane
/// ported from FleetMate for Mac (header with branch menu, Fetch, Pull, Push;
/// commit composer above the change list; structured diff with per-chunk
/// Stage, Unstage and Discard; History with the lane graph and commit
/// actions), plus the Files panel's tree, content search and editor.
/// Repository Insights live in the Graphs strip above. Lazygit-style keys
/// work while the pane has focus outside a text field. Destructive steps —
/// discarding files or a chunk, checking out, deleting a stale index lock —
/// ask first, with Cancel the default.
/// </summary>
public sealed partial class RepoWorkspaceView : UserControl
{
    private RepoWorkspaceModel? _model;
    private bool _syncing;
    private readonly DispatcherQueueTimer _errorTimer;
    private string? _changesKey, _historyKey, _filesKey;

    public RepoWorkspaceView()
    {
        InitializeComponent();
        _errorTimer = DispatcherQueue.CreateTimer();
        _errorTimer.Interval = TimeSpan.FromSeconds(12);
        _errorTimer.IsRepeating = false;
        _errorTimer.Tick += (_, _) => ErrorBar.IsOpen = false;
        Editor.TextEdited += () => _model?.EditorTextChanged();
        Editor.SaveRequested += () => _model?.Save();
        DiffView.StageChunk = (file, hunk) => _model?.Git.ApplyChunkAsync(file, hunk, reverse: false) ?? Task.CompletedTask;
        DiffView.UnstageChunk = (file, hunk) => _model?.Git.ApplyChunkAsync(file, hunk, reverse: true) ?? Task.CompletedTask;
        DiffView.DiscardChunk = (file, hunk) => _model?.Git.DiscardChunkAsync(file, hunk) ?? Task.CompletedTask;
        RowMenus.Attach(ChangesList, (item, _) => item is ChangeRow row ? ChangeMenu(row) : null);
        RowMenus.Attach(HistoryList, (item, anchor) => item is HistoryRow row ? CommitMenu(row.Commit, anchor) : null);
        RowMenus.Attach(FilesList, (item, _) => item is FileRow row ? FileMenu(row) : null);
        RowMenus.Attach(GrepList, (item, _) => item is GrepRow row ? FileMenu(row.Path, row.Match.Line) : null);
        BuildHelp();
        Workspace.SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width < StackedBelow);
    }

    /// <summary>Below this width the list sits above the diff or editor instead of beside it.</summary>
    private const double StackedBelow = 820;

    private bool? _stacked;

    /// <summary>Side by side when there is room; otherwise the list above and the diff or editor below, each full width.</summary>
    private void ApplyLayout(bool stacked)
    {
        if (_stacked == stacked) return;
        _stacked = stacked;
        PanelGrid.ColumnDefinitions.Clear();
        PanelGrid.RowDefinitions.Clear();
        if (stacked)
        {
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            // Fixed heights that each show real content; the body scrolls when the window is shorter.
            PanelGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(560), MinHeight = 280 });
            PanelGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            PanelGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(480), MinHeight = 200 });
        }
        else
        {
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.42, GridUnitType.Star), MinWidth = 300 });
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.58, GridUnitType.Star), MinWidth = 300 });
        }
        FrameworkElement[] panes = { ListPane, PanelSplitter, DetailPane };
        for (var i = 0; i < panes.Length; i++)
        {
            Grid.SetRow(panes[i], stacked ? i : 0);
            Grid.SetColumn(panes[i], stacked ? 0 : i);
        }
        PanelSplitter.ResizeDirection = stacked
            ? CommunityToolkit.WinUI.Controls.GridSplitter.GridResizeDirection.Rows
            : CommunityToolkit.WinUI.Controls.GridSplitter.GridResizeDirection.Columns;
        PanelScroller.VerticalScrollMode = stacked ? ScrollMode.Enabled : ScrollMode.Disabled;
        PanelScroller.VerticalScrollBarVisibility = stacked ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        PanelSplitter.Width = stacked ? double.NaN : 12;
        PanelSplitter.Height = stacked ? 12 : double.NaN;
    }

    public void Attach(RepoWorkspaceModel model)
    {
        _model = model;
        model.Git.Confirm = (title, message, action) => RepoDialogs.ConfirmAsync(XamlRoot, title, message, action);
        model.ErrorReported += ShowError;
        model.PropertyChanged += OnModelChanged;
        model.Git.PropertyChanged += OnGitChanged;
        RenderAll();
    }

    /// <summary>The toolbar search's Enter: search every file's contents.</summary>
    public async void SubmitSearch(string text)
    {
        if (_model == null || text.Trim().Length == 0) return;
        _model.Git.Panel = RepoPanel.Files;
        await _model.GrepAsync(text);
    }

    /// <summary>Show <paramref name="relativePath"/> in the Files panel — Insights' most-changed files open here.</summary>
    public void OpenFile(string relativePath)
    {
        if (_model == null) return;
        _model.Git.Panel = RepoPanel.Files;
        _model.Open(relativePath);
    }

    // MARK: - Model to view

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
                RenderComposer();
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
        NoSelection.Visibility = DevelopmentUi.Show(!hasSelection);
        Workspace.Visibility = DevelopmentUi.Show(hasSelection);
        _changesKey = _historyKey = _filesKey = null;
        RenderHeader();
        RenderPanel();
        RenderComposer();
        RenderChanges();
        RenderHistory();
        RenderEditor(reload: true);
    }

    private void RenderHeader()
    {
        if (_model == null) return;
        var git = _model.Git;
        BranchLabel.Text = git.CurrentBranch ?? "(detached)";
        DirtyDot.Visibility = DevelopmentUi.Show(git.Files.Count > 0);
        SyncLabel.Text = RepoWorkspaceText.SyncLabel(git);
        SyncLabel.Visibility = DevelopmentUi.Show(SyncLabel.Text.Length > 0);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(BranchButton,
            $"Branch {BranchLabel.Text}{(git.Files.Count > 0 ? ", uncommitted changes" : "")}{(SyncLabel.Text.Length > 0 ? ", " + SyncLabel.Text : "")}");
        ToolTipService.SetToolTip(PushButton, RepoWorkspaceText.PushTooltip(git));
        var hasAgents = RepoWorkspaceText.HasAgentsFile(_model);
        AgentsButton.IsEnabled = hasAgents;
        AgentsButton.Label = hasAgents ? "AGENTS.md" : "No AGENTS.md";
        ToolTipService.SetToolTip(AgentsButton, hasAgents
            ? "Agents read this file before working here. Open it in Files."
            : "This repository has no AGENTS.md for agents to read");
        ToolTipService.SetToolTip(RevealButton, _model.SelectedPath is { } path ? $"Reveal {RepoShell.Abbreviate(path)} in Explorer" : null);
    }

    private void RenderPanel()
    {
        if (_model == null) return;
        var panel = _model.Git.Panel;
        var lists = panel is RepoPanel.Changes or RepoPanel.History;
        Composer.Visibility = DevelopmentUi.Show(panel == RepoPanel.Changes);
        FilesHeader.Visibility = DevelopmentUi.Show(panel == RepoPanel.Files);
        ChangesList.Visibility = DevelopmentUi.Show(panel == RepoPanel.Changes);
        HistoryList.Visibility = DevelopmentUi.Show(panel == RepoPanel.History);
        if (!lists) FilterBar.Visibility = Visibility.Collapsed;
        else if (_model.Git.Filter.Length > 0) FilterBar.Visibility = Visibility.Visible;
        DiffView.Visibility = DevelopmentUi.Show(lists);
        EditorPane.Visibility = DevelopmentUi.Show(panel == RepoPanel.Files);
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
        // The box hands back carriage returns; the model keeps newlines.
        if (BodyBox.Text.Replace("\r", "\n", StringComparison.Ordinal) != git.CommitBody) BodyBox.Text = git.CommitBody;
        AmendBox.IsChecked = git.Amend;
        SkipHooksBox.IsChecked = git.SkipHooks;
        _syncing = false;
        ProtectedText.Text = $"{git.CurrentBranch} is protected";
        ProtectedLabel.Visibility = DevelopmentUi.Show(git.IsOnProtectedBranch);
        var canCommit = git.CommitSubject.Trim().Length > 0 && !git.IsBusy;
        CommitButton.IsEnabled = canCommit;
        CommitPushButton.IsEnabled = canCommit;
        StageAllButton.IsEnabled = git.Files.Count > 0;
        BusyRing.IsActive = git.IsBusy;
        StatusText.Text = RepoWorkspaceText.ComposerStatus(git);
        StatusText.Foreground = git.StatusMessage == null ? RepoBrushes.Secondary : git.StatusKind switch
        {
            GitStatusKind.Success => RepoBrushes.Success,
            GitStatusKind.Error => RepoBrushes.Warning,
            _ => RepoBrushes.Secondary,
        };
        ToolTipService.SetToolTip(StatusText, StatusText.Text.Length > 0 ? StatusText.Text : null);
        OutputText.Text = git.ProcessOutput;
        OutputPanel.Visibility = DevelopmentUi.Show(git.ProcessOutput.Length > 0);
    }

    private void RenderChanges()
    {
        if (_model == null) return;
        var root = _model.SelectedPath ?? "";
        var git = _model.Git;
        var entries = git.FilteredFiles;
        var key = root + "\n" + string.Join("\n", entries.Select(e => e.Id + (e.Staged ? "+" : "") + e.Kind));
        _syncing = true;
        try
        {
            if (key != _changesKey)
            {
                _changesKey = key;
                ChangesList.ItemsSource = entries.Select(e => new ChangeRow(e, root)).ToList();
            }
            var rows = (ChangesList.ItemsSource as List<ChangeRow>) ?? new List<ChangeRow>();
            var wanted = rows.Where(r => git.FileSelection.Contains(r.Id)).ToList();
            var current = ChangesList.SelectedItems.OfType<ChangeRow>().ToList();
            if (!wanted.Select(r => r.Id).ToHashSet().SetEquals(current.Select(r => r.Id)))
            {
                ChangesList.SelectedItems.Clear();
                foreach (var row in wanted) ChangesList.SelectedItems.Add(row);
                if (wanted.FirstOrDefault() is { } first && ChangesList.Visibility == Visibility.Visible) ChangesList.ScrollIntoView(first);
            }
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
        var filtered = git.Filter.Length > 0;
        var key = string.Join("\n", git.FilteredCommits.Select(c => c.Sha + string.Join(",", c.Refs.Select(r => r.Name)))) + (filtered ? "f" : "");
        _syncing = true;
        try
        {
            if (key != _historyKey)
            {
                _historyKey = key;
                var graph = git.Commits.Select((c, i) => (c.Sha, Row: i < git.Graph.Count ? git.Graph[i] : null))
                    .GroupBy(x => x.Sha).ToDictionary(g => g.Key, g => g.First().Row);
                HistoryList.ItemsSource = git.FilteredCommits
                    .Select(c => new HistoryRow(c, filtered ? null : graph.GetValueOrDefault(c.Sha), git.LaneCount)).ToList();
            }
            var rows = (HistoryList.ItemsSource as List<HistoryRow>) ?? new List<HistoryRow>();
            var selected = rows.FirstOrDefault(r => r.Sha == git.CommitSelection);
            if (!ReferenceEquals(HistoryList.SelectedItem, selected))
            {
                HistoryList.SelectedItem = selected;
                if (selected != null && HistoryList.Visibility == Visibility.Visible) HistoryList.ScrollIntoView(selected);
            }
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
        FilesList.Visibility = DevelopmentUi.Show(showFiles && !searching);
        GrepList.Visibility = DevelopmentUi.Show(showFiles && searching);
        ShowFilesButton.Visibility = DevelopmentUi.Show(searching);
        SearchRing.IsActive = _model.IsSearching;
        FilesTitle.Text = RepoWorkspaceText.FilesTitle(_model);
        FilesHint.Text = RepoWorkspaceText.FilesHint(_model);
        if (!showFiles) return;

        if (searching)
        {
            GrepSource.Source = _model.GrepResults.Select(m => new GrepRow(m)).GroupBy(r => r.Path)
                .Select(g => new GrepGroup(g.Key, g)).ToList();
            GrepList.ItemsSource = GrepSource.View;
            _filesKey = null;
        }
        else
        {
            var filter = _model.FileFilter.Trim();
            var nodes = RepoFileTree.Filter(_model.Tree, filter);
            var expanded = filter.Length > 0 ? RepoFileTree.FolderPaths(nodes) : _model.ExpandedFolders;
            var rows = RepoFileTree.VisibleRows(nodes, expanded)
                .Select(r => new FileRow(r, !r.Node.IsFolder && _model.HasUnsavedEdits(r.Node.Path))).ToList();
            var key = string.Join("\n", rows.Select(r => r.Path + (r.Row.IsExpanded ? "+" : "") + (r.IsUnsaved ? "*" : "")));
            _syncing = true;
            if (key != _filesKey)
            {
                _filesKey = key;
                FilesList.ItemsSource = rows;
            }
            var current = (FilesList.ItemsSource as List<FileRow>)?.FirstOrDefault(r => r.Path == _model.Document?.Path);
            if (!ReferenceEquals(FilesList.SelectedItem, current)) FilesList.SelectedItem = current;
            _syncing = false;
        }
        RenderListEmpty();
    }

    private void RenderListEmpty()
    {
        if (_model == null) return;
        var text = RepoWorkspaceText.ListEmpty(_model, (FilesList.ItemsSource as List<FileRow>)?.Count ?? 0);
        ListEmpty.Text = text ?? "";
        ListEmpty.Visibility = DevelopmentUi.Show(text != null);
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
        EditorEmpty.Visibility = DevelopmentUi.Show(document == null);
        Editor.Visibility = DevelopmentUi.Show(document != null);
        EditorTitle.Text = RepoWorkspaceText.EditorTitle(_model);
        ToolTipService.SetToolTip(EditorTitle, document?.Path);
        RevertButton.IsEnabled = _model.IsDirty;
        CloseButton.Visibility = DevelopmentUi.Show(document != null);
        RevertButton.Visibility = CloseButton.Visibility;
        SaveButton.IsEnabled = _model.IsDirty && document is { IsReadOnly: false };
        NoticeText.Text = _model.Notice ?? "";
    }

    // MARK: - Errors

    private void ShowError(string title, string message)
    {
        ErrorBar.Title = title;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
        _errorTimer.Stop();
        _errorTimer.Start();
    }

    // MARK: - Header actions

    private void BranchMenu_Opening(object sender, object e)
    {
        BranchMenu.Items.Clear();
        if (_model == null) return;
        var git = _model.Git;
        foreach (var branch in git.Branches)
        {
            var item = new RadioMenuFlyoutItem { Text = RepoWorkspaceText.BranchTitle(branch), GroupName = "ReposBranch", IsChecked = branch.IsCurrent };
            var name = branch.Name;
            item.Click += async (_, _) => { if (!branch.IsCurrent) await git.SwitchBranchAsync(name); };
            BranchMenu.Items.Add(item);
        }
        if (BranchMenu.Items.Count > 0) BranchMenu.Items.Add(new MenuFlyoutSeparator());
        BranchMenu.Items.Add(RowMenus.Item("New Branch…", () => NamePopover.Show(BranchButton, "New Branch",
            $"Create a branch at HEAD ({git.CurrentBranch ?? "detached"}) and switch to it.",
            new[] { new PopoverField("Branch name", "feature/", AutomationId: "ReposNewBranchName") }, "Create",
            values => git.NewBranchAsync(values[0])), glyph: "\uE710"));
    }

    private async void FetchButton_Click(object sender, RoutedEventArgs e) { if (_model != null) await _model.Git.FetchAsync(); }
    private async void PullButton_Click(object sender, RoutedEventArgs e) { if (_model != null) await _model.Git.PullAsync(); }
    private async void PushButton_Click(object sender, RoutedEventArgs e) { if (_model != null) await _model.Git.PushAsync(); }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_model == null) return;
        _model.ReloadRecords();
        await _model.RefreshStatusesAsync();
        await _model.LoadSelectedAsync();
    }

    private void AgentsButton_Click(object sender, RoutedEventArgs e) => OpenFile("AGENTS.md");

    private void RevealButton_Click(object sender, RoutedEventArgs e)
    {
        if (_model?.SelectedPath is { } path) RepoShell.Reveal(path);
    }

    // MARK: - Composer

    private void SubjectBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncing && _model != null) _model.Git.CommitSubject = SubjectBox.Text;
    }

    private void BodyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncing && _model != null) _model.Git.CommitBody = BodyBox.Text.Replace("\r", "\n", StringComparison.Ordinal);
    }

    private void AmendBox_Click(object sender, RoutedEventArgs e)
    {
        if (_model != null) _model.Git.Amend = AmendBox.IsChecked == true;
    }

    private void SkipHooksBox_Click(object sender, RoutedEventArgs e)
    {
        if (_model != null) _model.Git.SkipHooks = SkipHooksBox.IsChecked == true;
    }

    private async void StageAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_model == null) return;
        if (Keys.Shift) await _model.Git.UnstageAllAsync();
        else await _model.Git.ToggleStageAllAsync();
    }

    private async void CommitButton_Click(object sender, RoutedEventArgs e) { if (_model != null) await _model.Git.CommitAsync(); }
    private async void CommitPushButton_Click(object sender, RoutedEventArgs e) { if (_model != null) await _model.Git.CommitAndPushAsync(); }

    private void CopyOutputButton_Click(object sender, RoutedEventArgs e) => DevelopmentUi.Copy(OutputText.Text);

    // MARK: - Changes

    private void ChangesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _model == null) return;
        _model.Git.FileSelection = ChangesList.SelectedItems.OfType<ChangeRow>().Select(r => r.Id).ToList();
    }

    private async void StageCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_model == null || (sender as FrameworkElement)?.Tag is not ChangeRow row) return;
        if (row.Staged) await _model.Git.UnstageAsync(new[] { row.Entry.RelativePath });
        else await _model.Git.StageAsync(new[] { row.Entry.RelativePath });
    }

    private void ChangesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ChangeRow row && row.Entry.Kind != GitEntryKind.Deleted)
            OpenFile(row.Entry.RelativePath);
    }

    private MenuFlyout ChangeMenu(ChangeRow row)
    {
        var git = _model!.Git;
        var menu = new MenuFlyout();
        var exists = File.Exists(row.AbsolutePath);
        menu.Items.Add(RowMenus.Item("Open in Editor", () => OpenFile(row.Entry.RelativePath), row.Entry.Kind != GitEntryKind.Deleted, "\uE70F"));
        menu.Items.Add(RowMenus.Item("Open with Default App", () => OpenExternal(row), exists));
        menu.Items.Add(RowMenus.Item("Reveal in Explorer", () => RepoShell.Reveal(exists ? row.AbsolutePath : row.Root), glyph: "\uE838"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(RowMenus.Item("Copy Path", () => DevelopmentUi.Copy(row.AbsolutePath), glyph: "\uE8C8"));
        menu.Items.Add(RowMenus.Item("Copy Relative Path", () => DevelopmentUi.Copy(row.Entry.RelativePath)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(RowMenus.Item(row.StageLabel, async () =>
        {
            if (!git.FileSelection.Contains(row.Id)) git.FileSelection = new[] { row.Id };
            await git.ToggleStageSelectedAsync();
        }));
        menu.Items.Add(RowMenus.Item("Discard Changes…", async () =>
        {
            if (!git.FileSelection.Contains(row.Id)) git.FileSelection = new[] { row.Id };
            await git.DiscardSelectedAsync();
        }, row.CanDiscard, "\uE7A7"));
        return menu;
    }

    private void OpenExternal(ChangeRow row)
    {
        try { RepoShell.OpenWithDefaultApp(row.AbsolutePath); }
        catch (System.ComponentModel.Win32Exception ex) { ShowError("Could not open the file", ex.Message); }
    }

    // MARK: - History

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _model == null) return;
        _model.Git.CommitSelection = (HistoryList.SelectedItem as HistoryRow)?.Sha;
    }

    private MenuFlyout CommitMenu(GitCommit commit, FrameworkElement anchor)
    {
        var git = _model!.Git;
        var menu = new MenuFlyout();
        menu.Items.Add(RowMenus.Item("Add Tag…", () => NamePopover.Show(anchor, "Add Tag", $"Tag {commit.ShortSha} “{commit.Subject}”.",
            new[]
            {
                new PopoverField("Tag name", "v", AutomationId: "ReposTagName"),
                new PopoverField("Message (optional; makes an annotated tag)", Required: false, AutomationId: "ReposTagMessage"),
            }, "Add Tag", values => git.TagAsync(commit, values[0], values[1].Length > 0 ? values[1] : null)), glyph: "\uE8EC"));
        menu.Items.Add(RowMenus.Item("Create Branch…", () => NamePopover.Show(anchor, "Create Branch",
            $"Create a branch at {commit.ShortSha}, without switching to it.",
            new[] { new PopoverField("Branch name", "feature/", AutomationId: "ReposBranchName") }, "Create",
            values => git.CreateBranchAsync(commit, values[0])), glyph: "\uE8D4"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(RowMenus.Item("Checkout…", () => git.CheckoutAsync(commit)));
        menu.Items.Add(RowMenus.Item("Cherry-Pick…", () => git.CherryPickAsync(commit)));
        menu.Items.Add(RowMenus.Item("Revert…", () => git.RevertAsync(commit), glyph: "\uE7A7"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(RowMenus.Item("Copy Hash", () => DevelopmentUi.Copy(commit.Sha), glyph: "\uE8C8"));
        menu.Items.Add(RowMenus.Item("Copy Subject", () => DevelopmentUi.Copy(commit.Subject)));
        menu.Items.Add(RowMenus.Item("Copy as Patch", async () =>
        {
            if (await git.PatchAsync(commit) is { Length: > 0 } patch) DevelopmentUi.Copy(patch);
        }));
        return menu;
    }

    // MARK: - Files

    private void FilesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_model == null || e.ClickedItem is not FileRow row) return;
        if (row.IsFolder)
        {
            if (_model.FileFilter.Trim().Length == 0) _model.ToggleFolder(row.Path);
        }
        else
        {
            _model.Open(row.Path);
        }
    }

    private MenuFlyout? FileMenu(FileRow row) => row.IsFolder ? FolderMenu(row) : FileMenu(row.Path, null);

    private MenuFlyout? FolderMenu(FileRow row)
    {
        if (_model?.SelectedPath is not { } root) return null;
        var absolute = Path.Combine(root, row.Path.Replace('/', '\\'));
        var menu = new MenuFlyout();
        menu.Items.Add(RowMenus.Item(row.Row.IsExpanded ? "Collapse" : "Expand", () => _model.ToggleFolder(row.Path), _model.FileFilter.Trim().Length == 0));
        menu.Items.Add(RowMenus.Item("Reveal in Explorer", () => RepoShell.Reveal(absolute), glyph: "\uE838"));
        menu.Items.Add(RowMenus.Item("Copy Relative Path", () => DevelopmentUi.Copy(row.Path), glyph: "\uE8C8"));
        return menu;
    }

    private MenuFlyout? FileMenu(string path, int? line)
    {
        if (_model?.SelectedPath is not { } root) return null;
        var absolute = Path.Combine(root, path.Replace('/', '\\'));
        var menu = new MenuFlyout();
        menu.Items.Add(RowMenus.Item("Open in Editor", () => _model.Open(path, line), glyph: "\uE70F"));
        menu.Items.Add(RowMenus.Item("Reveal in Explorer", () => RepoShell.Reveal(absolute), glyph: "\uE838"));
        menu.Items.Add(RowMenus.Item("Copy Path", () => DevelopmentUi.Copy(absolute), glyph: "\uE8C8"));
        menu.Items.Add(RowMenus.Item("Copy Relative Path", () => DevelopmentUi.Copy(path)));
        return menu;
    }

    private void GrepList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_model != null && GrepList.SelectedItem is GrepRow row) _model.Open(row.Match.Path, row.Match.Line);
    }

    private void ShowFilesButton_Click(object sender, RoutedEventArgs e) => _model?.ClearGrep();

    // MARK: - Editor

    private void SaveButton_Click(object sender, RoutedEventArgs e) => _model?.Save();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => _model?.CloseEditor();

    private async void RevertButton_Click(object sender, RoutedEventArgs e)
    {
        if (_model?.Document is not { } document || !_model.IsDirty) return;
        if (await RepoDialogs.ConfirmAsync(XamlRoot, "Discard your unsaved edits?", $"{document.Path} goes back to what is saved on disk.", "Discard Edits"))
            _model.Revert();
    }

    // MARK: - Filter (/)

    private void ShowFilter()
    {
        FilterBar.Visibility = Visibility.Visible;
        PanelFilterBox.Focus(FocusState.Keyboard);
        PanelFilterBox.SelectAll();
    }

    private void PanelFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_model != null) _model.Git.Filter = PanelFilterBox.Text.Trim();
    }

    private void PanelFilterBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        ClearFilter();
        e.Handled = true;
    }

    private void ClearFilterButton_Click(object sender, RoutedEventArgs e) => ClearFilter();

    private void ClearFilter()
    {
        PanelFilterBox.Text = "";
        FilterBar.Visibility = Visibility.Collapsed;
        (_model?.Git.Panel == RepoPanel.History ? HistoryList : ChangesList).Focus(FocusState.Keyboard);
    }

    // MARK: - Keys

    private const VirtualKey Slash = (VirtualKey)191;

    /// <summary>
    /// Lazygit-style keys while the pane has focus outside a text field:
    /// 1–3 panels, j/k move, Space stages, a stages all, c commit message,
    /// C commit and push, P push, p pull, f fetch, r refresh, o open, d discard
    /// (asks first), ? help, / filter, Ctrl+Tab next panel. Ctrl+Enter commits.
    /// Tab is left to move focus, so every control stays reachable by keyboard.
    /// </summary>
    private async void Workspace_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_model?.Selected == null) return;
        var git = _model.Git;
        var ctrl = Keys.Control;
        var shift = Keys.Shift;
        if (e.Key == VirtualKey.Enter && ctrl && !shift && git.Panel == RepoPanel.Changes)
        {
            e.Handled = true;
            await git.CommitAsync();
            return;
        }
        if (e.Key == VirtualKey.Tab && ctrl)
        {
            e.Handled = true;
            git.CyclePanel(shift ? -1 : 1);
            return;
        }
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        if (focused is TextBox or AutoSuggestBox or ComboBox or PasswordBox or RichEditBox) return;
        // Space and Enter on a focused button press the button.
        if (focused is ButtonBase && e.Key is VirtualKey.Space or VirtualKey.Enter) return;
        if (ctrl || Keys.Alt) return;
        var handled = true;
        switch (e.Key)
        {
            case VirtualKey.Number1 or VirtualKey.NumberPad1 when !shift: git.Panel = RepoPanel.Changes; break;
            case VirtualKey.Number2 or VirtualKey.NumberPad2 when !shift: git.Panel = RepoPanel.History; break;
            case VirtualKey.Number3 or VirtualKey.NumberPad3 when !shift: git.Panel = RepoPanel.Files; break;
            case VirtualKey.J when !shift && git.Panel is RepoPanel.Changes or RepoPanel.History: git.MoveSelection(1); FocusList(); break;
            case VirtualKey.K when !shift && git.Panel is RepoPanel.Changes or RepoPanel.History: git.MoveSelection(-1); FocusList(); break;
            case VirtualKey.Space when git.Panel == RepoPanel.Changes: await git.ToggleStageSelectedAsync(); break;
            case VirtualKey.A when !shift && git.Panel == RepoPanel.Changes: await git.ToggleStageAllAsync(); break;
            case VirtualKey.C when shift: await git.CommitAndPushAsync(); break;
            case VirtualKey.C:
                git.Panel = RepoPanel.Changes;
                SubjectBox.Focus(FocusState.Keyboard);
                break;
            case VirtualKey.P when shift: await git.PushAsync(); break;
            case VirtualKey.P: await git.PullAsync(); break;
            case VirtualKey.F when !shift: await git.FetchAsync(); break;
            case VirtualKey.R when !shift: await git.RefreshAsync(); break;
            case VirtualKey.O when !shift && git.PrimaryEntry is { } entry && git.Panel == RepoPanel.Changes: OpenFile(entry.RelativePath); break;
            case VirtualKey.D when !shift && git.Panel == RepoPanel.Changes: await git.DiscardSelectedAsync(); break;
            case Slash when shift: HelpFlyout.ShowAt(HelpButton); break;
            case Slash when git.Panel is RepoPanel.Changes or RepoPanel.History: ShowFilter(); break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }

    private void FocusList()
    {
        var list = _model?.Git.Panel == RepoPanel.History ? HistoryList : ChangesList;
        if (list.SelectedItem is { } item && list.ContainerFromItem(item) is Control container) container.Focus(FocusState.Keyboard);
    }

    private void BuildHelp()
    {
        var grid = new Grid { ColumnSpacing = 24 };
        void Column(int column, string title, (string Key, string What)[] entries)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 0, 0, 4) });
            foreach (var (key, what) in entries)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = key, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") });
                var description = new TextBlock { Text = what, Foreground = RepoBrushes.Secondary };
                Grid.SetColumn(description, 1);
                row.Children.Add(description);
                panel.Children.Add(row);
            }
            Grid.SetColumn(panel, column);
            grid.Children.Add(panel);
        }
        Column(0, "Navigation", new[]
        {
            ("1 / 2 / 3", "Changes / History / Files"), ("Ctrl+Tab", "Next panel"), ("Ctrl+Shift+Tab", "Previous panel"),
            ("j / ↓", "Down"), ("k / ↑", "Up"), ("/", "Filter"), ("Esc", "Close filter or help"),
        });
        Column(1, "Changes", new[]
        {
            ("Space", "Stage or unstage"), ("a", "Stage or unstage all"), ("d", "Discard (asks first)"), ("o", "Open in editor"),
            ("c", "Write the commit message"), ("Ctrl+Enter", "Commit"), ("C", "Commit and push"),
        });
        Column(2, "Remote", new[]
        {
            ("f", "Fetch"), ("p", "Pull (fast-forward only)"), ("P", "Push"), ("r", "Refresh"), ("?", "This help"),
        });
        HelpContent.Children.Add(grid);
        HelpContent.Children.Add(new TextBlock
        {
            Text = "Commits and pushes on main, master and the repository's default branch are refused. Keys work while the pane has focus outside a text field; Tab moves focus as everywhere else.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = RepoBrushes.Secondary,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        });
    }
}
