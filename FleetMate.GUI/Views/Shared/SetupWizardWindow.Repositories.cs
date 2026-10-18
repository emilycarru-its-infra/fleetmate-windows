using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Services.Repos;
using FleetMate.GUI.Views.Development.Repos;
using Microsoft.Win32;
using Serilog;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// The Repositories step: "Where do you keep your code?" The folders start as
/// the ones already chosen, or on a first run the usual places that exist on
/// this PC. Every clone found in them is listed the way the Repos sidebar
/// groups them, all selected, and any can be left out. Catalog repositories
/// not on this PC can be picked for cloning, none by default. Finish watches
/// the folders, tracks the selected clones, records the rest as untracked so
/// they are never picked up behind the person's back, and clones in the
/// background.
/// </summary>
public sealed partial class SetupWizardWindow
{
    private const string RepositoriesStepTitle = "Repositories";

    private readonly RepoManager _repoManager = new();
    private List<string>? _repoRoots;
    private List<DiscoveredCheckout> _repoFound = new();
    /// <summary>Paths of found clones the person unticked.</summary>
    private readonly HashSet<string> _repoSkipped = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Catalog ids ticked for cloning.</summary>
    private readonly HashSet<string> _repoToClone = new(StringComparer.OrdinalIgnoreCase);
    private List<CatalogRepo> _repoCatalog = new();
    private bool _repoScanning;
    private bool _repoCatalogLoading;
    private string? _repoCatalogError;
    private int _repoScanVersion;
    private bool _repoStepShown;

    private UIElement RepositoriesStep()
    {
        _repoStepShown = true;
        if (_repoRoots == null)
        {
            RepoSettings settings;
            try { settings = _repoManager.Settings(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { settings = RepoSettings.Default; }
            _repoRoots = settings.RootsChosen ? settings.ScanRoots.ToList() : RepoDiscovery.ExistingCommonLocations();
            _repoCatalog = AvailableCatalog(_repoManager.CachedCatalog());
            _ = ScanRepositoriesAsync();
        }

        var panel = Heading("Where do you keep your code?",
            "Pick the folders that hold your git clones. FleetMate tracks every clone in them and keeps watching for new ones.");
        var content = new StackPanel();
        panel.Children.Add(content);

        void Render()
        {
            content.Children.Clear();
            Section(content, "Folders");
            if (_repoRoots!.Count == 0)
                content.Children.Add(Note("No folders yet. Add the one where you keep your code.", top: 4));
            foreach (var root in _repoRoots)
            {
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                var remove = new Button { Content = "Remove", Padding = new Thickness(10, 2, 10, 2), ToolTip = $"Stop looking in {root}" };
                remove.Click += (_, _) =>
                {
                    _repoRoots.Remove(root);
                    _ = ScanRepositoriesAsync();
                    Render();
                };
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                row.Children.Add(new TextBlock { Text = root, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                content.Children.Add(row);
            }
            var add = new Button { Content = "Add Folder…", Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(add, "SetupReposAddFolder");
            add.Click += (_, _) =>
            {
                var dialog = new OpenFolderDialog { Title = "Choose the folders where you keep your code", Multiselect = true };
                if (dialog.ShowDialog(this) != true) return;
                foreach (var folder in dialog.FolderNames.Select(RepoSettings.Abbreviate))
                    if (!_repoRoots.Contains(folder, StringComparer.OrdinalIgnoreCase)) _repoRoots.Add(folder);
                _ = ScanRepositoriesAsync();
                Render();
            };
            content.Children.Add(add);

            Section(content, "Clones found");
            if (_repoScanning)
            {
                var busy = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                busy.Children.Add(new ModernWpf.Controls.ProgressRing { IsActive = true, Width = 14, Height = 14, Margin = new Thickness(0, 0, 8, 0) });
                busy.Children.Add(Note("Looking for clones…", top: 0));
                content.Children.Add(busy);
            }
            else
            {
                var usable = _repoFound.Where(c => c.Key != null).ToList();
                if (usable.Count == 0)
                    content.Children.Add(Note(_repoRoots.Count == 0 ? "Add a folder to see its clones." : "No git clones in these folders.", top: 4));
                else
                {
                    content.Children.Add(Note($"{usable.Count - usable.Count(c => _repoSkipped.Contains(c.Path))} of {usable.Count} selected. Untick any you don't work in.", top: 2));
                    var records = usable.Select(c => new RepoRecord(c.Key!, null,
                        new RepoRegistryEntry { Key = c.Key!, Path = c.Path, Tracked = true })).ToList();
                    foreach (var group in RepoRecordGroup.Groups(records))
                        content.Children.Add(GroupPanel(group, r => !_repoSkipped.Contains(r.Local!.Path), (r, on) =>
                        {
                            if (on) _repoSkipped.Remove(r.Local!.Path); else _repoSkipped.Add(r.Local!.Path);
                        }, r => RepoShell.Abbreviate(r.Local!.Path), Render));
                }
                var noRemote = _repoFound.Count(c => c.Key == null);
                if (noRemote > 0)
                    content.Children.Add(Note($"{noRemote} clone{(noRemote == 1 ? " has" : "s have")} no origin remote FleetMate can read, and {(noRemote == 1 ? "is" : "are")} left out.", top: 6));
            }

            Section(content, "Clone more (optional)", "Repositories you can see on Azure DevOps and GitHub that are not on this PC.");
            var load = new Button { Content = _repoCatalog.Count > 0 ? "Refresh Catalog" : "Load Catalog", Padding = new Thickness(12, 4, 12, 4),
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), IsEnabled = !_repoCatalogLoading };
            load.Click += async (_, _) => { await LoadCatalogAsync(); Render(); };
            content.Children.Add(load);
            if (_repoCatalogLoading) content.Children.Add(Note("Asking Azure DevOps and GitHub…", top: 4));
            if (_repoCatalogError != null) content.Children.Add(Note(_repoCatalogError, top: 4));
            var foundIds = _repoFound.Where(c => c.Key != null).Select(c => c.Key!.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var available = _repoCatalog.Where(c => !foundIds.Contains(c.Id)).Select(c => new RepoRecord(c.Key, c, null)).ToList();
            if (available.Count > 0)
            {
                string clonePath;
                try { clonePath = _repoManager.Settings().CloneRoot; } catch (Exception) { clonePath = RepoSettings.Default.CloneRoot; }
                content.Children.Add(Note($"Ticked ones are cloned into {clonePath} in the background once setup finishes.", top: 6));
                foreach (var group in RepoRecordGroup.Groups(available))
                    content.Children.Add(GroupPanel(group, r => _repoToClone.Contains(r.Id), (r, on) =>
                    {
                        if (on) _repoToClone.Add(r.Id); else _repoToClone.Remove(r.Id);
                    }, r => r.Catalog?.DefaultBranch is { } b ? $"default branch {b}" : "", Render));
            }
        }

        _repoRender = Render;
        Render();
        return panel;
    }

    private Action? _repoRender;

    /// <summary>One project or owner: a box for the whole group, then one per repository.</summary>
    private static FrameworkElement GroupPanel(RepoRecordGroup group, Func<RepoRecord, bool> isOn,
        Action<RepoRecord, bool> set, Func<RepoRecord, string> detail, Action render)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var on = group.Records.Count(isOn);
        var all = new CheckBox
        {
            IsThreeState = false,
            IsChecked = on == group.Records.Count ? true : on == 0 ? false : null,
            Content = new TextBlock { Text = group.Title, FontWeight = FontWeights.SemiBold },
        };
        System.Windows.Automation.AutomationProperties.SetName(all, group.Title);
        all.Click += (_, _) =>
        {
            var target = on != group.Records.Count;
            foreach (var record in group.Records) set(record, target);
            render();
        };
        panel.Children.Add(all);
        foreach (var record in group.Records)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = record.Key.Name });
            var extra = detail(record);
            if (extra.Length > 0)
            {
                var hint = new TextBlock { Text = "  " + extra, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
                label.Children.Add(hint);
            }
            var box = new CheckBox { IsChecked = isOn(record), Content = label, Margin = new Thickness(22, 2, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(box, record.Key.DisplayName);
            box.Click += (_, _) => { set(record, box.IsChecked == true); render(); };
            panel.Children.Add(box);
        }
        return panel;
    }

    private async Task ScanRepositoriesAsync()
    {
        var version = ++_repoScanVersion;
        _repoScanning = true;
        _repoRender?.Invoke();
        try
        {
            var roots = _repoRoots!.ToList();
            var found = roots.Count == 0 ? new List<DiscoveredCheckout>() : await Task.Run(() => _repoManager.PreviewAsync(roots));
            if (version != _repoScanVersion) return;
            _repoFound = found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or RepoException)
        {
            Log.Warning(ex, "[setup] Could not scan for clones");
        }
        finally
        {
            if (version == _repoScanVersion)
            {
                _repoScanning = false;
                if (StepTitles[_step] == RepositoriesStepTitle) _repoRender?.Invoke();
            }
        }
    }

    private async Task LoadCatalogAsync()
    {
        _repoCatalogLoading = true;
        _repoCatalogError = null;
        _repoRender?.Invoke();
        try
        {
            var config = (Application.Current as App)?.Config ?? _config;
            var catalog = await _repoManager.RefreshCatalogAsync(RepoCatalogService.FromConfig(config, _repoManager.Settings()));
            _repoCatalog = AvailableCatalog(catalog);
            if (catalog.Errors.Count > 0) _repoCatalogError = string.Join(Environment.NewLine, catalog.Errors);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or RepoException)
        {
            _repoCatalogError = ex.Message;
        }
        finally
        {
            _repoCatalogLoading = false;
        }
    }

    /// <summary>Catalog repositories worth offering: not archived, not already on this PC.</summary>
    private List<CatalogRepo> AvailableCatalog(RepoCatalog? catalog)
    {
        if (catalog == null) return new();
        HashSet<string> local;
        try { local = _repoManager.Registry().Repos.Values.Where(e => Directory.Exists(e.Path)).Select(e => e.Key.Id).ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch (Exception) { local = new(StringComparer.OrdinalIgnoreCase); }
        return catalog.Repos.Where(r => !r.IsArchived && !local.Contains(r.Id)).ToList();
    }

    private string RepositoriesSummary()
    {
        var usable = _repoFound.Where(c => c.Key != null).ToList();
        var tracked = usable.Count(c => !_repoSkipped.Contains(c.Path));
        var parts = new List<string>
        {
            _repoRoots is { Count: > 0 } roots ? "Watching " + string.Join(", ", roots) : "No folders watched",
            $"{tracked} clone{(tracked == 1 ? "" : "s")} tracked",
        };
        if (_repoToClone.Count > 0) parts.Add($"{_repoToClone.Count} to clone");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Watches the chosen folders, tracks the ticked clones and records the
    /// unticked ones as untracked, then clones the ticked catalog repositories
    /// one at a time in the background.
    /// </summary>
    private void SaveRepositories()
    {
        if (!_repoStepShown || _repoRoots == null) return;
        try
        {
            var roots = _repoRoots.Select(RepoSettings.Abbreviate).ToList();
            _repoManager.UpdateSettings(s =>
            {
                s.ScanRoots = roots;
                s.RootsChosen = true;
            });
            var usable = _repoFound.Where(c => c.Key != null).ToList();
            _repoManager.Register(usable.Where(c => !_repoSkipped.Contains(c.Path)), track: true);
            _repoManager.Register(usable.Where(c => _repoSkipped.Contains(c.Path)));
            RepoWorkspaceModel.NotifyRegistryChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or RepoException)
        {
            Log.Warning(ex, "[setup] Could not save repositories");
            MessageBox.Show(this, $"Couldn't save the repositories:\n{ex.Message}", $"{AppName} Setup",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var toClone = _repoToClone.ToList();
        if (toClone.Count == 0) return;
        var manager = _repoManager;
        var dispatcher = Application.Current.Dispatcher;
        _ = Task.Run(async () =>
        {
            foreach (var id in toClone)
            {
                try
                {
                    await manager.CloneAsync(id);
                    _ = dispatcher.BeginInvoke(RepoWorkspaceModel.NotifyRegistryChanged);
                }
                catch (Exception ex) when (ex is RepoException or IOException or UnauthorizedAccessException)
                {
                    Log.Warning(ex, "[setup] Could not clone {Repository}", id);
                }
            }
        });
    }
}
