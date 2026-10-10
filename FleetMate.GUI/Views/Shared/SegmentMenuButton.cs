using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// Folds a module's segment switch into one pill naming the open segment
/// ("Pulls ⌄") when its column is too narrow to show every segment, as the
/// macOS app does in a narrow window. Place it beside the panel of segment
/// radio buttons, in the same single-cell host:
/// <code>
/// &lt;Grid&gt;
///     &lt;StackPanel x:Name="Segments" Orientation="Horizontal"&gt;…radio buttons…&lt;/StackPanel&gt;
///     &lt;shared:SegmentMenuButton Segments="{Binding ElementName=Segments}" /&gt;
/// &lt;/Grid&gt;
/// </code>
/// The segments stay the source of truth: the pill lists the ones showing and
/// choosing one checks it, so a page's own Checked handlers run unchanged. A
/// switch of two segments never folds.
/// </summary>
public sealed class SegmentMenuButton : Button
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(Panel), typeof(SegmentMenuButton),
        new PropertyMetadata(null, (d, _) => ((SegmentMenuButton)d).Attach()));

    /// <summary>The panel of segment radio buttons this pill stands in for.</summary>
    public Panel? Segments
    {
        get => (Panel?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center };
    private FrameworkElement? _host;
    private bool _attached;

    public SegmentMenuButton()
    {
        Visibility = Visibility.Collapsed;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        Padding = new Thickness(11, 5, 9, 5);
        MinWidth = 0;
        FontSize = 12;
        FontWeight = FontWeights.SemiBold;
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                _label,
                new ModernWpf.Controls.FontIcon
                {
                    Glyph = "", FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"),
                    FontSize = 10, Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        AutomationProperties.SetHelpText(this, "Shows the other choices");
        Loaded += (_, _) => { Attach(); Update(); };
        Click += (_, _) => OpenMenu();
    }

    private IEnumerable<RadioButton> Options =>
        Segments?.Children.OfType<RadioButton>() ?? Enumerable.Empty<RadioButton>();

    private void Attach()
    {
        if (_attached || Segments == null || !IsLoaded) return;
        _attached = true;

        _host = Parent as FrameworkElement;
        if (_host != null) _host.SizeChanged += (_, e) => { if (e.WidthChanged) Update(); };

        var visibility = DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(UIElement));
        foreach (var option in Options)
        {
            option.Checked += (_, _) => Update();
            // A segment that comes and goes (Development's Inbox) changes the fit.
            visibility.AddValueChanged(option, (_, _) => Update());
        }
    }

    /// <summary>Whether a switch of <paramref name="count"/> segments needing <paramref name="needed"/> folds in <paramref name="available"/>.</summary>
    internal static bool ShouldFold(int count, double needed, double available) =>
        count > 2 && available > 0 && needed > available;

    private void Update()
    {
        if (Segments == null || _host == null) return;
        var showing = Options.Where(o => o.Visibility == Visibility.Visible).ToList();

        // Measure each segment on its own, so the width is known while the
        // panel is folded away.
        var needed = 0.0;
        foreach (var option in showing)
        {
            option.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            needed += option.DesiredSize.Width;
        }

        var fold = ShouldFold(showing.Count, needed, _host.ActualWidth);
        Segments.Visibility = fold ? Visibility.Collapsed : Visibility.Visible;
        Visibility = fold ? Visibility.Visible : Visibility.Collapsed;

        var current = showing.FirstOrDefault(o => o.IsChecked == true) ?? showing.FirstOrDefault();
        var name = current == null ? "" : NameOf(current);
        _label.Text = name;
        AutomationProperties.SetName(this, name);
        ToolTip = name;
    }

    private static string NameOf(RadioButton option) =>
        option.Content as string is { Length: > 0 } text ? text : AutomationProperties.GetName(option);

    private void OpenMenu()
    {
        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.Bottom };
        foreach (var option in Options.Where(o => o.Visibility == Visibility.Visible))
        {
            var item = new MenuItem { Header = NameOf(option), IsCheckable = true, IsChecked = option.IsChecked == true };
            var target = option;
            item.Click += (_, _) => target.IsChecked = true;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
