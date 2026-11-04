using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using FleetMate.Core.Shared;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// Switches every pull request and commit diff between side by side and inline.
/// The choice is saved in <see cref="UserPreferences.DiffLayout"/> and shared by
/// each picker, so changing it in one view changes it in the other.
/// </summary>
public sealed class DiffLayoutPicker : StackPanel
{
    private readonly ToggleButton _split;
    private readonly ToggleButton _inline;

    /// <summary>Raised after the operator picks a layout.</summary>
    public event Action<DiffLayout>? LayoutChanged;

    public DiffLayoutPicker()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        ToolTip = "Show changes side by side or inline";
        AutomationProperties.SetName(this, "Diff layout");

        _split = Segment(DiffLayout.Split);
        _inline = Segment(DiffLayout.Inline);
        Children.Add(_split);
        Children.Add(_inline);
        Show(UserPreferences.DiffLayout);
    }

    private ToggleButton Segment(DiffLayout layout)
    {
        var button = new ToggleButton
        {
            Content = DiffLayouts.Title(layout),
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(layout == DiffLayout.Split ? 0 : 2, 0, 0, 0),
        };
        button.Click += (_, _) => Pick(layout);
        return button;
    }

    private void Pick(DiffLayout layout)
    {
        Show(layout);
        if (UserPreferences.DiffLayout == layout) return;
        UserPreferences.SetDiffLayout(layout);
        LayoutChanged?.Invoke(layout);
    }

    /// <summary>Reflect a layout without saving it, e.g. one picked in another view.</summary>
    public void Show(DiffLayout layout)
    {
        _split.IsChecked = layout == DiffLayout.Split;
        _inline.IsChecked = layout == DiffLayout.Inline;
    }
}
