using FleetMate.Core.Devices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FleetMate.WinUI.Pages.Devices;

/// <summary>Small pieces the Devices views share: theme brushes for a <see cref="DeviceTone"/>, badges and confirmations.</summary>
internal static class DeviceUi
{
    public static Brush Theme(string key) => (Brush)Application.Current.Resources[key];

    public static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A dot, bar or icon in the tone's system colour, which each theme (and High Contrast) sets.</summary>
    public static Brush ToneBrush(DeviceTone tone) => Theme(tone switch
    {
        DeviceTone.Good => "SystemFillColorSuccessBrush",
        DeviceTone.Caution => "SystemFillColorCautionBrush",
        DeviceTone.Attention => "SystemFillColorCriticalBrush",
        _ => "ControlStrongFillColorDefaultBrush",
    });

    /// <summary>The tone's quiet background, for badges and highlighted rows that carry primary text.</summary>
    public static Brush ToneBackground(DeviceTone tone) => Theme(tone switch
    {
        DeviceTone.Good => "SystemFillColorSuccessBackgroundBrush",
        DeviceTone.Caution => "SystemFillColorCautionBackgroundBrush",
        DeviceTone.Attention => "SystemFillColorCriticalBackgroundBrush",
        _ => "SystemFillColorNeutralBackgroundBrush",
    });

    /// <summary>A state badge: a coloured dot and the state's name on its quiet background.</summary>
    public static Border Badge(DeviceTone tone, string text)
    {
        var badge = new Border
        {
            Background = ToneBackground(tone),
            CornerRadius = (CornerRadius)Application.Current.Resources["ControlCornerRadius"],
            Padding = new Thickness(8, 2, 8, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, Fill = ToneBrush(tone), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = text, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                },
            },
        };
        AutomationProperties.SetName(badge, text);
        return badge;
    }

    /// <summary>
    /// Ask before an action. A destructive one makes Cancel the default, so
    /// Enter never wipes a device by accident.
    /// </summary>
    public static async Task<bool> ConfirmAsync(XamlRoot root, DeviceActionConfirm confirm, bool destructive, string? primary = null)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = confirm.Title,
            Content = new TextBlock { Text = confirm.Message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary ?? (destructive ? "Continue" : "OK"),
            CloseButtonText = "Cancel",
            DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Say something that needs acknowledging, such as why a reveal could not be read.</summary>
    public static async Task InformAsync(XamlRoot root, string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }
}
