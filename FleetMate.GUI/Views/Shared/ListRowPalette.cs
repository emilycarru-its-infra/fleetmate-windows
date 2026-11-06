using System.Windows;
using System.Windows.Media;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// The fills and text colours of list and table rows, the Mac's look: a
/// focused list's selection is an accent fill with white text, an unfocused
/// one a subdued grey with normal text, and hover only a faint tint. In high
/// contrast every selection uses the system's highlight pair and nothing is
/// tinted, so the scheme the person chose is the one they see.
/// </summary>
public sealed record ListRowPalette(
    Color Selected,
    Color SelectedText,
    Color SelectedInactive,
    Color SelectedInactiveText,
    Color Hover,
    Color Stripe)
{
    /// <summary>The resource keys <c>Views/Shared/ListRows.xaml</c> reads.</summary>
    public const string SelectedKey = "FleetRowSelectedBrush";
    public const string SelectedTextKey = "FleetRowSelectedForegroundBrush";
    public const string SelectedInactiveKey = "FleetRowSelectedInactiveBrush";
    public const string SelectedInactiveTextKey = "FleetRowSelectedInactiveForegroundBrush";
    public const string HoverKey = "FleetRowHoverBrush";
    public const string StripeKey = "FleetRowStripeBrush";

    /// <summary>
    /// The palette for a theme. <paramref name="accent"/> is the accent colour;
    /// <paramref name="highlight"/> and <paramref name="highlightText"/> are the
    /// system highlight pair, used only in high contrast.
    /// </summary>
    public static ListRowPalette For(bool dark, bool highContrast, Color accent, Color highlight, Color highlightText)
    {
        if (highContrast)
        {
            return new ListRowPalette(
                Selected: highlight,
                SelectedText: highlightText,
                SelectedInactive: highlight,
                SelectedInactiveText: highlightText,
                Hover: Colors.Transparent,
                Stripe: Colors.Transparent);
        }

        // Accents too light for white text on them (a pale system accent)
        // are darkened until white reads on them.
        var selected = Readable(accent);
        return dark
            ? new ListRowPalette(
                Selected: selected,
                SelectedText: Colors.White,
                SelectedInactive: Color.FromRgb(0x48, 0x48, 0x4E),
                SelectedInactiveText: Colors.White,
                Hover: Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
                Stripe: Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF))
            : new ListRowPalette(
                Selected: selected,
                SelectedText: Colors.White,
                SelectedInactive: Color.FromRgb(0xDC, 0xDC, 0xE0),
                SelectedInactiveText: Colors.Black,
                Hover: Color.FromArgb(0x0F, 0x00, 0x00, 0x00),
                Stripe: Color.FromArgb(0x08, 0x00, 0x00, 0x00));
    }

    /// <summary>The palette for the running app's theme, accent and contrast setting.</summary>
    public static ListRowPalette Current(bool dark)
    {
        var accent = Application.Current?.TryFindResource("SystemAccentColor") is Color a ? a : Color.FromRgb(0x00, 0x78, 0xD4);
        return For(dark, SystemParameters.HighContrast, accent, SystemColors.HighlightColor, SystemColors.HighlightTextColor);
    }

    /// <summary>Put the palette's brushes in <paramref name="resources"/> under the keys the row styles read.</summary>
    public void ApplyTo(ResourceDictionary resources)
    {
        resources[SelectedKey] = Brush(Selected);
        resources[SelectedTextKey] = Brush(SelectedText);
        resources[SelectedInactiveKey] = Brush(SelectedInactive);
        resources[SelectedInactiveTextKey] = Brush(SelectedInactiveText);
        resources[HoverKey] = Brush(Hover);
        resources[StripeKey] = Brush(Stripe);
    }

    /// <summary>WCAG contrast of white text on <paramref name="color"/>.</summary>
    public static double ContrastWithWhite(Color color) => 1.05 / (RelativeLuminance(color) + 0.05);

    /// <summary>White text on a fill needs at least this contrast, the WCAG level for normal text.</summary>
    public const double MinimumContrast = 4.5;

    /// <summary><paramref name="color"/>, darkened in steps until white text on it reaches <see cref="MinimumContrast"/>.</summary>
    public static Color Readable(Color color)
    {
        var c = Color.FromRgb(color.R, color.G, color.B);
        for (var i = 0; i < 20 && ContrastWithWhite(c) < MinimumContrast; i++)
            c = Color.FromRgb((byte)(c.R * 0.9), (byte)(c.G * 0.9), (byte)(c.B * 0.9));
        return c;
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
