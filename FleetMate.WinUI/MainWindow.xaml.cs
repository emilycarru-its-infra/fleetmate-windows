using System.Runtime.InteropServices;
using FleetMate.Core.Config;
using Microsoft.UI.Xaml.Media.Imaging;
using FleetMate.WinUI.Pages;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;

namespace FleetMate.WinUI;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    /// <summary>The text in the title-bar search box. The current page filters on it.</summary>
    public static event EventHandler<string>? SearchChanged;

    /// <summary>List or Board, from the title-bar selector.</summary>
    public static event EventHandler<string>? ViewModeChanged;

    public static string ViewMode { get; private set; } = "list";

    private bool _syncingSelection;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        // TicketsMate is this shell with only the Tickets section.
        var edition = AppEdition.Current;
        Title = AppTitleBar.Title = edition.Name;
        var iconFile = edition.IsTicketsOnly ? "TicketsMate.ico" : "AppIcon.ico";
        AppTitleBar.IconSource = new ImageIconSource { ImageSource = new BitmapImage(new Uri($"ms-appx:///Assets/{iconFile}")) };
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", iconFile));
        var sections = NavView.MenuItems.OfType<NavigationViewItem>().Select(i => (string)i.Tag).ToList();
        var carried = edition.Tabs(sections);
        foreach (var item in NavView.MenuItems.ToList())
        {
            if (item is NavigationViewItemSeparator || item is NavigationViewItem { Tag: string t } && !carried.Contains(t))
                NavView.MenuItems.Remove(item);
        }
        if (edition.IsTicketsOnly)
        {
            NavView.IsPaneVisible = false;
            AppTitleBar.IsPaneToggleButtonVisible = false;
        }

        // Navigation and content at 1600x1000 DIPs; scaled to physical pixels for this monitor.
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1600 * scale), (int)(1000 * scale)));
        if (DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary) is { } area)
        {
            var work = area.WorkArea;
            AppWindow.Move(new PointInt32(
                work.X + Math.Max(0, (work.Width - AppWindow.Size.Width) / 2),
                work.Y + Math.Max(0, (work.Height - AppWindow.Size.Height) / 2)));
        }

        TicketsBadge.Value = Data.SampleTickets.Tickets.Count(t => !FleetMate.Core.Models.Tickets.TicketStats.IsClosed(t));
        NavView.SelectedItem = TicketsItem;
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingSelection) return;

        if (args.IsSettingsSelected)
        {
            NavigateTo(typeof(PlaceholderPage), "Settings");
        }
        else if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            NavigateTo(tag == "Tickets" ? typeof(TicketsPage) : typeof(PlaceholderPage), tag);
        }
    }

    private void NavigateTo(Type page, string section)
    {
        if (NavFrame.CurrentSourcePageType == page && Equals(NavFrame.Tag, section)) return;
        NavFrame.Navigate(page, section);
    }

    private void NavFrame_Navigated(object sender, NavigationEventArgs e)
    {
        NavFrame.Tag = e.Parameter;
        BackButton.IsEnabled = NavFrame.CanGoBack;
        ForwardButton.IsEnabled = NavFrame.CanGoForward;

        // Keep the pane's selection in step when Back or Forward changes the page.
        _syncingSelection = true;
        if (e.Parameter is "Settings")
        {
            NavView.SelectedItem = NavView.SettingsItem;
        }
        else
        {
            NavView.SelectedItem = NavView.MenuItems
                .OfType<NavigationViewItem>()
                .FirstOrDefault(i => Equals(i.Tag, e.Parameter));
        }
        _syncingSelection = false;

        var isTickets = e.SourcePageType == typeof(TicketsPage);
        ViewModeBar.Visibility = isTickets ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.PlaceholderText = isTickets ? "Search tickets  (Ctrl+F)" : $"Search {e.Parameter}  (Ctrl+F)";
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (NavFrame.CanGoBack) NavFrame.GoBack();
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (NavFrame.CanGoForward) NavFrame.GoForward();
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        NavView.IsPaneOpen = !NavView.IsPaneOpen;

    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) =>
        SearchChanged?.Invoke(this, sender.Text);

    private void ViewModeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ViewMode = sender.SelectedItem?.Tag as string ?? "list";
        ViewModeChanged?.Invoke(this, ViewMode);
    }
}
