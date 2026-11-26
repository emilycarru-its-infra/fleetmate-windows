using System.Net.Http;
using FleetMate.Core.Services.Repos;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace FleetMate.WinUI.Pages.Development.Repos;

/// <summary>
/// Settings › Repositories: the provider catalog joined with local checkouts,
/// and where clones go and which folders are scanned for existing ones.
/// Everything goes through the same <see cref="RepoManager"/> and registry
/// file as <c>fleetmate repos</c>, and every change tells the Repos workspace
/// to reload. Nothing here deletes a folder: Unlink only forgets a checkout.
/// </summary>
public sealed partial class RepositoriesSettingsView : UserControl
{
    private readonly RepoManager _manager;
    private List<RepoRecord> _records = new();
    private RepoSettings _saved = RepoSettings.Default;
    private List<string> _scanRoots = new();
    private readonly HashSet<string> _busy = new();
    private readonly Dictionary<string, string> _rowErrors = new();
    private DateTimeOffset? _fetchedAt;
    private bool _loading;
    private readonly DispatcherQueueTimer _queryDelay;

    public RepositoriesSettingsView(RepoManager manager)
    {
        _manager = manager;
        InitializeComponent();
        _queryDelay = DispatcherQueue.CreateTimer();
        _queryDelay.Interval = TimeSpan.FromMilliseconds(200);
        _queryDelay.IsRepeating = false;
        _queryDelay.Tick += (_, _) => RenderGroups();
    }

    /// <summary>Rereads the settings, catalog cache and registry: each time the view opens.</summary>
    public void Load()
    {
        _loading = true;
        try
        {
            _saved = _manager.Settings();
            CloneRootBox.Text = _saved.CloneRoot;
            _scanRoots = _saved.ScanRoots.ToList();
            AutoTrackBox.IsChecked = _saved.AutoTrackNewClones;
            var cached = _manager.CachedCatalog();
            _fetchedAt = cached?.FetchedAt;
            RenderProviderErrors(cached?.Errors ?? new List<string>());
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

    // MARK: - Locations

    private RepoSettings Edited()
    {
        var settings = _manager.Settings();
        settings.CloneRoot = CloneRootBox.Text.Trim().Length > 0 ? CloneRootBox.Text.Trim() : RepoSettings.Default.CloneRoot;
        settings.ScanRoots = _scanRoots.ToList();
        settings.AutoTrackNewClones = AutoTrackBox.IsChecked == true;
        return settings;
    }

    private void RenderLocations()
    {
        ScanRootsList.ItemsSource = null;
        ScanRootsList.ItemsSource = _scanRoots.ToList();
        DiscoverButton.IsEnabled = _scanRoots.Count > 0 && !DiscoverRing.IsActive;
        SaveButton.IsEnabled = CloneRootBox.Text.Trim() != _saved.CloneRoot || !_scanRoots.SequenceEqual(_saved.ScanRoots)
            || (AutoTrackBox.IsChecked == true) != _saved.AutoTrackNewClones;
        LocationsFooter.Text = RepoWorkspaceText.LocationsFooter(_saved.ScanDepth);
    }

    private void CloneRootBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading) RenderLocations();
    }

    private void AutoTrackBox_Click(object sender, RoutedEventArgs e)
    {
        if (!_loading) RenderLocations();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var edited = Edited();
            _manager.UpdateSettings(s =>
            {
                s.CloneRoot = edited.CloneRoot;
                s.ScanRoots = edited.ScanRoots;
                s.AutoTrackNewClones = edited.AutoTrackNewClones;
                // Saving the watched folders is choosing them.
                s.RootsChosen = true;
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
        string? folder = null;
        if (start != null)
        {
            try { folder = RepoSettings.Normalize(start); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        }
        return RepoFolderPicker.Pick(title, multiple: false, folder).FirstOrDefault() is { } path ? RepoShell.Abbreviate(path) : null;
    }

    private void ChooseCloneRootButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseFolder("Clone repositories into", CloneRootBox.Text) is { } path) CloneRootBox.Text = path;
    }

    private void ChooseScanRootButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseFolder("Watch this folder for clones", _scanRoots.FirstOrDefault()) is { } path) AddScanRoot(path);
    }

    private void AddScanRootButton_Click(object sender, RoutedEventArgs e) => AddScanRoot(NewScanRootBox.Text);

    private void NewScanRootBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
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

    private void RemoveScanRootButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string root) return;
        _scanRoots.Remove(root);
        RenderLocations();
    }

    private async void DiscoverButton_Click(object sender, RoutedEventArgs e)
    {
        DiscoverRing.IsActive = true;
        DiscoverButton.IsEnabled = false;
        try
        {
            var results = await _manager.DiscoverAsync(track: false, settings: Edited());
            DiscoverySummary.Text = RepoWorkspaceText.DiscoverySummary(results);
            DiscoverySummary.Visibility = Visibility.Visible;
            RegistryChanged();
        }
        catch (Exception ex) when (ex is RepoException or IOException or UnauthorizedAccessException)
        {
            ShowError(ex.Message);
        }
        finally
        {
            DiscoverRing.IsActive = false;
            RenderLocations();
        }
    }

    /// <summary>Links any checkout by its origin, catalog or not.</summary>
    private async void LinkFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folders = RepoFolderPicker.Pick("Choose one or more git checkouts", multiple: true);
        if (folders.Count == 0) return;
        var failures = new List<string>();
        foreach (var folder in folders)
        {
            try { await _manager.LinkAsync(folder, tracked: true); }
            catch (RepoException ex) { failures.Add(ex.Message); }
        }
        RegistryChanged();
        if (failures.Count > 0) ShowError(string.Join("\n", failures));
    }

    // MARK: - Catalog

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        RefreshRing.IsActive = true;
        try
        {
            var service = RepoCatalogService.FromConfig(App.Services.Config, _manager.Settings());
            var catalog = await _manager.RefreshCatalogAsync(service);
            RenderProviderErrors(catalog.Errors);
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
            RefreshRing.IsActive = false;
        }
    }

    private void RenderProviderErrors(IEnumerable<string> errors)
    {
        ProviderErrors.Children.Clear();
        foreach (var error in errors)
            ProviderErrors.Children.Add(new TextBlock
            {
                Text = "⚠ " + error,
                Foreground = RepoBrushes.Warning,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            });
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _queryDelay.Stop();
        _queryDelay.Start();
    }

    private void RenderGroups()
    {
        CatalogFooter.Text = RepoWorkspaceText.CatalogFooter(_records, _fetchedAt);
        GroupsPanel.Children.Clear();
        var query = QueryBox.Text.Trim();
        var groups = RepoRecordGroup.Groups(_records, query);
        if (groups.Count == 0)
        {
            GroupsPanel.Children.Add(new TextBlock
            {
                Text = RepoWorkspaceText.CatalogEmpty(_records.Count, query),
                Foreground = RepoBrushes.Secondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4),
            });
            return;
        }
        foreach (var group in groups)
        {
            var rows = new StackPanel { Spacing = 2 };
            rows.Children.Add(new TextBlock { Text = group.Title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 0, 0, 6) });
            foreach (var record in group.Records) rows.Children.Add(Row(record));
            GroupsPanel.Children.Add(new Border { Child = rows, Style = (Style)Application.Current.Resources["CardStyle"] });
        }
    }

    private UIElement Row(RepoRecord record)
    {
        var busy = _busy.Contains(record.Id);
        var exists = record.Local is { } l && Directory.Exists(l.Path);

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        title.Children.Add(new TextBlock { Text = record.Key.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (record.Catalog?.IsArchived == true) title.Children.Add(Chip("archived"));
        if (record.Catalog?.IsFork == true) title.Children.Add(Chip("fork"));
        if (record.Catalog == null) title.Children.Add(Chip("not in catalog"));
        var where = new TextBlock
        {
            Text = record.Local is { } local ? RepoShell.Abbreviate(local.Path) + (exists ? "" : " (missing)") : "Not on this PC",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = record.IsLocal ? RepoBrushes.Secondary : DevelopmentUi.Theme("TextFillColorTertiaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var info = new StackPanel { Children = { title, where }, VerticalAlignment = VerticalAlignment.Center };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (busy)
        {
            actions.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
            actions.Children.Add(new TextBlock { Text = record.IsLocal ? "Linking…" : "Cloning…", Foreground = RepoBrushes.Secondary, VerticalAlignment = VerticalAlignment.Center });
        }
        else if (record.Local is { } checkout)
        {
            var reveal = SmallButton(new FontIcon { Glyph = "\uE838", FontSize = 14 }, "Reveal in Explorer", () => RepoShell.Reveal(checkout.Path));
            reveal.IsEnabled = exists;
            AutomationProperties.SetName(reveal, $"Reveal {record.Key.DisplayName} in Explorer");
            actions.Children.Add(reveal);
            var unlink = SmallButton("Unlink", "Forget this checkout. The folder stays on disk.", () => Unlink(record));
            AutomationProperties.SetName(unlink, $"Unlink {record.Key.DisplayName}");
            actions.Children.Add(unlink);
        }
        else
        {
            var clone = SmallButton("Clone", "Clone into the default layout under the clone root", () => _ = CloneAsync(record));
            clone.IsEnabled = record.Catalog != null;
            AutomationProperties.SetName(clone, $"Clone {record.Key.DisplayName}");
            actions.Children.Add(clone);
            var locate = SmallButton("Locate…", "Point at a checkout already on disk", () => _ = LocateAsync(record));
            AutomationProperties.SetName(locate, $"Locate {record.Key.DisplayName}");
            actions.Children.Add(locate);
        }
        var track = new ToggleSwitch
        {
            IsOn = record.IsTracked,
            IsEnabled = record.IsLocal && !busy,
            OnContent = "Tracked",
            OffContent = "Track",
            MinWidth = 0,
        };
        ToolTipService.SetToolTip(track, record.IsLocal ? "Tracked repositories appear in Development › Repos" : "Clone or locate it first");
        AutomationProperties.SetName(track, $"Track {record.Key.DisplayName}");
        track.Toggled += (_, _) => SetTracked(record, track.IsOn);
        actions.Children.Add(track);

        var line = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 4, 0, 4) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(actions, 1);
        line.Children.Add(info);
        line.Children.Add(actions);
        var stack = new StackPanel { Children = { line } };
        if (_rowErrors.TryGetValue(record.Id, out var error))
            stack.Children.Add(new TextBlock
            {
                Text = "⚠ " + error,
                Foreground = RepoBrushes.Warning,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            });
        return stack;
    }

    private static Border Chip(string text) => new()
    {
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(6, 0, 6, 1),
        Child = new TextBlock { Text = text, FontSize = 11 },
        VerticalAlignment = VerticalAlignment.Center,
        Background = DevelopmentUi.Theme("SubtleFillColorSecondaryBrush"),
        BorderBrush = DevelopmentUi.Theme("CardStrokeColorDefaultBrush"),
        BorderThickness = new Thickness(1),
    };

    private static Button SmallButton(object content, string tip, Action action)
    {
        var button = new Button { Content = content };
        ToolTipService.SetToolTip(button, tip);
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
        string? start = null;
        try { start = RepoSettings.Normalize(CloneRootBox.Text.Length > 0 ? CloneRootBox.Text : RepoSettings.Default.CloneRoot); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        if (RepoFolderPicker.Pick($"Choose the checkout of {record.Key.DisplayName}", multiple: false, start).FirstOrDefault() is not { } folder) return;
        _busy.Add(record.Id);
        _rowErrors.Remove(record.Id);
        RenderGroups();
        try
        {
            await _manager.LinkAsync(folder, record.Id, tracked: true);
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

    private void ShowError(string message)
    {
        ErrorBar.Title = "Repositories";
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
