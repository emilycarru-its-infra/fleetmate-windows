using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace FleetMate.GUI.Views.Shared;

/// <summary>A short-lived, selectable preview of the text actually copied.</summary>
public static class CopyFeedback
{
    private static Popup? _popup;
    private static DispatcherTimer? _timer;
    private static Window? _owner;
    private static long _generation;
    internal static string? CurrentText { get; private set; }
    internal static FrameworkElement? CurrentAnchor => _popup?.PlacementTarget as FrameworkElement;
    internal static bool IsOpen => _popup?.IsOpen == true;
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(5);

    public static FrameworkElement? Anchor(FrameworkElement? origin)
    {
        for (var item = origin; item is MenuItem;)
        {
            var parent = ItemsControl.ItemsControlFromItemContainer(item);
            if (parent is ContextMenu menu) return menu.PlacementTarget as FrameworkElement;
            item = parent as FrameworkElement;
            if (item == null) break;
        }
        if (origin is ContextMenu context) return context.PlacementTarget as FrameworkElement;
        return origin is MenuItem ? null : origin;
    }

    public static void Show(FrameworkElement? origin, string text)
    {
        Dismiss();
        var anchor = Anchor(origin) ?? Keyboard.FocusedElement as FrameworkElement
            ?? Application.Current?.MainWindow;
        if (anchor == null || !anchor.IsVisible) return;
        var generation = _generation;
        anchor.Dispatcher.BeginInvoke(() =>
        {
            if (generation == _generation && anchor.IsVisible) Open(anchor, text);
        }, DispatcherPriority.ContextIdle);
    }

    private static void Open(FrameworkElement anchor, string text)
    {
        CurrentText = text;
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "Copied", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        var preview = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 300,
            MinWidth = 260, MaxWidth = 540, FontFamily = new FontFamily("Consolas"), FontSize = 12,
        };
        System.Windows.Automation.AutomationProperties.SetName(preview, "Copied content");
        content.Children.Add(preview);
        var border = new Border { Child = content, Padding = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "SystemControlBackgroundChromeMediumLowBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "SystemControlForegroundBaseLowBrush");
        border.SetResourceReference(TextElement.ForegroundProperty, "SystemControlForegroundBaseHighBrush");
        var popup = _popup = new Popup
        {
            Child = border, PlacementTarget = anchor, Placement = anchor is Window ? PlacementMode.Center : PlacementMode.Bottom,
            VerticalOffset = 6, AllowsTransparency = true, StaysOpen = false,
        };
        _popup.Closed += (_, _) => { if (ReferenceEquals(_popup, popup)) Dismiss(); };
        _popup.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Dismiss(); } };
        _owner = Window.GetWindow(anchor);
        if (_owner != null)
        {
            _owner.LocationChanged += OnOwnerChanged;
            _owner.SizeChanged += OnOwnerChanged;
            _owner.Deactivated += OnOwnerChanged;
            _owner.Closed += OnOwnerChanged;
        }
        _timer = new DispatcherTimer { Interval = Duration };
        _timer.Tick += OnExpired;
        _popup.IsOpen = true;
        _timer.Start();
    }

    private static void OnExpired(object? sender, EventArgs e) { if (ReferenceEquals(sender, _timer)) Dismiss(); }
    private static void OnOwnerChanged(object? sender, EventArgs e) => Dismiss();

    public static void Dismiss()
    {
        _generation++;
        var popup = _popup;
        _popup = null;
        _timer?.Stop();
        if (_timer != null) _timer.Tick -= OnExpired;
        _timer = null;
        if (_owner != null)
        {
            _owner.LocationChanged -= OnOwnerChanged;
            _owner.SizeChanged -= OnOwnerChanged;
            _owner.Deactivated -= OnOwnerChanged;
            _owner.Closed -= OnOwnerChanged;
            _owner = null;
        }
        CurrentText = null;
        if (popup != null) { popup.IsOpen = false; popup.Child = null; }
    }
}
