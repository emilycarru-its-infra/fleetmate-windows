using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace FleetMate.WinUI.Shell.Terminal;

/// <summary>
/// A thin strip that is dragged to resize something, showing the resize
/// pointer over it. The owner handles the pointer events; this only draws
/// the cursor, which WinUI exposes to subclasses alone.
/// </summary>
public sealed partial class ResizeHandle : Grid
{
    public ResizeHandle() : this(vertical: false) { }

    /// <param name="vertical">True for a divider dragged up and down, false for one dragged left and right.</param>
    public ResizeHandle(bool vertical)
    {
        IsVertical = vertical;
        ProtectedCursor = InputSystemCursor.Create(vertical ? InputSystemCursorShape.SizeNorthSouth : InputSystemCursorShape.SizeWestEast);
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public bool IsVertical
    {
        get;
        set
        {
            field = value;
            ProtectedCursor = InputSystemCursor.Create(value ? InputSystemCursorShape.SizeNorthSouth : InputSystemCursorShape.SizeWestEast);
        }
    }
}
