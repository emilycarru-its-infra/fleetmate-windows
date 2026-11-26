using FleetMate.Core.Shared;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Serilog;

namespace FleetMate.GUI.Views.Shared.Widgets;

/// <summary>A tab page that can apply a widget chart click to its own filters.</summary>
public interface IWidgetFilterHost
{
    /// <summary>Apply <paramref name="value"/> to the page's <paramref name="category"/> filter, if it has one.</summary>
    void ApplyWidgetFilter(string category, string value);
}

/// <summary>
/// The widget strip at the top of a tab: no header of its own, shown or
/// hidden by the toolbar Graphs button. Hidden, the tab is exactly its
/// original layout; shown, the cards slide down and fade in. Redraws when the
/// app's data for the tab changes.
/// </summary>
public sealed class WidgetsSection : StackPanel
{
    private static readonly Duration RevealDuration = new(TimeSpan.FromMilliseconds(220));

    private readonly WidgetFlowPanel _flow = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Focusable = false,
    };

    /// <summary>
    /// The most of the page the strip may take; past it the strip scrolls
    /// inside itself, so the page's own content keeps the screen.
    /// </summary>
    public double MaxStripHeight
    {
        get => _scroll.MaxHeight;
        set => _scroll.MaxHeight = value;
    }
    private readonly TranslateTransform _slide = new();
    private bool _dirty = true;

    public static readonly DependencyProperty TabProperty = DependencyProperty.Register(
        nameof(Tab), typeof(string), typeof(WidgetsSection), new PropertyMetadata(""));

    /// <summary>The tab's name as the tab bar tags it: Development, Projects, Devices, Inventory, Tickets.</summary>
    public string Tab
    {
        get => (string)GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    public static string PersistenceKey(string tab) => WidgetVisibility.PersistenceKey(tab);

    /// <summary>
    /// The page's own cards, used instead of the catalog's when set — for a
    /// tab whose widgets follow its list's filters, as Tickets' do.
    /// </summary>
    public Func<List<UIElement>>? Builder { get; set; }

    /// <summary>Keep every card on one row; see <see cref="WidgetFlowPanel.SingleRow"/>.</summary>
    public bool SingleRow
    {
        get => _flow.SingleRow;
        set => _flow.SingleRow = value;
    }

    /// <summary>Give every card in a row the row's tallest height.</summary>
    public bool EqualHeights
    {
        get => _flow.EqualHeights;
        set => _flow.EqualHeights = value;
    }

    private bool _rebuildQueued;

    /// <summary>Redraw now if showing, otherwise on the next show. Calls made together redraw once.</summary>
    public void Invalidate()
    {
        _dirty = true;
        if (!IsVisible || _rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.BeginInvoke(() => { _rebuildQueued = false; Rebuild(); });
    }

    public WidgetsSection()
    {
        Margin = new Thickness(0, 0, 0, 8);
        RenderTransform = _slide;
        _scroll.Content = _flow;
        Children.Add(_scroll);

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

        Visibility = WidgetVisibility.IsShown(Tab) ? Visibility.Visible : Visibility.Collapsed;

        if (Application.Current is App app)
        {
            app.CacheChanged -= OnCacheChanged;
            app.CacheChanged += OnCacheChanged;
            app.Inbox.Changed -= OnInboxChanged;
            app.Inbox.Changed += OnInboxChanged;
        }
        WidgetVisibility.Changed -= OnVisibilityChanged;
        WidgetVisibility.Changed += OnVisibilityChanged;

        Rebuild();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        WidgetVisibility.Changed -= OnVisibilityChanged;
        if (Application.Current is not App app) return;
        app.CacheChanged -= OnCacheChanged;
        app.Inbox.Changed -= OnInboxChanged;
    }

    private void OnVisibilityChanged(string tab)
    {
        if (tab != Tab) return;

        if (!WidgetVisibility.IsShown(Tab))
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;
        if (_dirty || _flow.Children.Count == 0) Rebuild();

        // Move from the top and fade in.
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-16, 0, RevealDuration) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, RevealDuration) { EasingFunction = ease });
    }

    private void OnInboxChanged(object? sender, EventArgs e) => OnCacheChanged("Inbox");

    /// <summary>Redraw now if showing, otherwise on the next show — no work for a hidden tab.</summary>
    private void OnCacheChanged(string key)
    {
        if (!WidgetCatalog.DependsOn(Tab, key)) return;
        Invalidate();
    }

    private void Rebuild()
    {
        if (Application.Current is not App app || Visibility != Visibility.Visible) return;
        _dirty = false;

        try
        {
            _flow.Children.Clear();
            var cards = Builder?.Invoke() ?? WidgetCatalog.Build(Tab, app, ApplyFilter);
            foreach (var card in cards) _flow.Children.Add(card);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[widgets] Failed to build the {Tab} widgets", Tab);
        }
    }

    private void ApplyFilter(string category, string label)
    {
        var value = ChartSlice.FilterValue(label);
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
}
