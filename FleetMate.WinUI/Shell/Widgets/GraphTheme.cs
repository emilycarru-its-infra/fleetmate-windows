using FleetMate.Core.Development;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FleetMate.WinUI.Shell.Widgets;

/// <summary>
/// The strip's brushes and text styles, all theme resources: the app's chart
/// series brushes (stepped for Light and Dark, the highlight colour in High
/// Contrast) and the system text and fill brushes. The strip is rebuilt when
/// the theme changes, so every lookup here is current.
/// </summary>
internal static class GraphTheme
{
    /// <summary>A theme brush by key, or the secondary text brush when the key is missing.</summary>
    public static Brush Resource(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    /// <summary>The categorical order for bars coloured by position. Series8 is red, which the charts never use.</summary>
    private static readonly string[] Categorical =
    {
        "Series1Brush", "Series7Brush", "Series2Brush", "Series3Brush", "Series6Brush", "Series4Brush", "Series5Brush",
    };

    public static Brush Tone(ChartTone tone) => Resource(tone switch
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
        tone is { } t ? Tone(t) : Resource(Categorical[index % Categorical.Length]);

    public static Brush Track => Resource("BarTrackBrush");
    public static Brush Primary => Resource("TextFillColorPrimaryBrush");
    public static Brush Secondary => Resource("TextFillColorSecondaryBrush");
    public static Brush Placeholder => Resource("ControlAltFillColorTertiaryBrush");
    public static Brush Success => Resource("SystemFillColorSuccessBrush");

    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    /// <summary>
    /// Caption text: the smallest size anything in a card uses. A label
    /// (<paramref name="wrap"/>) wraps, inside a long name when it must, so a
    /// repository or title is never cut off; figures stay on one line.
    /// </summary>
    public static TextBlock Caption(string text, bool secondary = true, bool wrap = false) => new()
    {
        Text = wrap ? Breakable(text) : text,
        Style = Style("CaptionTextBlockStyle"),
        Foreground = secondary ? Secondary : Primary,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = TextTrimming.None,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// A name with a line-break opportunity after each '/', '.', '-' and '_',
    /// so "owner/repo.name" wraps at its parts rather than mid-word.
    /// </summary>
    public static string Breakable(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            builder.Append(c);
            if (c is '/' or '.' or '-' or '_') builder.Append('\u200B');
        }
        return builder.ToString();
    }
}