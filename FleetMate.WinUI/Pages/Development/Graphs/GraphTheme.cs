using FleetMate.Core.Development;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FleetMate.WinUI.Pages.Development.Graphs;

/// <summary>
/// The strip's brushes and text styles, all theme resources: the app's chart
/// series brushes (stepped for Light and Dark, the highlight colour in High
/// Contrast) and the system text and fill brushes. The strip is rebuilt when
/// the theme changes, so every lookup here is current.
/// </summary>
internal static class GraphTheme
{
    /// <summary>The categorical order for bars coloured by position. Series8 is red, which the charts never use.</summary>
    private static readonly string[] Categorical =
    {
        "Series1Brush", "Series7Brush", "Series2Brush", "Series3Brush", "Series6Brush", "Series4Brush", "Series5Brush",
    };

    public static Brush Tone(ChartTone tone) => DevelopmentUi.Theme(tone switch
    {
        ChartTone.Blue => "Series1Brush",
        ChartTone.Orange => "Series2Brush",
        ChartTone.Teal => "Series3Brush",
        ChartTone.Yellow or ChartTone.Brown => "Series4Brush",
        ChartTone.Purple or ChartTone.Pink => "Series5Brush",
        ChartTone.Green => "Series6Brush",
        ChartTone.Indigo => "Series7Brush",
        _ => "ControlStrongFillColorDefaultBrush",
    });

    /// <summary>A bar's own tone, or the categorical colour for its position.</summary>
    public static Brush Bar(ChartTone? tone, int index) =>
        tone is { } t ? Tone(t) : DevelopmentUi.Theme(Categorical[index % Categorical.Length]);

    public static Brush Track => DevelopmentUi.Theme("BarTrackBrush");
    public static Brush Primary => DevelopmentUi.Theme("TextFillColorPrimaryBrush");
    public static Brush Secondary => DevelopmentUi.Theme("TextFillColorSecondaryBrush");
    public static Brush Placeholder => DevelopmentUi.Theme("ControlAltFillColorTertiaryBrush");
    public static Brush Success => DevelopmentUi.Theme("SystemFillColorSuccessBrush");

    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    /// <summary>Caption text: the smallest size anything in a card uses.</summary>
    public static TextBlock Caption(string text, bool secondary = true) => new()
    {
        Text = text,
        Style = Style("CaptionTextBlockStyle"),
        Foreground = secondary ? Secondary : Primary,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
