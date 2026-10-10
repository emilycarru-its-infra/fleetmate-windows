using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FleetMate.Core.Models;
using FleetMate.Core.Services;
using FleetMate.GUI.Views.Devices;
using FleetMate.GUI.Views.Inventory;
using FleetMate.GUI.Views.Tickets;
using FleetMate.GUI.Views.Projects;
using FleetMate.GUI.Views.Identity;
using FleetMate.GUI.Views.Manage;

namespace FleetMate.GUI.Views.Shared;

public partial class MainWindow : Window
{
    // Page cache: keep views alive across tab switches
    private readonly Dictionary<string, Page> _pageCache = new();

    public MainWindow()
    {
        InitializeComponent();

        // Development is the launch tab; there is no Dashboard. TicketsMate
        // opens on Tickets, the only tab it has.
        ContentFrame.Navigate(GetOrCreatePage(LaunchTab));
        (TicketsOnly ? TabTickets : TabDevelopment).IsChecked = true;
        if (TicketsOnly) ApplyTicketsOnlyChrome();
        UpdateGraphsButton();
        InitShortcuts();
        SearchBox.GotKeyboardFocus += OnSearchFocused;

        if (Application.Current is App app)
        {
            app.AppErrorChanged += (_, _) => Dispatcher.Invoke(UpdateAppError);
            BindElevationMonitor(app);
            app.ServicesReloaded += () => Dispatcher.Invoke(() => BindElevationMonitor(app));
        }

        // Re-derive the elevation label between polls: the expires tag lets
        // Ready turn Expired on time without another az call.
        _elevationTick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _elevationTick.Tick += (_, _) => UpdateElevationStatus();
        _elevationTick.Start();

        // The terminal panel: hide on request, end every session with the
        // window, and with AgentAutoStart (on by default) open a session at launch.
        Terminal.HideRequested += (_, _) => SetTerminalVisible(false);
        Terminal.FullWindowRequested += (_, _) => ToggleFullWindow();
        // While the panel is closed the strip stands in for it.
        TerminalStrip.Panel = Terminal;
        TerminalStrip.ShowRequested += (_, _) => SetTerminalVisible(true);
        Closed += (_, _) => Terminal.DisposeAll();
        Loaded += (_, _) => InitToolbarFit();
        InitPreferences();
        // An agent session started at launch must not take the keyboard.
        // TicketsMate has no terminal.
        if (!TicketsOnly && Application.Current is App { Config.Terminal.AgentAutoStart: true })
            Loaded += (_, _) =>
            {
                SetTerminalVisible(true, takeFocus: false);
                Terminal.OpenDefaultSession(takeFocus: false);
            };
    }

    // ── Elevation status ──────────────────────────────────────────

    private readonly System.Windows.Threading.DispatcherTimer _elevationTick;
    private ElevationMonitor? _elevationMonitor;

    private void BindElevationMonitor(App app)
    {
        if (_elevationMonitor != null) _elevationMonitor.Changed -= OnElevationChanged;
        _elevationMonitor = app.ElevationMonitor;
        if (_elevationMonitor != null) _elevationMonitor.Changed += OnElevationChanged;
        UpdateElevationStatus();
    }

    private void OnElevationChanged() => Dispatcher.BeginInvoke(UpdateElevationStatus);

    private void UpdateElevationStatus()
    {
        var monitor = _elevationMonitor;
        if (monitor is not { Enabled: true })
        {
            ElevationButton.Visibility = Visibility.Collapsed;
            return;
        }

        var overall = monitor.Overall();
        ElevationButton.Visibility = Visibility.Visible;
        ElevationText.Text = ElevationStatusText.Label(overall);
        ElevationDot.Fill = new SolidColorBrush(overall switch
        {
            ElevationSessionState.Ready => Colors.Green,
            ElevationSessionState.Starting => Colors.DodgerBlue,
            ElevationSessionState.Expired => Colors.Orange,
            ElevationSessionState.None => Colors.Gray,
            _ => Colors.Goldenrod,
        });
        ElevationButton.ToolTip = string.Join(Environment.NewLine, ElevationMonitor.DesktopDomains.Select(d =>
                ElevationStatusText.DomainLine(d, monitor.StateOf(d), monitor.ExpiresOf(d), monitor.ErrorOf(d), DateTimeOffset.Now)))
            + Environment.NewLine + Environment.NewLine
            + "Click to re-check and start any session that is not running.";
    }

    private async void OnElevationClicked(object sender, RoutedEventArgs e)
    {
        if (_elevationMonitor is not { Enabled: true } monitor) return;
        ElevationText.Text = "Checking…";
        try { await monitor.PrewarmAsync(); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Elevation check failed"); }
        UpdateElevationStatus();
    }

    // ── Edition ──────────────────────────────────────────────────────────

    private static bool TicketsOnly => FleetMate.Core.Config.AppEdition.Current.IsTicketsOnly;

    private static string LaunchTab => TicketsOnly ? "Tickets" : "Development";

    /// <summary>
    /// TicketsMate's window: its own title and icon, no tab bar for its one
    /// tab, and no terminal button or strip.
    /// </summary>
    private void ApplyTicketsOnlyChrome()
    {
        Title = FleetMate.Core.Config.AppEdition.Current.Name;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/TicketsMate.ico"));
        TabBarBorder.Visibility = Visibility.Collapsed;
        TerminalToggleButton.Visibility = Visibility.Collapsed;
        TerminalStrip.Visibility = Visibility.Collapsed;
    }

    /// <summary>Ctrl+`, Ctrl+T and Ctrl+Shift+Enter: the keys that open or size the terminal.</summary>
    private static bool IsTerminalKey(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys mods)
    {
        const System.Windows.Input.ModifierKeys Ctrl = System.Windows.Input.ModifierKeys.Control;
        const System.Windows.Input.ModifierKeys Shift = System.Windows.Input.ModifierKeys.Shift;
        return (key == System.Windows.Input.Key.Oem3 && mods == Ctrl)
            || (key == System.Windows.Input.Key.T && (mods == Ctrl || mods == (Ctrl | Shift)))
            || (key == System.Windows.Input.Key.Enter && mods == (Ctrl | Shift));
    }

    // ── Terminal panel ───────────────────────────────────────────────────

    private readonly FleetMate.Core.Services.Terminal.TerminalLayoutState _terminalLayout = new();

    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // App-wide terminal keys. Inside a terminal the page reports them
        // itself (WebView2 keeps keys to itself), so these cover the rest.
        var mods = System.Windows.Input.Keyboard.Modifiers;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        const System.Windows.Input.ModifierKeys Ctrl = System.Windows.Input.ModifierKeys.Control;
        const System.Windows.Input.ModifierKeys Shift = System.Windows.Input.ModifierKeys.Shift;

        if (TicketsOnly && IsTerminalKey(key, mods))
        {
            // TicketsMate has no terminal; its keys do nothing.
        }
        else if (key == System.Windows.Input.Key.Oem3 && mods == Ctrl)
        {
            ToggleTerminal();
            e.Handled = true;
        }
        else if (key == System.Windows.Input.Key.T && (mods == Ctrl || mods == (Ctrl | Shift)))
        {
            // Ctrl+T opens a new session from anywhere in the app.
            SetTerminalVisible(true, takeFocus: false);
            Terminal.OpenDefaultSession();
            e.Handled = true;
        }
        else if (key == System.Windows.Input.Key.Enter && mods == (Ctrl | Shift))
        {
            ToggleFullWindow();
            e.Handled = true;
        }
        else if (key == System.Windows.Input.Key.G && mods == (Ctrl | System.Windows.Input.ModifierKeys.Alt))
        {
            ToggleGraphs();
            e.Handled = true;
        }
        else if (HandleSearchShortcut(key, mods))
        {
            e.Handled = true;
        }
        else if (mods == Ctrl && VisibleTabShortcut(key) is { } tab)
        {
            // Ctrl+1–8 switch tabs, in the order of the tabs showing.
            NavigateToTab(tab);
            e.Handled = true;
        }
        else if (HandleAppShortcut(key, mods))
        {
            e.Handled = true;
        }
    }

    private double AvailableHeight => Math.Max(0, RootGrid.ActualHeight - RootGrid.RowDefinitions[0].ActualHeight - TerminalDivider.ActualHeight);

    private void OnDividerDragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e) =>
        _terminalLayout.BeginDrag();

    /// <summary>
    /// The height is where the pointer is in the window, not an offset from
    /// the moving divider, so the drag tracks the pointer without jitter.
    /// </summary>
    private void OnDividerDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        var pointer = System.Windows.Input.Mouse.GetPosition(RootGrid).Y;
        var height = RootGrid.ActualHeight - pointer - TerminalDivider.ActualHeight / 2;
        _terminalLayout.Drag(height, AvailableHeight);
        ApplyTerminalLayout();
    }

    /// <summary>Released near the bottom, the panel folds into the strip and keeps its height for next time.</summary>
    private void OnDividerDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (_terminalLayout.EndDrag()) SetTerminalVisible(false);
        else ApplyTerminalLayout();
    }

    private void OnDividerDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        SetTerminalVisible(false);
        e.Handled = true;
    }

    private void ToggleFullWindow()
    {
        if (Terminal.Visibility != Visibility.Visible) SetTerminalVisible(true);
        _terminalLayout.Toggle(AvailableHeight);
        ApplyTerminalLayout();
    }

    /// <summary>
    /// Full-window mode hides the tab's page behind the terminal; otherwise the
    /// panel has its height. Closed, the row is as tall as the strip.
    /// </summary>
    private void ApplyTerminalLayout()
    {
        var visible = Terminal.Visibility == Visibility.Visible;
        var full = visible && _terminalLayout.FullWindow;
        ContentFrame.Visibility = full ? Visibility.Hidden : Visibility.Visible;
        ContentRow.Height = full ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        TerminalRow.Height = !visible ? GridLength.Auto
            : full ? new GridLength(1, GridUnitType.Star)
            : new GridLength(_terminalLayout.DisplayHeight);
    }

    private void OnTerminalToggleClicked(object sender, RoutedEventArgs e) => ToggleTerminal();

    public void ToggleTerminal() => SetTerminalVisible(Terminal.Visibility != Visibility.Visible);

    /// <summary>
    /// Show or hide the panel. Showing it never opens a session: sessions
    /// open at launch, from Ctrl+T and from the New menu. The panel lives
    /// outside the page frame, so tab changes leave it and its sessions alone.
    /// Closing it hands the keyboard back to whatever had it when it opened,
    /// so typing never goes to a hidden pane.
    /// </summary>
    public void SetTerminalVisible(bool visible, bool takeFocus = true)
    {
        var wasVisible = Terminal.Visibility == Visibility.Visible;
        if (visible && !wasVisible) RememberContentFocus();
        var terminalHadFocus = Terminal.IsKeyboardFocusWithin;

        Terminal.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TerminalDivider.Visibility = Terminal.Visibility;
        TerminalStrip.Visibility = visible || TicketsOnly ? Visibility.Collapsed : Visibility.Visible;
        TerminalToggleButton.IsChecked = visible;
        var label = visible ? "Hide Agent Terminal" : "Show Agent Terminal";
        TerminalToggleButton.ToolTip = $"{label} (Ctrl+`)";
        System.Windows.Automation.AutomationProperties.SetName(TerminalToggleButton, label);
        ApplyTerminalLayout();
        Terminal.OnVisibilityChanged();
        if (!visible)
        {
            if (wasVisible && terminalHadFocus) ReturnFocusToContent();
            return;
        }
        if (takeFocus) Terminal.FocusActive();
    }

    /// <summary>What had the keyboard when the panel opened, to hand it back on close.</summary>
    private WeakReference<IInputElement>? _contentFocus;

    private void RememberContentFocus()
    {
        var focused = System.Windows.Input.Keyboard.FocusedElement;
        if (focused is DependencyObject d && IsInsideTerminal(d)) return;
        _contentFocus = focused == null ? null : new WeakReference<IInputElement>(focused);
    }

    private void ReturnFocusToContent()
    {
        if (_contentFocus?.TryGetTarget(out var previous) == true
            && previous is UIElement { IsVisible: true } element
            && GetWindow(element) == this
            && element.Focus())
            return;
        ContentFrame.Focus();
    }

    private bool IsInsideTerminal(DependencyObject element)
    {
        for (var node = element; node != null;
             node = node is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (ReferenceEquals(node, Terminal)) return true;
        return false;
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (ContentFrame == null) return; // Not yet initialized
        if (sender is RadioButton radio && radio.Tag is string tag)
        {
            RecordTabVisit(tag);
            NavigateToPage(tag);
            UpdateGraphsButton();
        }
    }

    /// <summary>Tab-bar order: Ctrl+1 is the first, Ctrl+8 the last.</summary>
    internal static readonly string[] TabOrder =
        { "Development", "Projects", "Devices", "Reporting", "Manage", "Inventory", "Identity", "Tickets" };

    internal static string? TabShortcut(System.Windows.Input.Key key)
    {
        var index = key switch
        {
            >= System.Windows.Input.Key.D1 and <= System.Windows.Input.Key.D8 => key - System.Windows.Input.Key.D1,
            >= System.Windows.Input.Key.NumPad1 and <= System.Windows.Input.Key.NumPad8 => key - System.Windows.Input.Key.NumPad1,
            _ => -1,
        };
        return index >= 0 ? TabOrder[index] : null;
    }

    /// <summary>The tag of the tab showing now.</summary>
    public string CurrentTab =>
        TabBar.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? LaunchTab;

    /// <summary>Navigate to a tab by tag name — deep links and Ctrl+1–8.</summary>
    public void NavigateToTab(string tag)
    {
        foreach (var child in TabBar.Children)
        {
            if (child is RadioButton radio && radio.Tag?.ToString() == tag)
            {
                radio.IsChecked = true; // fires OnTabChecked -> NavigateToPage
                return;
            }
        }
    }

    /// <summary>Show the Reporting tab on a <c>reportmate://</c> page.</summary>
    public void OpenReportingLink(string url)
    {
        NavigateToTab("Reporting");
        if (GetOrCreatePage("Reporting") is FleetMate.GUI.Views.Reporting.ReportingPage page) page.OpenLink(url);
    }

    private Page GetOrCreatePage(string tag) =>
        _pageCache.TryGetValue(tag, out var cached) ? cached : (_pageCache[tag] = CreatePage(tag));

    private static Page CreatePage(string tag) => tag switch
    {
        "Devices" => new IntunePage(),
        "Manage" => new ManagePage(),
        "Inventory" => new AssetsPage(),
        "Tickets" => new TicketsPage(),
        "Projects" => new BoardsPage(),
        "Development" => new FleetMate.GUI.Views.Development.DevelopmentPage(),
        "Identity" => new IdentityPage(),
        "Reporting" => new FleetMate.GUI.Views.Reporting.ReportingPage(),
        _ => new FleetMate.GUI.Views.Development.DevelopmentPage()
    };

    private void NavigateToPage(string tag)
    {
        FleetMate.GUI.Views.Terminal.ContextPublisher.Tab(tag);
        var page = GetOrCreatePage(tag);
        ContentFrame.Navigate(page);
        AttachSearchScope(page, tag);
    }

    public void ResetPageCache()
    {
        _pageCache.Clear();
    }

    // ── Authentication popover ────────────────────────────────────
    // Lives in the window chrome so it is reachable from any tab, the same
    // way the macOS toolbar exposes it.

    private void OnAuthClicked(object sender, RoutedEventArgs e)
    {
        PopulateAuthPopup();
        AuthPopup.IsOpen = !AuthPopup.IsOpen;
    }

    private void PopulateAuthPopup()
    {
        if (Application.Current is not App app) return;
        AuthSystemsPanel.Children.Clear();

        foreach (var category in Enum.GetValues<AuthCategory>())
        {
            var systems = app.AuthManager.SystemsForCategory(category);
            if (systems.Count == 0) continue;

            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
            header.Children.Add(new ModernWpf.Controls.FontIcon { Glyph = CategoryGlyph(category), FontSize = 14, Margin = new Thickness(0, 0, 6, 0) });
            header.Children.Add(new TextBlock { Text = category.DisplayName(), FontWeight = FontWeights.SemiBold, FontSize = 14 });
            AuthSystemsPanel.Children.Add(header);

            foreach (var system in systems)
                AuthSystemsPanel.Children.Add(BuildAuthSystemCard(app, system));
        }

        var refreshBtn = new Button { Content = "Refresh All", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        refreshBtn.Click += async (_, _) =>
        {
            await app.AuthManager.ProbeAllAsync(app.GraphService, app.TdxService, app.SnipeService, app.DevOpsService);
            PopulateAuthPopup();
        };
        AuthSystemsPanel.Children.Add(refreshBtn);
    }

    private Border BuildAuthSystemCard(App app, AuthSystemStatus system)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("SubtleFillBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 4, 0, 4),
            BorderBrush = new SolidColorBrush(StateColor(system.State)),
            BorderThickness = new Thickness(1)
        };

        var content = new DockPanel();

        var icon = new ModernWpf.Controls.FontIcon
        {
            Glyph = system.SystemId.Icon(),
            FontSize = 20,
            Foreground = new SolidColorBrush(StateColor(system.State)),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        DockPanel.SetDock(icon, Dock.Left);
        content.Children.Add(icon);

        var actions = BuildAuthActions(app, system);
        if (actions != null)
        {
            DockPanel.SetDock(actions, Dock.Right);
            content.Children.Add(actions);
        }

        var main = new StackPanel();

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        headerRow.Children.Add(new TextBlock { Text = system.SystemId.DisplayName(), FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 8, 0) });

        var badge = new Border
        {
            Background = new SolidColorBrush(StateColor(system.State)) { Opacity = 0.15 },
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2)
        };
        var badgeContent = new StackPanel { Orientation = Orientation.Horizontal };
        badgeContent.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(StateColor(system.State)), Margin = new Thickness(0, 0, 4, 0) });
        badgeContent.Children.Add(new TextBlock { Text = system.State.StatusLabel, FontSize = 11, Foreground = new SolidColorBrush(StateColor(system.State)) });
        badge.Child = badgeContent;
        headerRow.Children.Add(badge);
        main.Children.Add(headerRow);

        if (system.User != null)
            main.Children.Add(AuthDetailRow("Signed in as", system.User));
        if (system.LastChecked.HasValue)
            main.Children.Add(AuthDetailRow("Last verified", FormatRelative(system.LastChecked.Value)));

        content.Children.Add(main);
        card.Child = content;
        return card;
    }

    private FrameworkElement? BuildAuthActions(App app, AuthSystemStatus system)
    {
        switch (system.SystemId)
        {
            case AuthSystemId.Tdx:
                var tdxBtn = new Button { FontSize = 11, Padding = new Thickness(8, 4, 8, 4), VerticalAlignment = VerticalAlignment.Top };
                if (system.State.IsHealthy)
                {
                    tdxBtn.Content = "Sign Out";
                    tdxBtn.Click += (_, _) => { app.SignOutTdxSso(); PopulateAuthPopup(); };
                }
                else
                {
                    tdxBtn.Content = "Sign In";
                    tdxBtn.Click += async (_, _) => { await app.RetryTdxSsoAsync(); PopulateAuthPopup(); };
                }
                return tdxBtn;

            case AuthSystemId.DevOps:
                var devOpsBtn = new Button { FontSize = 11, Padding = new Thickness(8, 4, 8, 4), VerticalAlignment = VerticalAlignment.Top };
                if (system.State.IsHealthy)
                {
                    devOpsBtn.Content = "Sign Out";
                    devOpsBtn.Click += (_, _) => { app.SignOutDevOpsSso(); PopulateAuthPopup(); };
                }
                else
                {
                    devOpsBtn.Content = "Sign In";
                    devOpsBtn.Click += async (_, _) => { await app.AttemptSilentDevOpsSsoAsync(); PopulateAuthPopup(); };
                }
                return devOpsBtn;

            default:
                return null;
        }
    }

    private static TextBlock AuthDetailRow(string label, string value)
    {
        var tb = new TextBlock { FontSize = 11, Margin = new Thickness(0, 1, 0, 1) };
        // The theme's secondary text, not a fixed grey that fades on the light card.
        var caption = new System.Windows.Documents.Run(label + "  ");
        caption.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        tb.Inlines.Add(caption);
        tb.Inlines.Add(new System.Windows.Documents.Run(value) { FontFamily = new FontFamily("Consolas") });
        return tb;
    }

    private static Color StateColor(AuthTokenState state) => state.Kind switch
    {
        AuthStateKind.Valid => Colors.Green,
        AuthStateKind.Configured => Colors.Goldenrod,
        AuthStateKind.Authenticating => Colors.DodgerBlue,
        AuthStateKind.Expired => Colors.Orange,
        AuthStateKind.Failed => Colors.Red,
        AuthStateKind.ServicePrincipal => Colors.Orange,
        _ => Colors.Gray
    };

    private static string CategoryGlyph(AuthCategory cat) => cat switch
    {
        AuthCategory.Devices => "",
        AuthCategory.Inventory => "",
        AuthCategory.Tickets => "",
        AuthCategory.Projects => "",
        AuthCategory.Identity => "",
        _ => ""
    };

    private static string FormatRelative(DateTime dt)
    {
        var span = DateTime.Now - dt;
        if (span.TotalMinutes < 2)  return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24)   return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7)     return $"{(int)span.TotalDays}d ago";
        return dt.ToString("MMM d");
    }
}
