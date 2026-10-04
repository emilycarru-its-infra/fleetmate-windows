using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Serilog;

namespace FleetMate.GUI.Views.Shared.Widgets;

/// <summary>A tab page that can apply a widget chart click to its own filters.</summary>
public interface IWidgetFilterHost
{
    /// <summary>Apply <paramref name="value"/> to the page's <paramref name="category"/> filter, if it has one.</summary>
    void ApplyWidgetFilter(string category, string value);
}

/// <summary>
/// The collapsible "Widgets" section at the top of a tab: a header with a
/// chevron, then that tab's cards in full-width rows. Redraws when the app's
/// data for the tab changes. The collapsed state is kept per tab in
/// HKCU\SOFTWARE\FleetMate under <c>widgets.collapsed.&lt;Tab&gt;</c>; the
/// default is expanded.
/// </summary>
public sealed class WidgetsSection : StackPanel
{
    private const string RegistryPath = @"SOFTWARE\FleetMate";

    private readonly WidgetFlowPanel _flow = new() { Margin = new Thickness(0, 8, 0, 4) };
    private readonly TextBlock _chevron = new()
    {
        FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 6, 0),
    };
    private bool _collapsed;
    private bool _dirty = true;

    public static readonly DependencyProperty TabProperty = DependencyProperty.Register(
        nameof(Tab), typeof(string), typeof(WidgetsSection), new PropertyMetadata(""));

    /// <summary>The tab's name as the tab bar tags it: Development, Projects, Devices, Inventory, Tickets.</summary>
    public string Tab
    {
        get => (string)GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    public static string PersistenceKey(string tab) => $"widgets.collapsed.{tab}";

    public WidgetsSection()
    {
        Margin = new Thickness(0, 0, 0, 8);

        var header = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        header.Children.Add(_chevron);
        header.Children.Add(new TextBlock { Text = "Widgets", FontSize = 13, FontWeight = FontWeights.SemiBold });
        header.MouseLeftButtonUp += (_, _) => SetCollapsed(!_collapsed);

        Children.Add(header);
        Children.Add(_flow);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => { if (IsVisible && _dirty) Rebuild(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!WidgetCatalog.HasWidgets(Tab))
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        _collapsed = ReadCollapsed(Tab);
        ApplyCollapsed();

        if (Application.Current is App app)
        {
            app.CacheChanged -= OnCacheChanged;
            app.CacheChanged += OnCacheChanged;
            app.Inbox.Changed -= OnInboxChanged;
            app.Inbox.Changed += OnInboxChanged;
        }

        Rebuild();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (Application.Current is not App app) return;
        app.CacheChanged -= OnCacheChanged;
        app.Inbox.Changed -= OnInboxChanged;
    }

    private void OnInboxChanged(object? sender, EventArgs e) => OnCacheChanged("Inbox");

    /// <summary>Redraw now if showing, otherwise on the next show — no work for a hidden tab.</summary>
    private void OnCacheChanged(string key)
    {
        if (!WidgetCatalog.DependsOn(Tab, key)) return;
        _dirty = true;
        if (IsVisible) Dispatcher.BeginInvoke(Rebuild);
    }

    private void Rebuild()
    {
        if (Application.Current is not App app || _collapsed) return;
        _dirty = false;

        try
        {
            _flow.Children.Clear();
            foreach (var card in WidgetCatalog.Build(Tab, app, ApplyFilter)) _flow.Children.Add(card);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[widgets] Failed to build the {Tab} widgets", Tab);
        }
    }

    private void ApplyFilter(string category, string value)
    {
        var host = FindHost(this);
        if (host != null) host.ApplyWidgetFilter(category, value);
        else Log.Debug("[widgets] {Tab} has no filter for {Category}={Value}", Tab, category, value);
    }

    private static IWidgetFilterHost? FindHost(DependencyObject start)
    {
        for (var node = start; node != null; node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
        {
            if (node is IWidgetFilterHost host) return host;
        }
        return null;
    }

    private void SetCollapsed(bool collapsed)
    {
        _collapsed = collapsed;
        ApplyCollapsed();
        WriteCollapsed(Tab, collapsed);
        if (!collapsed && (_dirty || _flow.Children.Count == 0)) Rebuild();
    }

    private void ApplyCollapsed()
    {
        _flow.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        _chevron.Text = _collapsed ? "" : "";
    }

    private static bool ReadCollapsed(string tab)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            return key?.GetValue(PersistenceKey(tab)) is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteCollapsed(string tab, bool collapsed)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue(PersistenceKey(tab), collapsed ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[widgets] Could not save the collapsed state for {Tab}", tab);
        }
    }
}
