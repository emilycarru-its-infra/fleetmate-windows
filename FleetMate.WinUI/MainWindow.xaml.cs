using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using FleetMate.WinUI.Views;

namespace FleetMate.WinUI;

/// <summary>
/// Shell window: a top-tab NavigationView driving a Frame. Nav item Tags map to
/// page types (mirrors the Cimian MSC pattern). Tabs match the macOS FleetMate
/// order: Dashboard, Devices, Inventory, Tickets, Projects, Identity, + Settings.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.Resize(new Windows.Graphics.SizeInt32(1600, 1000));
        CenterOnScreen();

        RootGrid.Loaded += OnRootLoaded;
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnRootLoaded;

        // First run: nothing configured -> launch the setup wizard.
        var app = App.Current;
        var anyConfigured = app.GraphService != null || app.SnipeService != null
            || app.TdxService != null || app.DevOpsService != null || app.ReportMateService != null;
        if (!anyConfigured)
            await RunSetupAsync();
    }

    /// <summary>Show the onboarding wizard; on finish, reload services and refresh the shell.</summary>
    public async Task RunSetupAsync()
    {
        var dialog = new OnboardingDialog(RootGrid.XamlRoot);
        if (await dialog.ShowAsync())
        {
            App.Current.ReloadConfigAndServices();
            NavView.SelectedItem = NavView.MenuItems[0]; // re-navigate to Dashboard with fresh services
        }
    }

    private void CenterOnScreen()
    {
        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
        var x = (displayArea.WorkArea.Width - 1600) / 2;
        var y = (displayArea.WorkArea.Height - 1000) / 2;
        AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        // Select the first tab on startup.
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
            NavigateToTag(tag);
    }

    private void NavigateToTag(string tag)
    {
        Type? pageType = tag switch
        {
            "dashboard" => typeof(DashboardPage),
            "devices"   => typeof(DevicesPage),
            "inventory" => typeof(InventoryPage),
            "tickets"   => typeof(TicketsPage),
            "projects"  => typeof(ProjectsPage),
            "identity"  => typeof(IdentityPage),
            _ => null,
        };
        if (pageType != null)
            Navigate(pageType);
    }

    private void Navigate(Type pageType)
    {
        if (ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
    }
}
