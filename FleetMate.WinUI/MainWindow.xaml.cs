using System.Runtime.InteropServices;
using FleetMate.Core.Config;
using FleetMate.Core.Links;
using FleetMate.Core.Models;
using FleetMate.Core.Services.Search;
using FleetMate.WinUI.Pages;
using FleetMate.WinUI.Shell;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Serilog;
using Windows.Graphics;
using Windows.System;

namespace FleetMate.WinUI;

/// <summary>
/// The one window: toolbar, banners, and the tab bar over a frame. Tabs come
/// from <see cref="AppTabs"/> (FleetMate for Mac's order and names, with the
/// operator's hidden modules left out), and each opens the page
/// <see cref="ShellPages"/> has for it. TicketsMate shows the Tickets tab alone
/// with no tab bar.
/// </summary>
public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>The text in the toolbar search field. The current page filters on it.</summary>
    public static event EventHandler<string>? SearchChanged;

    /// <summary>
    /// Enter in the toolbar search field with no suggestion chosen: a page that
    /// looks things up rather than filtering (Identity's Users) searches then.
    /// </summary>
    public static event EventHandler<string>? SearchSubmitted;

    /// <summary>List or Board, from the Tickets tab's selector.</summary>
    public static event EventHandler<string>? ViewModeChanged;

    /// <summary>Board, as in FleetMate.GUI and on the Mac, until the selector changes it.</summary>
    public static string ViewMode { get; private set; } = "board";

    private static event Action<string>? ViewModeRequested;

    /// <summary>Select List or Board from a page: Assigned to Me switches to the list, as on Mac.</summary>
    public static void RequestViewMode(string mode) => ViewModeRequested?.Invoke(mode);

    private readonly AppEdition _edition = AppEdition.Current;
    private IReadOnlyList<string> _tabs = Array.Empty<string>();
    private readonly List<KeyboardAccelerator> _tabAccelerators = new();
    private bool _syncingSelection;
    private CancellationTokenSource? _search;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        Title = AppTitleBar.Title = _edition.Name;
        var iconFile = _edition.IsTicketsOnly ? "TicketsMate.ico" : "FleetMate.ico";
        AppTitleBar.IconSource = new ImageIconSource { ImageSource = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", iconFile))) };
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", iconFile));

        ViewModeRequested += mode =>
        {
            if (ViewModeBar.Items.FirstOrDefault(i => Equals(i.Tag, mode)) is { } item) ViewModeBar.SelectedItem = item;
        };

        BuildTabs();
        AddSettingsShortcut();
        if (_edition.IsTicketsOnly) NavView.IsPaneVisible = false;
        // Switching a module in Settings rebuilds the tab bar at once.
        Pages.Settings.SettingsViewModel.ModulesChanged += () => DispatcherQueue.TryEnqueue(BuildTabs);
        Pages.Settings.SettingsViewModel.ThemeChanged += ApplyTheme;

        SizeAndCenter(1600, 1000);

        var services = App.Services;
        DemoBadge.Visibility = services.DemoMode ? Visibility.Visible : Visibility.Collapsed;
        services.TdxSession.StatusChanged += UpdateSignInBanner;
        services.TdxSession.SignInChanged += UpdateSignInBanner;
        services.AppErrorChanged += UpdateAppError;
        Shell.Widgets.WidgetStrip.Changed += _ => UpdateGraphsButton();
        UpdateSignInBanner();
        UpdateAppError();
        LinkBanner.IsOpen = false;

        if (AppTabs.StartTab(_edition, _tabs) is { } start) NavigateToTab(start);
    }

    // MARK: - Tabs

    /// <summary>
    /// The tabs this edition shows, leaving out the modules the operator
    /// switched off. Demo mode reads no preferences and shows every tab.
    /// </summary>
    private void BuildTabs()
    {
        var hidden = App.Services.DemoMode ? new HashSet<string>() : UserPreferences.HiddenModules;
        var tabs = AppTabs.Visible(_edition, hidden);
        if (tabs.SequenceEqual(_tabs)) return;
        _tabs = tabs;
        NavView.MenuItems.Clear();
        foreach (var old in _tabAccelerators) RootGrid.KeyboardAccelerators.Remove(old);
        _tabAccelerators.Clear();

        foreach (var (tag, index) in _tabs.Select((t, i) => (t, i)))
        {
            var item = new NavigationViewItem
            {
                Content = tag,
                Tag = tag,
                Icon = new FontIcon { Glyph = AppTabs.Glyph(tag) },
            };
            AutomationProperties.SetName(item, tag);
            AutomationProperties.SetAutomationId(item, $"Tab{tag}");
            if (index < 9)
            {
                var number = index + 1;
                ToolTipService.SetToolTip(item, $"{tag} (Ctrl+{number})");
                var accelerator = new KeyboardAccelerator { Key = VirtualKey.Number1 + index, Modifiers = VirtualKeyModifiers.Control };
                accelerator.Invoked += (_, e) =>
                {
                    if (AppTabs.ForShortcut(_tabs, number) is { } target) NavigateToTab(target);
                    e.Handled = true;
                };
                RootGrid.KeyboardAccelerators.Add(accelerator);
                _tabAccelerators.Add(accelerator);
            }
            NavView.MenuItems.Add(item);
        }

        // Keep the selection on the tab showing; a tab just switched off falls back to the start tab.
        if (CurrentTab is { } current && current != ShellPages.Settings && !_tabs.Contains(current)
            && AppTabs.StartTab(_edition, _tabs) is { } start)
            NavigateToTab(start);
        else if (CurrentTab is { } shown)
            NavView.SelectedItem = shown == ShellPages.Settings
                ? NavView.SettingsItem
                : NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => Equals(i.Tag, shown));
    }

    private void AddSettingsShortcut()
    {
        var settings = new KeyboardAccelerator { Key = (VirtualKey)188, Modifiers = VirtualKeyModifiers.Control };
        settings.Invoked += (_, e) =>
        {
            ShowPanel(ShellPages.Settings);
            e.Handled = true;
        };
        RootGrid.KeyboardAccelerators.Add(settings);
    }

    /// <summary>Apply a theme chosen in Settings without restarting.</summary>
    private void ApplyTheme(string theme) =>
        RootGrid.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

    /// <summary>Show <paramref name="tag"/>'s tab; a tab this edition or the operator left out is ignored.</summary>
    public void NavigateToTab(string tag)
    {
        if (!_tabs.Contains(tag))
        {
            Log.Information("Tab {Tab} is not showing; staying on the current tab", tag);
            return;
        }
        NavigateTo(ShellPages.For(tag), tag);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingSelection) return;
        if (args.IsSettingsSelected) ShowPanel(ShellPages.Settings);
        else if (args.SelectedItem is NavigationViewItem { Tag: string tag }) NavigateToTab(tag);
    }

    private void NavigateTo(Type page, string section)
    {
        if (NavFrame.CurrentSourcePageType == page && Equals(NavFrame.Tag, section)) return;
        NavFrame.Navigate(page, section);
    }

    private string? CurrentTab => NavFrame.Tag as string;

    private void NavFrame_Navigated(object sender, NavigationEventArgs e)
    {
        NavFrame.Tag = e.Parameter;
        BackButton.IsEnabled = NavFrame.CanGoBack;
        ForwardButton.IsEnabled = NavFrame.CanGoForward;

        // Keep the tab bar in step when Back or Forward changes the page.
        _syncingSelection = true;
        NavView.SelectedItem = e.Parameter is ShellPages.Settings
            ? NavView.SettingsItem
            : NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => Equals(i.Tag, e.Parameter));
        _syncingSelection = false;

        var isTickets = Equals(e.Parameter, "Tickets");
        TicketsTools.Visibility = isTickets ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.PlaceholderText = e.Parameter is string tab && tab != ShellPages.Settings
            ? $"Search {tab}  (Ctrl+F)"
            : "Search  (Ctrl+F)";
        UpdateSignInBanner();
        UpdateGraphsButton();
    }

    // MARK: - Graphs

    private void UpdateGraphsButton()
    {
        var tab = CurrentTab;
        var has = Shell.Widgets.WidgetStrip.HasWidgets(tab);
        GraphsButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (has) GraphsButton.IsChecked = Shell.Widgets.WidgetStrip.IsShown(tab!);
    }

    private void GraphsButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTab is { } tab) Shell.Widgets.WidgetStrip.SetShown(tab, GraphsButton.IsChecked == true);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (NavFrame.CanGoBack) NavFrame.GoBack();
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (NavFrame.CanGoForward) NavFrame.GoForward();
    }

    // MARK: - Search

    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private void RefreshAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Pages that load live data refresh from SignInChanged; F5 retries sign-in
        // when it is the thing in the way.
        if (App.Services.TdxSession.ShowsSignIn) _ = RetrySignInAsync();
        args.Handled = true;
    }

    /// <summary>
    /// The one search field: it filters the tab showing (through
    /// <see cref="SearchChanged"/>) and lists matches from everything loaded
    /// (<see cref="GlobalSearch"/>), each opening through its link.
    /// </summary>
    private async void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        SearchChanged?.Invoke(this, sender.Text);
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        var text = sender.Text;
        if (!GlobalSearch.ShouldSearch(text))
        {
            sender.ItemsSource = null;
            return;
        }
        var sources = App.Services.BuildSearchSources();
        try
        {
            var groups = await Task.Run(() => GlobalSearch.Search(text, sources), cts.Token);
            if (cts.IsCancellationRequested) return;
            sender.ItemsSource = groups
                .SelectMany(g => g.Hits.Select(h => new SearchSuggestion(g.Title, h)))
                .ToList();
        }
        catch (OperationCanceledException) { }
    }

    private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is SearchSuggestion s) OpenLink(s.Hit.Link);
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is SearchSuggestion s) OpenLink(s.Hit.Link);
        else SearchSubmitted?.Invoke(this, args.QueryText);
    }

    /// <summary>One row in the search field's list.</summary>
    private sealed record SearchSuggestion(string Category, SearchHit Hit)
    {
        public override string ToString() =>
            string.Join(" · ", new[] { Hit.Title, Category, Hit.Subtitle }.Where(t => !string.IsNullOrWhiteSpace(t)));
    }

    private void ViewModeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ViewMode = sender.SelectedItem?.Tag as string ?? "board";
        ViewModeChanged?.Invoke(this, ViewMode);
    }

    // MARK: - Links

    /// <summary>
    /// Bring the window forward and open <paramref name="link"/> in the tab
    /// that shows it (<see cref="LinkRoutes"/>). An empty message, from a
    /// second launch with no link, only brings the window forward.
    /// </summary>
    public void OpenLink(string link)
    {
        BringToFront();
        if (string.IsNullOrWhiteSpace(link)) return;
        if (!link.Trim().Trim('"').StartsWith(_edition.LinkScheme + ":", StringComparison.OrdinalIgnoreCase)) return;

        if (FleetMateLink.TryParse(link, out var error) is not { } parsed)
        {
            Log.Information("Unopenable link {Link}: {Reason}", link, error?.Message);
            LinkBanner.Message = error?.Message ?? $"{_edition.Name} can't open {link}.";
            LinkBanner.IsOpen = true;
            return;
        }

        Log.Information("Opening {Link}", parsed.ToLink());
        LinkBanner.IsOpen = false;
        if (parsed is FleetMateLink.Ticket ticket) App.Services.PendingTicketId = ticket.Id;
        if (parsed is FleetMateLink.Asset asset) App.Services.RequestAssetLink(asset.Id);
        var tab = LinkRoutes.TabFor(parsed);
        if (tab == "Development") App.Services.RequestDevelopmentLink(parsed);
        NavigateToTab(tab);
    }

    /// <summary>Say something about a link in the banner under the toolbar: a page's link that found nothing.</summary>
    public void ShowLinkBanner(string message)
    {
        LinkBanner.Message = message;
        LinkBanner.IsOpen = true;
    }

    private void BringToFront()
    {
        var plan = WindowActivation.Plan(AppWindow.IsVisible,
            AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized });
        if (plan.Show) AppWindow.Show();
        if (plan.Restore && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        Activate();
        SetForegroundWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id));
    }

    // MARK: - Banners and toolbar popovers

    private void UpdateSignInBanner()
    {
        var session = App.Services.TdxSession;
        var onTickets = _edition.IsTicketsOnly || CurrentTab == "Tickets";
        SignInBanner.IsOpen = onTickets && session.ShowsSignIn;
        SignInBanner.Message = session.SignInMessage;
        SignInButton.IsEnabled = !session.IsSigningIn;
        AuthGlyph.Glyph = session.IsEnabled && !session.IsSignedIn && !session.IsSigningIn ? "" : "";
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e) => await RetrySignInAsync();

    private async Task RetrySignInAsync()
    {
        SignInButton.IsEnabled = false;
        await App.Services.TdxSession.RetryAsync();
        UpdateSignInBanner();
    }

    private void UpdateAppError()
    {
        var error = App.Services.AppError;
        AppErrorBadge.Visibility = error != null ? Visibility.Visible : Visibility.Collapsed;
        AppErrorInfo.Message = error ?? "";
        AppErrorInfo.IsOpen = error != null;
        NoActivityText.Visibility = error == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RecentActivityFlyout_Opening(object sender, object e) => UpdateAppError();

    private void AppErrorInfo_CloseButtonClick(InfoBar sender, object args) => App.Services.ReportAppError(null);

    private void AuthFlyout_Opening(object sender, object e)
    {
        var systems = App.Services.Auth.ConfiguredSystems;
        AuthList.ItemsSource = App.Services.DemoMode
            ? new[] { "Demo mode: fictional data, nothing signed in." }
            : systems.Count == 0
            ? new[] { "Nothing is configured." }
            : systems.Select(s => $"{s.SystemId.DisplayName()}: {s.State.StatusLabel}"
                + (s.State.User is { } user ? $" ({user})" : "")).ToList();
    }

    // MARK: - QA capture

    /// <summary>The tabs showing, in order, for <see cref="CaptureHarness"/>.</summary>
    internal IReadOnlyList<string> Tabs => _tabs;

    /// <summary>The XAML root a capture renders and themes.</summary>
    internal FrameworkElement CaptureRoot => RootGrid;

    /// <summary>The page the frame shows, for the capture's <see cref="ICaptureViews"/>.</summary>
    internal object? CurrentPage => NavFrame.Content;

    /// <summary>Show a tab, or Settings, without touching history rules.</summary>
    internal void ShowPanel(string panel)
    {
        if (panel == ShellPages.Settings)
        {
            NavigateTo(typeof(Pages.Settings.SettingsPage), ShellPages.Settings);
            // Opening Settings again starts at General.
            (NavFrame.Content as Pages.Settings.SettingsPage)?.ShowSection("General");
        }
        else NavigateToTab(panel);
    }

    internal void ResizeForCapture(int width, int height) => SizeAndCenter(width, height);

    // MARK: - Window size

    /// <summary>Size in DIPs, scaled to this monitor, centred in its work area.</summary>
    private void SizeAndCenter(int width, int height)
    {
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(width * scale), (int)(height * scale)));
        if (DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary) is { } area)
        {
            var work = area.WorkArea;
            AppWindow.Move(new PointInt32(
                work.X + Math.Max(0, (work.Width - AppWindow.Size.Width) / 2),
                work.Y + Math.Max(0, (work.Height - AppWindow.Size.Height) / 2)));
        }
    }
}
