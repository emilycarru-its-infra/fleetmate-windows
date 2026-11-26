using System.Windows;
using System.Windows.Media;

namespace FleetMate.GUI.Views.Development.Repos;

/// <summary>The clipboard for the Repos views.</summary>
internal static class RepoClipboard
{
    public static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { }
    }
}

/// <summary>
/// The Repos palette. Nothing destructive or removed is red: deletions and
/// warnings are orange, as in FleetMate for Mac.
/// </summary>
internal static class RepoBrushes
{
    public static readonly Brush Added = Frozen(0x1A, 0x9E, 0x8F);
    public static readonly Brush Removed = Frozen(0xE0, 0x7A, 0x1F);
    public static readonly Brush Warning = Frozen(0xE0, 0x7A, 0x1F);
    public static readonly Brush Success = Frozen(0x2D, 0xA4, 0x4E);
    public static readonly Brush Other = Frozen(0x8E, 0x8E, 0x93);

    /// <summary>Series colours in a fixed order, never cycled into red.</summary>
    public static readonly Brush[] Series =
    {
        Frozen(0x00, 0x78, 0xD4), Frozen(0xE0, 0x7A, 0x1F), Frozen(0x1A, 0x9E, 0x8F), Frozen(0x88, 0x5A, 0xC8),
        Frozen(0x9C, 0x6B, 0x3E), Frozen(0x3C, 0xB3, 0x71), Frozen(0x4B, 0x5F, 0xC7),
    };

    /// <summary>The app's own accent, which bars and badges fill with.</summary>
    public static Brush Accent =>
        Application.Current?.TryFindResource("FleetMateAccentBrush") as Brush ?? Series[0];

    public static Brush Secondary =>
        Application.Current?.TryFindResource("SystemControlForegroundBaseMediumBrush") as Brush ?? Brushes.Gray;

    public static Brush Text =>
        Application.Current?.TryFindResource("SystemControlForegroundBaseHighBrush") as Brush ?? Brushes.Black;

    public static Brush Subtle =>
        Application.Current?.TryFindResource("SubtleFillBrush") as Brush ?? Brushes.LightGray;

    public static Brush Lane(int index) => Series[((index % Series.Length) + Series.Length) % Series.Length];

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
