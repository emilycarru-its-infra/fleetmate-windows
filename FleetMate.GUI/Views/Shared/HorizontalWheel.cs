using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// WPF never routes WM_MOUSEHWHEEL, so trackpad two-finger horizontal swipes
/// (and tilt wheels) do nothing on a horizontal ScrollViewer. Attaching this
/// hooks the window message loop and scrolls the viewer under the pointer,
/// and also maps Shift+vertical-wheel to horizontal scrolling.
/// </summary>
public static class HorizontalWheel
{
    private const int WM_MOUSEHWHEEL = 0x020E;

    public static void Attach(ScrollViewer viewer)
    {
        HwndSource? hooked = null;

        HwndSourceHook hook = (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_MOUSEHWHEEL && viewer.IsVisible && viewer.IsMouseOver)
            {
                var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset + delta);
                handled = true;
            }
            return IntPtr.Zero;
        };

        viewer.Loaded += (_, _) =>
        {
            if (hooked != null) return;
            if (Window.GetWindow(viewer) is not { } window) return;
            if (PresentationSource.FromVisual(window) is not HwndSource source) return;
            source.AddHook(hook);
            hooked = source;
        };

        viewer.Unloaded += (_, _) =>
        {
            hooked?.RemoveHook(hook);
            hooked = null;
        };

        // Shift + vertical wheel scrolls horizontally, like everywhere else.
        viewer.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Shift) return;
            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta);
            e.Handled = true;
        };
    }
}
