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

    /// <summary>List or Board, from the Tickets tab's selector.</summary>
    public static event EventHandler<string>? ViewModeChanged;

    public static string ViewMode { get; private set; } = "list";

    private readonly AppEdition _edition = AppEdition.Current;
    private readonly IReadOnlyList<string> _tabs;
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

        _tabs = AppTabs.Visible(_edition, UserPreferences.HiddenModules);
        BuildTabs();
        if (_edition.IsTicketsOnly) NavView.IsPaneVisible = false;

        SizeAndCenter(1600, 1000);

        var services = App.Services;
        services.TdxSession.StatusChanged += UpdateSignInBanner;
        services.TdxSession.SignInChanged += UpdateSignInBanner;
        services.AppErrorChanged += UpdateAppError;
        UpdateSignInBanner();
        UpdateAppError();
        LinkBanner.IsOpen = false;

        if (AppTabs.StartTab(_edition, _tabs) is { } start) NavigateToTab(start);
    }

    // MARK: - Tabs

    private void BuildTabs()
    {
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
            }
            NavView.MenuItems.Add(item);
        }

        var settings = new KeyboardAccelerator { Key = (VirtualKey)188, Modifiers = VirtualKeyModifiers.Control };
        settings.Invoked += (_, e) =>
        {
            NavigateTo(typeof(PlaceholderPage), ShellPages.Settings);
            e.Handled = true;
        };
        RootGrid.KeyboardAccelerators.Add(settings);
    }

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
        if (args.IsSettingsSelected) NavigateTo(typeof(PlaceholderPage), ShellPages.Settings);
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
    }

    /// <summary>One row in the search field's list.</summary>
    private sealed record SearchSuggestion(string Category, SearchHit Hit)
    {
        public override string ToString() =>
            string.Join(" · ", new[] { Hit.Title, Category, Hit.Subtitle }.Where(t => !string.IsNullOrWhiteSpace(t)));
    }

    private void ViewModeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ViewMode = sender.SelectedItem?.Tag as string ?? "list";
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
        AuthList.ItemsSource = systems.Count == 0
            ? new[] { "Nothing is configured." }
            : systems.Select(s => $"{s.SystemId.DisplayName()}: {s.State.StatusLabel}"
                + (s.State.User is { } user ? $" ({user})" : "")).ToList();
    }

    // MARK: - QA capture

    /// <summary>The tabs showing, in order, for <see cref="CaptureHarness"/>.</summary>
    internal IReadOnlyList<string> Tabs => _tabs;

    /// <summary>The XAML root a capture renders and themes.</summary>
    internal FrameworkElement CaptureRoot => RootGrid;

    /// <summary>Show a tab, or Settings, without touching history rules.</summary>
    internal void ShowPanel(string panel)
    {
        if (panel == ShellPages.Settings) NavigateTo(typeof(PlaceholderPage), ShellPages.Settings);
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
