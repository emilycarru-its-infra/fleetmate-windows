using System.Windows.Media;
using FleetMate.GUI.Views.Shared;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The row colours every list and table uses: an accent selection with white
/// text when focused, a grey one with normal text when not, a faint hover, and
/// the system highlight pair in high contrast.
/// </summary>
public class ListRowPaletteTests
{
    private static readonly Color DefaultAccent = Color.FromRgb(0x00, 0x78, 0xD4);
    private static readonly Color Highlight = Color.FromRgb(0x1A, 0xEB, 0xFF);
    private static readonly Color HighlightText = Colors.Black;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FocusedSelectionIsTheAccentWithWhiteText(bool dark)
    {
        var palette = ListRowPalette.For(dark, highContrast: false, DefaultAccent, Highlight, HighlightText);

        Assert.Equal(DefaultAccent, palette.Selected);
        Assert.Equal(Colors.White, palette.SelectedText);
    }

    [Fact]
    public void UnfocusedSelectionIsGreyWithTheThemesNormalText()
    {
        var light = ListRowPalette.For(dark: false, highContrast: false, DefaultAccent, Highlight, HighlightText);
        var dark = ListRowPalette.For(dark: true, highContrast: false, DefaultAccent, Highlight, HighlightText);

        // The unreadable case this replaces: a near-white fill under white text.
        Assert.Equal(Colors.Black, light.SelectedInactiveText);
        Assert.Equal(Colors.White, dark.SelectedInactiveText);
        Assert.True(Contrast(light.SelectedInactive, light.SelectedInactiveText) >= ListRowPalette.MinimumContrast);
        Assert.True(Contrast(dark.SelectedInactive, dark.SelectedInactiveText) >= ListRowPalette.MinimumContrast);
        Assert.Equal(light.SelectedInactive.R, light.SelectedInactive.G);
        Assert.Equal(dark.SelectedInactive.R, dark.SelectedInactive.G);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HoverAndStripeAreFaintTints(bool dark)
    {
        var palette = ListRowPalette.For(dark, highContrast: false, DefaultAccent, Highlight, HighlightText);

        Assert.InRange(palette.Hover.A, 1, 0x20);
        Assert.InRange(palette.Stripe.A, 1, palette.Hover.A - 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HighContrastUsesTheSystemHighlightPairAndNoTints(bool dark)
    {
        var palette = ListRowPalette.For(dark, highContrast: true, DefaultAccent, Highlight, HighlightText);

        Assert.Equal(Highlight, palette.Selected);
        Assert.Equal(HighlightText, palette.SelectedText);
        Assert.Equal(Highlight, palette.SelectedInactive);
        Assert.Equal(HighlightText, palette.SelectedInactiveText);
        Assert.Equal(0, palette.Hover.A);
        Assert.Equal(0, palette.Stripe.A);
    }

    [Fact]
    public void APaleAccentIsDarkenedUntilWhiteTextReads()
    {
        var pale = Color.FromRgb(0x99, 0xEB, 0xFF);

        var palette = ListRowPalette.For(dark: true, highContrast: false, pale, Highlight, HighlightText);

        Assert.NotEqual(pale, palette.Selected);
        Assert.True(ListRowPalette.ContrastWithWhite(palette.Selected) >= ListRowPalette.MinimumContrast);
    }

    [Fact]
    public void ADarkEnoughAccentIsKept()
    {
        Assert.Equal(DefaultAccent, ListRowPalette.Readable(DefaultAccent));
    }

    private static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double Ch(byte v) { var x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        var (hi, lo) = (Math.Max(L(a), L(b)), Math.Min(L(a), L(b)));
        return (hi + 0.05) / (lo + 0.05);
    }
}
