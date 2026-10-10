using System.Net.Http;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FleetMate.Core.Services.Repos;
using FleetMate.GUI.Views.Shared;
using Microsoft.Win32;
using ModernWpf.Controls;

namespace FleetMate.GUI.Views.Development.Repos;

/// <summary>
/// Settings › Repositories: the provider catalog joined with local checkouts,
/// and where clones go and which folders are scanned for existing ones.
/// Everything goes through the same <see cref="RepoManager"/> and registry
/// file as <c>fleetmate repos</c>, and every change tells the Repos workspace
/// to reload. Nothing here deletes a folder: Unlink only forgets a checkout.
/// </summary>
public partial class RepositoriesSettingsView : UserControl
{
    /// <summary>The Settings tab's header, so other views can open Settings here.</summary>
    public const string TabHeader = "Repositories";

    /// <summary>TicketsMate carries the Tickets tab alone, so it has no repositories to manage.</summary>
    public static Visibility TabVisibility =>
        FleetMate.Core.Config.AppEdition.Current.IsTicketsOnly ? Visibility.Collapsed : Visibility.Visible;

    private readonly RepoManager _manager;
    private List<RepoRecord> _records = new();
    private RepoSettings _saved = RepoSettings.Default;
    private List<string> _scanRoots = new();
    private readonly HashSet<string> _busy = new();
    private readonly Dictionary<string, string> _rowErrors = new();
    private DateTimeOffset? _fetchedAt;
    private bool _loading;
    private readonly DispatcherTimer _queryDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public RepositoriesSettingsView() : this(null) { }

    public RepositoriesSettingsView(RepoManager? manager)
    {
        _manager = manager ?? new RepoManager();
        InitializeComponent();
        _queryDelay.Tick += (_, _) => { _queryDelay.Stop(); RenderGroups(); };
        Loaded += (_, _) => Load();
    }

    /// <summary>Opens the Settings window on this tab.</summary>
    public static void OpenInSettings(Window? owner)
    {
        SettingsWindow.ShowSingle(owner);
        var window = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
        window?.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            foreach (var tabs in FindTabs(window))
            {
                var tab = tabs.Items.OfType<TabItem>().FirstOrDefault(t => Equals(t.Header, TabHeader));
                if (tab != null) tabs.SelectedItem = tab;
            }
        });
    }

    private static IEnumerable<TabControl> FindTabs(DependencyObject root)
    {
        if (root is System.Windows.Controls.Frame { Content: DependencyObject page }) foreach (var t in FindTabs(page)) yield return t;
        if (root is TabControl tabs) yield return tabs;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            foreach (var t in FindTabs(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) yield return t;
    }

    // ── Loading ─────────────────────────────────────────────────────────

    private void Load()
    {
        _loading = true;
        try
        {
            _saved = _manager.Settings();
            CloneRootBox.Text = _saved.CloneRoot;
            _scanRoots = _saved.ScanRoots.ToList();
            var cached = _manager.CachedCatalog();
            _fetchedAt = cached?.FetchedAt;
            ProviderErrors.ItemsSource = cached?.Errors ?? new List<string>();
            ReloadRecords();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _loading = false;
        }
        RenderLocations();
    }

    private void ReloadRecords()
    {
        try
        {
            _records = _manager.Records();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ShowError(ex.Message);
        }
        RenderGroups();
    }

    /// <summary>Rereads the registry here and tells the Repos workspace to do the same.</summary>
    private void RegistryChanged()
    {
        ReloadRecords();
        RepoWorkspaceModel.NotifyRegistryChanged();
    }

    // ── Locations ───────────────────────────────────────────────────────

    private RepoSettings Edited()
    {
        var settings = _manager.Settings();
        settings.CloneRoot = CloneRootBox.Text.Trim().Length > 0 ? CloneRootBox.Text.Trim() : RepoSettings.Default.CloneRoot;
        settings.ScanRoots = _scanRoots.ToList();
        return settings;
    }

    private void RenderLocations()
    {
        ScanRootsList.ItemsSource = null;
        ScanRootsList.ItemsSource = _scanRoots;
        DiscoverButton.IsEnabled = _scanRoots.Count > 0 && DiscoverRing.Visibility != Visibility.Visible;
        SaveButton.IsEnabled = CloneRootBox.Text.Trim() != _saved.CloneRoot || !_scanRoots.SequenceEqual(_saved.ScanRoots);
        LocationsFooter.Text =
            $@"New clones go to <clone root>\AzDevOps\<Project>\<Repo> or <clone root>\GitHub\<owner>\<repo>. " +
            $"Finding clones scans the folders below {_saved.ScanDepth} levels deep and links each checkout by its origin.";
    }

    private void OnSettingsEdited(object sender, TextChangedEventArgs e)
    {
        if (!_loading) RenderLocations();
    }

    private void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            var edited = Edited();
            _manager.UpdateSettings(s =>
            {
                s.CloneRoot = edited.CloneRoot;
                s.ScanRoots = edited.ScanRoots;
            });
            _saved = _manager.Settings();
            RenderLocations();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(ex.Message);
        }
    }

    private static string? ChooseFolder(string title, string? start)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (start != null)
        {
            try
            {
                var expanded = RepoSettings.Normalize(start);
                if (Directory.Exists(expanded)) dialog.InitialDirectory = expanded;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        }
        return dialog.ShowDialog() == true ? RepoShell.Abbreviate(dialog.FolderName) : null;
    }

    private void OnChooseCloneRoot(object sender, RoutedEventArgs e)
    {
        if (ChooseFolder("Clone repositories into", CloneRootBox.Text) is { } path) CloneRootBox.Text = path;
    }

    private void OnChooseScanRoot(object sender, RoutedEventArgs e)
    {
        if (ChooseFolder("Scan this folder for existing clones", _scanRoots.FirstOrDefault()) is { } path) AddScanRoot(path);
    }

    private void OnAddScanRoot(object sender, RoutedEventArgs e) => AddScanRoot(NewScanRootBox.Text);

    private void OnNewScanRootKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddScanRoot(NewScanRootBox.Text);
        e.Handled = true;
    }

    private void AddScanRoot(string value)
    {
        var root = value.Trim();
        if (root.Length == 0 || _scanRoots.Contains(root, StringComparer.OrdinalIgnoreCase)) return;
        _scanRoots.Add(root);
        NewScanRootBox.Text = "";
        RenderLocations();
    }

    private void OnRemoveScanRoot(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string root) return;
        _scanRoots.Remove(root);
        RenderLocations();
    }

    private async void OnDiscover(object sender, RoutedEventArgs e)
    {
        DiscoverRing.Visibility = Visibility.Visible;
        DiscoverButton.IsEnabled = false;
        try
        {
            var results = await _manager.DiscoverAsync(track: false, settings: Edited());
            var linked = results.Count(r => r.Action == DiscoveryAction.Linked);
            var existing = results.Count(r => r.Action == DiscoveryAction.AlreadyLinked);
            var duplicates = results.Count(r => r.Action == DiscoveryAction.Duplicate);
            var parts = new List<string> { $"Linked {linked} new", $"{existing} already linked" };
            if (duplicates > 0) parts.Add($"{duplicates} second copies left alone");
            DiscoverySummary.Text = string.Join(" · ", parts) + ". Turn on Track for the ones you work in.";
            DiscoverySummary.Visibility = Visibility.Visible;
            RegistryChanged();
        }
        catch (Exception ex) when (ex is RepoException or IOException or UnauthorizedAccessException)
        {
            ShowError(ex.Message);
        }
        finally
        {
            DiscoverRing.Visibility = Visibility.Collapsed;
            RenderLocations();
        }
    }

    /// <summary>Links any checkout by its origin, catalog or not.</summary>
    private async void OnLinkFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose one or more git checkouts", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        var failures = new List<string>();
        foreach (var folder in dialog.FolderNames)
        {
            try { await _manager.LinkAsync(folder, tracked: true); }
            catch (RepoException ex) { failures.Add(ex.Message); }
        }
        RegistryChanged();
        if (failures.Count > 0) ShowError(string.Join("\n", failures));
    }

    // ── Catalog ─────────────────────────────────────────────────────────

    private async void OnRefreshCatalog(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        RefreshRing.Visibility = Visibility.Visible;
        try
        {
            var config = ((App)Application.Current).Config;
            var service = RepoCatalogService.FromConfig(config, _manager.Settings());
            var catalog = await _manager.RefreshCatalogAsync(service);
            ProviderErrors.ItemsSource = catalog.Errors;
            _fetchedAt = catalog.FetchedAt;
            RegistryChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            ShowError(ex.Message);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            RefreshRing.Visibility = Visibility.Collapsed;
        }
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        _queryDelay.Stop();
        _queryDelay.Start();
    }

    private void RenderGroups()
    {
        var local = _records.Count(r => r.IsLocal);
        var tracked = _records.Count(r => r.IsTracked);
        CatalogFooter.Text = $"{_records.Count} repositories, {local} on this PC, {tracked} tracked." +
                             (_fetchedAt is { } at ? $" Catalog updated {RepoShell.Relative(at)}." : " Refresh to list what the providers hold.");
        GroupsPanel.Children.Clear();
        var query = QueryBox.Text.Trim();
        var groups = RepoRecordGroup.Groups(_records, query);
        if (groups.Count == 0)
        {
            GroupsPanel.Children.Add(new TextBlock
            {
                Text = _records.Count == 0
                    ? "No repositories yet. Refresh the catalog, find existing clones, or link a folder."
                    : $"No repositories match “{query}”.",
                Opacity = 0.7,
                Margin = new Thickness(4),
            });
            return;
        }
        foreach (var group in groups)
        {
            var rows = new StackPanel();
            rows.Children.Add(new TextBlock { Text = group.Title, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 6) });
            foreach (var record in group.Records) rows.Children.Add(Row(record));
            var card = new Border { Child = rows };
            card.SetResourceReference(StyleProperty, "CardStyle");
            GroupsPanel.Children.Add(card);
        }
    }

    private UIElement Row(RepoRecord record)
    {
        var busy = _busy.Contains(record.Id);
        var exists = record.Local is { } l && Directory.Exists(l.Path);

        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = record.Key.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (record.Catalog?.IsArchived == true) title.Children.Add(Chip("archived"));
        if (record.Catalog?.IsFork == true) title.Children.Add(Chip("fork"));
        if (record.Catalog == null) title.Children.Add(Chip("not in catalog"));
        var where = new TextBlock
        {
            Text = record.Local is { } local ? RepoShell.Abbreviate(local.Path) + (exists ? "" : " (missing)") : "Not on this PC",
            FontSize = 11,
            Opacity = record.IsLocal ? 0.75 : 0.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var info = new StackPanel { Children = { title, where }, VerticalAlignment = VerticalAlignment.Center };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (busy)
        {
            actions.Children.Add(new ProgressRing { IsActive = true, Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0) });
            actions.Children.Add(new TextBlock { Text = record.IsLocal ? "Linking…" : "Cloning…", FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        }
        else if (record.Local is { } checkout)
        {
            var reveal = SmallButton(new FontIcon { Glyph = "", FontSize = 12 }, "Reveal in Explorer", () => RepoShell.Reveal(checkout.Path));
            reveal.IsEnabled = exists;
            System.Windows.Automation.AutomationProperties.SetName(reveal, "Reveal in Explorer");
            actions.Children.Add(reveal);
            actions.Children.Add(SmallButton("Unlink", "Forget this checkout. The folder stays on disk.", () => Unlink(record)));
        }
        else
        {
            var clone = SmallButton("Clone", "Clone into the default layout under the clone root", () => _ = CloneAsync(record));
            clone.IsEnabled = record.Catalog != null;
            actions.Children.Add(clone);
            actions.Children.Add(SmallButton("Locate…", "Point at a checkout already on disk", () => _ = LocateAsync(record)));
        }
        var track = new ToggleSwitch
        {
            IsOn = record.IsTracked,
            IsEnabled = record.IsLocal && !busy,
            OnContent = "Tracked",
            OffContent = "Track",
            MinWidth = 0,
            Margin = new Thickness(10, 0, 0, 0),
            ToolTip = record.IsLocal ? "Tracked repositories appear in Development › Repos" : "Clone or locate it first",
        };
        System.Windows.Automation.AutomationProperties.SetName(track, $"Track {record.Key.DisplayName}");
        track.Toggled += (_, _) => SetTracked(record, track.IsOn);
        actions.Children.Add(track);

        var line = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        DockPanel.SetDock(actions, Dock.Right);
        line.Children.Add(actions);
        line.Children.Add(info);
        var stack = new StackPanel { Children = { line } };
        if (_rowErrors.TryGetValue(record.Id, out var error))
            stack.Children.Add(new TextBox
            {
                Text = "⚠ " + error, IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent,
                Foreground = RepoBrushes.Warning, TextWrapping = TextWrapping.Wrap, FontSize = 11, Padding = new Thickness(0),
            });
        return stack;
    }

    private static Border Chip(string text)
    {
        var tag = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(6, 0, 0, 0),
            Child = new TextBlock { Text = text, FontSize = 10 },
            VerticalAlignment = VerticalAlignment.Center,
        };
        tag.SetResourceReference(Border.BackgroundProperty, "SubtleFillBrush");
        return tag;
    }

    private static Button SmallButton(object content, string tip, Action action)
    {
        var button = new Button { Content = content, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0), ToolTip = tip };
        button.Click += (_, _) => action();
        return button;
    }

    private void SetTracked(RepoRecord record, bool tracked)
    {
        if (record.IsTracked == tracked) return;
        try
        {
            _manager.SetTracked(record.Id, tracked);
            _rowErrors.Remove(record.Id);
            RegistryChanged();
        }
        catch (Exception ex) when (ex is RepoException or IOException or UnauthorizedAccessException)
        {
            _rowErrors[record.Id] = ex.Message;
            RenderGroups();
        }
    }

    private void Unlink(RepoRecord record)
    {
        try
        {
            _manager.Unlink(record.Id);
            _rowErrors.Remove(record.Id);
            RegistryChanged();
        }
        catch (Exception ex) when (ex is RepoException or IOException or UnauthorizedAccessException)
        {
            _rowErrors[record.Id] = ex.Message;
            RenderGroups();
        }
    }

    private async Task CloneAsync(RepoRecord record)
    {
        _busy.Add(record.Id);
        _rowErrors.Remove(record.Id);
        RenderGroups();
        try
        {
            await _manager.CloneAsync(record.Id);
        }
        catch (Exception ex) when (ex is RepoException or IOException or UnauthorizedAccessException)
        {
            _rowErrors[record.Id] = ex.Message;
        }
        finally
        {
            _busy.Remove(record.Id);
            RegistryChanged();
        }
    }

    /// <summary>
    /// Points a catalog repository at a checkout already on disk. The folder's
    /// origin must name the same repository, or the link is refused.
    /// </summary>
    private async Task LocateAsync(RepoRecord record)
    {
        var dialog = new OpenFolderDialog { Title = $"Choose the checkout of {record.Key.DisplayName}" };
        try
        {
            var root = RepoSettings.Normalize(CloneRootBox.Text.Length > 0 ? CloneRootBox.Text : RepoSettings.Default.CloneRoot);
            if (Directory.Exists(root)) dialog.InitialDirectory = root;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        if (dialog.ShowDialog() != true) return;
        _busy.Add(record.Id);
        _rowErrors.Remove(record.Id);
        RenderGroups();
        try
        {
            await _manager.LinkAsync(dialog.FolderName, record.Id, tracked: true);
        }
        catch (RepoException ex)
        {
            _rowErrors[record.Id] = ex.Message;
        }
        finally
        {
            _busy.Remove(record.Id);
            RegistryChanged();
        }
    }

    // ── Errors ──────────────────────────────────────────────────────────

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void OnDismissError(object sender, RoutedEventArgs e) => ErrorBanner.Visibility = Visibility.Collapsed;
}
