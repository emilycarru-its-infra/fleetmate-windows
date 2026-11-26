using System.ComponentModel;
using FleetMate.Core.Development;
using FleetMate.Core.Services.Repos;
using FleetMate.Core.Services.Terminal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace FleetMate.WinUI.Pages.Development.Repos;

/// <summary>
/// Development › Repos, first and the default as in FleetMate for Mac: tracked
/// checkouts in the sidebar, the selected one's git pane, files and editor
/// beside it, and its Insights in the Graphs strip above. Everything goes
/// through the same <see cref="RepoWorkspaceModel"/> (FleetMate.Core) as
/// FleetMate.GUI and the same registry as <c>fleetmate repos</c>.
///
/// While shown, statuses refresh every 30 seconds; the toolbar search filters
/// the file tree and its Enter searches every file's contents. In demo mode
/// the workspace reads fictional repositories built for the run
/// (<see cref="RepoDemoFixture"/>), never the person's registry.
/// </summary>
public sealed partial class ReposSegmentView : UserControl, IDevelopmentSegmentView
{
    private DevelopmentPage? _page;
    private RepoWorkspaceModel? _model;
    private RepoInsightsView? _insights;
    private RepositoriesSettingsView? _settings;
    private Task? _ready;
    private bool _shown, _scanned, _isBusy;
    private string _search = "";
    private readonly DispatcherQueueTimer _statusTimer;

    public ReposSegmentView()
    {
        InitializeComponent();
        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(30);
        _statusTimer.Tick += async (_, _) => { if (_model != null) await _model.RefreshStatusesAsync(); };
        Sidebar.SettingsRequested += (_, _) => ShowSettings(true);
    }

    public DevelopmentSegment Segment => DevelopmentSegment.Repos;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            BusyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? BusyChanged;

    /// <summary>The selected repository's Insights, for the Graphs strip; null until the workspace is ready.</summary>
    public UIElement? Insights => _insights;

    public void Attach(DevelopmentPage page)
    {
        _page = page;
        _ready = PrepareAsync();
    }

    private async Task PrepareAsync()
    {
        RepoManager manager;
        if (App.Services.DemoMode)
        {
            RepoWorkspacePreferences.UseMemoryStore();
            IsBusy = true;
            PreparingState.Visibility = Visibility.Visible;
            Root.Children.OfType<FrameworkElement>().Where(c => c != PreparingState).ToList().ForEach(c => c.Opacity = 0);
            try
            {
                manager = await RepoDemoFixture.CreateAsync();
                // Open on the sample with work in progress, so Changes has something to show.
                if (manager.Records().FirstOrDefault(r => r.Key.Name == "harbor-api") is { } first)
                    RepoWorkspacePreferences.Write("selected", first.Id);
            }
            catch (Exception ex) when (ex is RepoException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                Log.Warning(ex, "[repos] The demo repositories could not be built");
                manager = new RepoManager(new RepoRegistryStore(Path.Combine(RepoDemoFixture.DefaultFolder, "Registry", "repos.json")));
            }
            finally
            {
                PreparingState.Visibility = Visibility.Collapsed;
                Root.Children.OfType<FrameworkElement>().ToList().ForEach(c => c.Opacity = 1);
                IsBusy = false;
            }
        }
        else
        {
            manager = new RepoManager();
        }

        var model = new RepoWorkspaceModel(manager);
        _model = model;
        Sidebar.Attach(model);
        Workspace.Attach(model);
        _insights = new RepoInsightsView(model) { OpenFile = OpenFile };
        model.PropertyChanged += OnModelChanged;
        // Settings links, clones or (un)tracks a repository: the list follows at once.
        RepoWorkspaceModel.RegistryChanged += () => DispatcherQueue.TryEnqueue(async () =>
        {
            model.ReloadRecords();
            await model.RefreshStatusesAsync();
        });
        model.FileFilter = _search;
        if (_page?.CurrentSegment == DevelopmentSegment.Repos) _page.RenderGraphs();
        if (_shown) await ShowAsync();
    }

    public async void SetShown(bool shown)
    {
        _shown = shown;
        if (!shown)
        {
            _statusTimer.Stop();
            MainWindow.SearchSubmitted -= OnSearchSubmitted;
            return;
        }
        MainWindow.SearchSubmitted -= OnSearchSubmitted;
        MainWindow.SearchSubmitted += OnSearchSubmitted;
        if (_model != null) await ShowAsync();
    }

    /// <summary>
    /// Selecting the first (or remembered) repository loads it; after that,
    /// coming back only refreshes what changed while the segment was away. The
    /// first time this launch, look for clones that appeared in the watched
    /// folders; the sidebar offers to add them.
    /// </summary>
    private async Task ShowAsync()
    {
        if (_model is not { } model) return;
        _statusTimer.Start();
        if (!_scanned)
        {
            _scanned = true;
            _ = model.LookForNewClonesAsync();
        }
        model.ReloadRecords();
        PublishContext();
        await model.RefreshStatusesAsync();
    }

    public void ApplySearch(string text)
    {
        _search = text ?? "";
        if (_model != null) _model.FileFilter = _search;
    }

    private void OnSearchSubmitted(object? sender, string text)
    {
        if (_shown) Workspace.SubmitSearch(text);
    }

    /// <summary>The page's Refresh while Repos shows: new clones, the registry, every status, and the selected checkout.</summary>
    public async Task RefreshAsync()
    {
        if (_ready != null) await _ready;
        if (_model is not { } model) return;
        _ = model.LookForNewClonesAsync();
        model.ReloadRecords();
        await model.RefreshStatusesAsync();
        await model.LoadSelectedAsync();
        _settings?.Load();
    }

    /// <summary>Open a file from Insights in the Files panel.</summary>
    public void OpenFile(string relativePath)
    {
        ShowSettings(false);
        Workspace.OpenFile(relativePath);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RepoWorkspaceModel.SelectedId)) return;
        PublishContext();
        if (_shown) _page?.RenderGraphs();
    }

    /// <summary>The Terminal hook: what an agent should know about the selected repository.</summary>
    private void PublishContext()
    {
        if (!_shown) return;
        if (_model?.Selected is not { } record)
        {
            DevelopmentContext.Select("repository", Array.Empty<ContextSelection>());
            return;
        }
        DevelopmentContext.Select("repository", new[]
        {
            new ContextSelection("repository", record.Key.DisplayName, new Dictionary<string, string?>
            {
                ["name"] = record.Key.Name,
                ["scope"] = record.Key.Scope,
                ["provider"] = record.Key.Provider.ToString(),
                ["path"] = record.Local?.Path,
                ["branch"] = _model.SelectedStatus?.Branch,
            }),
        });
    }

    // MARK: - QA capture

    /// <summary>
    /// The QA capture's Repos views, on the demo repositories: Changes, History,
    /// Files with a file open, a content search, the sidebar's edit mode,
    /// Settings › Repositories, and Insights in the Graphs strip.
    /// </summary>
    public async Task ShowCaptureViewAsync(string view)
    {
        if (_ready != null) await _ready;
        if (_model is not { } model) return;
        await ShowAsync();
        ShowSettings(view == "repos-settings");
        model.IsEditing = view == "repos-edit";
        model.ClearGrep();
        model.Git.Panel = view switch
        {
            "repos-history" => RepoPanel.History,
            "repos-files" or "repos-search" => RepoPanel.Files,
            _ => RepoPanel.Changes,
        };
        await model.LoadSelectedAsync();
        if (view == "repos-files") model.Open("src/Harbor.Core/Tides.cs");
        if (view == "repos-search") await model.GrepAsync("step");
        if (view == "repos-graphs") _insights?.Load();
        // The diff comes from git after the panel switches; let it arrive and draw before the capture.
        await model.Git.SyncDiffAsync();
        await Task.Delay(1500);
    }

    // MARK: - Settings › Repositories

    private void ShowSettings(bool show)
    {
        if (show && _model != null)
        {
            _settings ??= new RepositoriesSettingsView(_model.Manager);
            SettingsContent.Content = _settings;
            _settings.Load();
        }
        SettingsHost.Visibility = DevelopmentUi.Show(show);
        Workspace.Visibility = DevelopmentUi.Show(!show);
        if (show) SettingsBackButton.Focus(FocusState.Programmatic);
    }

    private void SettingsBackButton_Click(object sender, RoutedEventArgs e) => ShowSettings(false);
}
