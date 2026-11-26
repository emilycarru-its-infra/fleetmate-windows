using System.ComponentModel;
using FleetMate.Core.Services.Repos;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FleetMate.WinUI.Pages.Development.Repos;

/// <summary>
/// The Repos sidebar: the workspace's mode (Changes, History, Files) on top,
/// then tracked repositories grouped the way checkouts sit on disk — host,
/// then project or owner — with a filter and sort above them. Sort, grouping,
/// filter and collapsed groups persist, shared with FleetMate.GUI. Status
/// refreshes are driven by <see cref="ReposSegmentView"/>; fetch is on demand.
/// </summary>
public sealed partial class RepoSidebar : UserControl
{
    private RepoWorkspaceModel? _model;
    private RepoSidebarSort _sort = RepoWorkspacePreferences.ReadEnum("sidebar.sort", RepoSidebarSort.Name);
    private bool _grouped = RepoWorkspacePreferences.Read("sidebar.grouped") != "0";
    private readonly HashSet<string> _collapsed = (RepoWorkspacePreferences.Read("sidebar.collapsed") ?? "")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    private bool _rendering;
    private string? _rowsKey;

    public RepoSidebar()
    {
        InitializeComponent();
        FilterBox.Text = RepoWorkspacePreferences.Read("sidebar.filter") ?? "";
        RowMenus.Attach(RepoList, (item, _) => item is SidebarRepoRow row ? RowMenu(row) : null);
        UpdateSortTooltip();
    }

    /// <summary>Opens Settings › Repositories; the segment shows it in place of the workspace.</summary>
    public event EventHandler? SettingsRequested;

    public static Visibility Show(bool on) => DevelopmentUi.Show(on);

    public void Attach(RepoWorkspaceModel model)
    {
        _model = model;
        model.PropertyChanged += OnModelChanged;
        model.Git.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(GitPaneModel.Panel)) SyncPanel(); };
        SyncPanel();
        Render();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RepoWorkspaceModel.Tracked):
            case nameof(RepoWorkspaceModel.AllTracked):
            case nameof(RepoWorkspaceModel.IsEditing):
            case nameof(RepoWorkspaceModel.IsScanning):
            case nameof(RepoWorkspaceModel.NewClones):
            case nameof(RepoWorkspaceModel.SuggestedRoots):
            case nameof(RepoWorkspaceModel.Statuses):
            case nameof(RepoWorkspaceModel.Fetching):
            case nameof(RepoWorkspaceModel.SelectedId):
                Render();
                break;
            case nameof(RepoWorkspaceModel.IsRefreshing):
                UpdateBusy();
                break;
        }
    }

    // MARK: - Mode

    private void SyncPanel()
    {
        if (_model == null) return;
        var target = _model.Git.Panel switch
        {
            RepoPanel.History => PanelHistory,
            RepoPanel.Files => PanelFiles,
            _ => PanelChanges,
        };
        if (PanelBar.SelectedItem != target) PanelBar.SelectedItem = target;
    }

    private void PanelBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_model != null && sender.SelectedItem?.Tag is string tag && Enum.TryParse<RepoPanel>(tag, out var panel))
            _model.Git.Panel = panel;
    }

    // MARK: - List

    private string Filter => FilterBox.Text.Trim();

    private void Render()
    {
        if (_model == null) return;
        _rendering = true;
        try
        {
            var filtering = Filter.Length > 0;
            var listed = _model.IsEditing ? _model.AllTracked : _model.Tracked;
            var rows = new List<object>();
            if (_grouped)
            {
                foreach (var section in RepoSidebarOrganizer.Sections(listed, _model.Statuses, _sort, Filter))
                {
                    var sectionOpen = filtering || !_collapsed.Contains(section.Id);
                    rows.Add(new SidebarSectionRow(section.Id, section.Title, section.RepositoryCount, sectionOpen));
                    if (!sectionOpen) continue;
                    foreach (var group in section.Groups)
                    {
                        // While a filter is typed every group is open, so a match is
                        // never hidden inside a collapsed folder.
                        var groupOpen = filtering || !_collapsed.Contains(group.Id);
                        rows.Add(new SidebarGroupRow(group.Id, group.Scope, group.Title, groupOpen));
                        if (!groupOpen) continue;
                        rows.AddRange(group.Records.Select(r => Row(r, showScope: false, depth: 1)));
                    }
                }
            }
            else
            {
                rows.AddRange(RepoSidebarOrganizer.Flat(listed, _model.Statuses, _sort, Filter)
                    .Select(r => Row(r, showScope: true, depth: 0)));
            }
            // Only a real change replaces the list, so the 30-second status refresh
            // keeps the keyboard focus and scroll position where they were.
            var key = string.Join("\n", rows.Select(r => r is SidebarRepoRow repo ? $"{repo.Id}|{repo}|{repo.IsFetching}|{repo.IsEditing}|{repo.Depth}" : r.ToString()));
            if (key != _rowsKey)
            {
                _rowsKey = key;
                RepoList.ItemsSource = rows;
            }
            var shown = (RepoList.ItemsSource as List<object>) ?? rows;
            var selected = shown.OfType<SidebarRepoRow>().FirstOrDefault(r => r.Id == _model.SelectedId);
            if (!ReferenceEquals(RepoList.SelectedItem, selected)) RepoList.SelectedItem = selected;

            var empty = _model.AllTracked.Count == 0;
            if (empty && EmptyState.Visibility != Visibility.Visible) _ = _model.LoadSuggestedRootsAsync();
            EmptyState.Visibility = Show(empty);
            ChooseFolderButton.IsEnabled = !_model.IsScanning;
            var names = string.Join(", ", _model.SuggestedRoots);
            UseSuggestedText.Text = $"Use {names}";
            ToolTipService.SetToolTip(UseSuggestedButton, $"Track every clone in {names}, where FleetMate found some");
            UseSuggestedButton.Visibility = Show(_model.SuggestedRoots.Count > 0 && !_model.IsScanning);
            ScanningState.Visibility = Show(_model.IsScanning);
            EditButton.Content = _model.IsEditing ? "Done" : "Edit";
            EditButton.IsEnabled = _model.AllTracked.Count > 0 || _model.IsEditing;
            RenderNewClones();
            NoMatches.Visibility = Show(listed.Count > 0 && !rows.OfType<SidebarRepoRow>().Any() && filtering);
            FetchAllButton.IsEnabled = _model.Tracked.Count > 0 && _model.Fetching.Count == 0;
            UpdateBusy();
        }
        finally
        {
            _rendering = false;
        }
    }

    private SidebarRepoRow Row(RepoRecord record, bool showScope, int depth) =>
        new(record, _model!.Statuses.GetValueOrDefault(record.Id), _model.Fetching.Contains(record.Id), showScope, depth, _model.IsEditing);

    private void UpdateBusy() => BusyRing.IsActive = _model is { } m && (m.IsRefreshing || m.Fetching.Count > 0);

    private void RepoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering || _model == null) return;
        if (RepoList.SelectedItem is SidebarRepoRow row) _model.SelectedId = row.Id;
    }

    /// <summary>A click, Enter or Space on a section or group heading opens or closes it.</summary>
    private void RepoList_ItemClick(object sender, ItemClickEventArgs e)
    {
        var id = e.ClickedItem switch
        {
            SidebarSectionRow s => s.Id,
            SidebarGroupRow g => g.Id,
            _ => null,
        };
        if (id == null || Filter.Length > 0) return;
        if (!_collapsed.Remove(id)) _collapsed.Add(id);
        SaveCollapsed();
        Render();
        // Keep the keyboard on the heading just toggled.
        if ((RepoList.ItemsSource as List<object>)?.FirstOrDefault(r => r is SidebarSectionRow s && s.Id == id || r is SidebarGroupRow g && g.Id == id) is { } heading
            && RepoList.ContainerFromItem(heading) is Control container)
            container.Focus(FocusState.Keyboard);
    }

    private void SaveCollapsed() =>
        RepoWorkspacePreferences.Write("sidebar.collapsed", string.Join("\n", _collapsed.OrderBy(c => c)));

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RepoWorkspacePreferences.Write("sidebar.filter", FilterBox.Text.Length > 0 ? FilterBox.Text : null);
        Render();
    }

    // MARK: - Sort menu

    private void SortMenu_Opening(object sender, object e)
    {
        SortMenu.Items.Clear();
        SortMenu.Items.Add(new MenuFlyoutItem { Text = "Sort By", IsEnabled = false });
        foreach (var sort in Enum.GetValues<RepoSidebarSort>())
        {
            var item = new RadioMenuFlyoutItem { Text = sort.Title(), GroupName = "ReposSort", IsChecked = sort == _sort };
            item.Click += (_, _) =>
            {
                _sort = sort;
                RepoWorkspacePreferences.Write("sidebar.sort", sort.ToString());
                UpdateSortTooltip();
                Render();
            };
            SortMenu.Items.Add(item);
        }
        SortMenu.Items.Add(new MenuFlyoutSeparator());
        var group = new ToggleMenuFlyoutItem { Text = "Group by Source", IsChecked = _grouped };
        group.Click += (_, _) =>
        {
            _grouped = !_grouped;
            RepoWorkspacePreferences.Write("sidebar.grouped", _grouped ? null : "0");
            UpdateSortTooltip();
            Render();
        };
        SortMenu.Items.Add(group);
        if (_model is not { } model) return;
        SortMenu.Items.Add(new MenuFlyoutSeparator());
        SortMenu.Items.Add(RowMenus.Item("Add Folder…", ChooseFolders, glyph: "\uE8F4"));
        SortMenu.Items.Add(RowMenus.Item("Find New Clones", model.FindNewClonesAsync, glyph: "\uE721",
            tooltip: "Look for clones that appeared in the watched folders"));
        SortMenu.Items.Add(RowMenus.Item("Repository Settings…", () => SettingsRequested?.Invoke(this, EventArgs.Empty), glyph: "\uE713",
            tooltip: "Every repository you can see, where each lives, and which are tracked"));
        if (!_grouped) return;
        SortMenu.Items.Add(new MenuFlyoutSeparator());
        SortMenu.Items.Add(RowMenus.Item("Expand All", () => { _collapsed.Clear(); SaveCollapsed(); Render(); }));
        SortMenu.Items.Add(RowMenus.Item("Collapse All", () =>
        {
            var listed = model.IsEditing ? model.AllTracked : model.Tracked;
            var sections = RepoSidebarOrganizer.Sections(listed, new Dictionary<string, RepoStatus>(), RepoSidebarSort.Name);
            _collapsed.Clear();
            foreach (var g in sections.SelectMany(s => s.Groups)) _collapsed.Add(g.Id);
            SaveCollapsed();
            Render();
        }));
    }

    private void UpdateSortTooltip() =>
        ToolTipService.SetToolTip(SortButton, $"Sort by {_sort.Title().ToLowerInvariant()}{(_grouped ? ", grouped by source" : "")}");

    // MARK: - Actions

    private async void FetchAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_model != null) await _model.FetchAsync(_model.Tracked.Select(r => r.Id).ToList());
    }

    private MenuFlyout RowMenu(SidebarRepoRow row)
    {
        var model = _model!;
        var menu = new MenuFlyout();
        menu.Items.Add(RowMenus.Item("Fetch", () => model.FetchAsync(new[] { row.Id }), row.IsLocal, "\uE896"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(RowMenus.Item("Reveal in Explorer", () => { if (row.Record.Local is { } l) RepoShell.Reveal(l.Path); }, row.IsLocal, "\uE838"));
        menu.Items.Add(RowMenus.Item("Copy Path", () => DevelopmentUi.Copy(row.Record.Local?.Path), row.IsLocal, "\uE8C8"));
        menu.Items.Add(RowMenus.Item("Copy Name", () => DevelopmentUi.Copy(row.Record.Key.DisplayName)));
        if (row.Status?.Error is { } error)
            menu.Items.Add(RowMenus.Item("Copy Error", () => DevelopmentUi.Copy(error.Trim()), tooltip: "Copy git's full message for this repository"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(RowMenus.Item(row.HideLabel, () => model.SetHidden(row.Id, !row.IsHidden), glyph: row.EyeGlyph));
        menu.Items.Add(RowMenus.Item("Stop Tracking", () => StopTracking(row), tooltip: "Leave the clone on disk and remove it from the sidebar"));
        menu.Items.Add(RowMenus.Item("Delete Local Clone…", () => DeleteCloneAsync(row), row.IsLocal, "\uE74D"));
        return menu;
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        if (_model != null && (sender as FrameworkElement)?.Tag is SidebarRepoRow row) _model.SetHidden(row.Id, !row.IsHidden);
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_model == null || sender is not FrameworkElement { Tag: SidebarRepoRow row } button) return;
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        menu.Items.Add(RowMenus.Item("Stop Tracking", () => StopTracking(row), tooltip: "Leave the clone on disk and remove it from the sidebar"));
        menu.Items.Add(RowMenus.Item("Delete Local Clone…", () => DeleteCloneAsync(row), row.IsLocal, "\uE74D"));
        menu.ShowAt(button);
    }

    private void StopTracking(SidebarRepoRow row)
    {
        if (_model == null) return;
        _model.StopTracking(row.Id);
        RepoWorkspaceModel.NotifyRegistryChanged();
    }

    /// <summary>
    /// Deleting is destructive, so it checks first. Nothing to lose: one plain
    /// confirmation. Uncommitted changes, unpushed commits, stashes or other
    /// worktrees: a warning that says exactly what would be lost, and a button
    /// that says so. Either way the folder goes to the Recycle Bin, not away.
    /// </summary>
    private async Task DeleteCloneAsync(SidebarRepoRow row)
    {
        if (_model == null || row.Record.Local is not { } local) return;
        CloneRemovalCheck check;
        BusyRing.IsActive = true;
        try
        {
            check = await _model.RemovalCheckAsync(row.Id);
        }
        catch (RepoException ex)
        {
            await RepoDialogs.NoticeAsync(XamlRoot, "Could not check the clone", ex.Message);
            return;
        }
        finally
        {
            UpdateBusy();
        }
        var name = row.Record.Key.DisplayName;
        var confirmed = check.IsSafe
            ? await RepoDialogs.ConfirmAsync(XamlRoot, "Delete Local Clone", RepoWorkspaceText.SafeDeleteMessage(name, local.Path), "Move to Recycle Bin")
            : await RepoDialogs.CloneLossAsync(XamlRoot, name, local.Path, check.LossDescription);
        if (confirmed) await _model.RemoveCloneAsync(row.Id);
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_model == null) return;
        _model.IsEditing = !_model.IsEditing;
        if (!_model.IsEditing) _model.ReloadRecords();
    }

    // MARK: - Choose Folder

    private void ChooseFolderButton_Click(object sender, RoutedEventArgs e) => ChooseFolders();

    private async void UseSuggestedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_model == null || _model.SuggestedRoots.Count == 0) return;
        await _model.TrackFoldersAsync(_model.SuggestedRoots.ToList());
        RepoWorkspaceModel.NotifyRegistryChanged();
    }

    /// <summary>
    /// Picks one or more folders, watches them and tracks every clone found in
    /// them. No further questions: the person said where their code is.
    /// </summary>
    private async void ChooseFolders()
    {
        if (_model == null) return;
        var developer = RepoSettings.Expand(@"~\Developer");
        var folders = RepoFolderPicker.Pick("Choose the folder where you keep your code", multiple: true,
            Directory.Exists(developer) ? developer : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (folders.Count == 0) return;
        await _model.TrackFoldersAsync(folders);
        RepoWorkspaceModel.NotifyRegistryChanged();
    }

    // MARK: - New clones

    private void RenderNewClones()
    {
        var clones = _model!.NewClones;
        NewClonesBar.IsOpen = clones.Count > 0;
        if (clones.Count == 0) return;
        NewClonesBar.Message = RepoWorkspaceText.NewClonesSummary(clones, RepoWorkspaceText.ScanRoots(_model.Manager));
        ToolTipService.SetToolTip(NewClonesBar, string.Join("\n", clones.Select(c => c.Key?.DisplayName ?? c.Path)));
    }

    /// <summary>
    /// The review list: a box per offered clone, ticked unless its settings can
    /// run commands, which it then says. Ignore All records every one untracked;
    /// Add tracks the ticked ones and leaves the rest offered.
    /// </summary>
    private void ReviewNewClonesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_model is not { NewClones.Count: > 0 } model) return;
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        var panel = new StackPanel { Spacing = 8, Width = 400 };
        panel.Children.Add(new TextBlock { Text = "New repositories", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        var list = new StackPanel { Spacing = 4 };
        var add = new Button { Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(add, "ReposAddNewClones");
        void UpdateAdd()
        {
            var count = model.NewClones.Count(c => model.NewClonesSelected.Contains(c.Path));
            add.Content = count == 0 ? "Add" : $"Add {count}";
            add.IsEnabled = count > 0;
        }
        foreach (var clone in model.NewClones)
        {
            var name = clone.Key?.DisplayName ?? Path.GetFileName(clone.Path);
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            label.Children.Add(new TextBlock { Text = RepoShell.Abbreviate(clone.Path), Foreground = RepoBrushes.Secondary,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis });
            if (clone.RunsCommands)
                label.Children.Add(new TextBlock
                {
                    Text = "This repository's settings can run commands: " + string.Join(", ", clone.CommandConfig),
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = RepoBrushes.Warning,
                });
            var box = new CheckBox { Content = label, IsChecked = model.NewClonesSelected.Contains(clone.Path) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, name);
            var clonePath = clone.Path;
            box.Click += (_, _) =>
            {
                if (box.IsChecked == true) model.NewClonesSelected.Add(clonePath);
                else model.NewClonesSelected.Remove(clonePath);
                UpdateAdd();
            };
            list.Children.Add(box);
        }
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var ignore = new Button { Content = "Ignore All" };
        ToolTipService.SetToolTip(ignore, "Leave them untracked and don't ask about them again");
        var cancel = new Button { Content = "Cancel" };
        var buttons = new Grid { ColumnSpacing = 8 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(cancel, 1);
        Grid.SetColumn(add, 2);
        buttons.Children.Add(ignore);
        buttons.Children.Add(cancel);
        buttons.Children.Add(add);
        panel.Children.Add(buttons);
        ignore.Click += (_, _) => { flyout.Hide(); model.IgnoreNewClones(); };
        cancel.Click += (_, _) => flyout.Hide();
        add.Click += async (_, _) => { flyout.Hide(); await model.AddSelectedNewClonesAsync(); };
        UpdateAdd();
        flyout.Content = panel;
        flyout.ShowAt(ReviewNewClonesButton);
    }
}
