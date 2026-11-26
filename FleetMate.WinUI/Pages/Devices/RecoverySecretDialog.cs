using System.Security.Cryptography;
using System.Text;
using FleetMate.Core.Models.Devices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace FleetMate.WinUI.Pages.Devices;

/// <summary>
/// The only place a revealed secret is shown. Closing the dialog empties its
/// boxes and drops the secrets, so nothing outlives it but what was copied,
/// and that stays out of clipboard history and cloud sync and is cleared
/// after a minute (<see cref="SecretClipboard"/>).
/// </summary>
public sealed partial class RecoverySecretDialog : ContentDialog
{
    private readonly List<RevealedSecret> _secrets;
    private readonly List<TextBox> _boxes = new();

    public RecoverySecretDialog(string deviceName, RecoverySecretKind kind, List<RevealedSecret> secrets)
    {
        _secrets = secrets;
        Title = kind.DisplayName();
        CloseButtonText = "Close";
        DefaultButton = ContentDialogButton.Close;

        var root = new StackPanel { Spacing = 4, MinWidth = 440 };
        root.Children.Add(new TextBlock { Text = deviceName, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], Margin = new Thickness(0, 0, 0, 8) });
        foreach (var secret in secrets)
        {
            root.Children.Add(new TextBlock { Text = secret.Label, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 6, 0, 2) });
            var row = new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions = { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } },
            };
            var box = new TextBox
            {
                Text = secret.Value,
                IsReadOnly = true,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                TextWrapping = TextWrapping.Wrap,
            };
            AutomationProperties.SetName(box, secret.Label);
            _boxes.Add(box);
            var copy = new Button { Content = "Copy", MinWidth = 72, VerticalAlignment = VerticalAlignment.Top };
            AutomationProperties.SetName(copy, $"Copy {secret.Label}");
            var current = secret;
            copy.Click += (_, _) =>
            {
                SecretClipboard.Copy(current.Value, DispatcherQueue);
                copy.Content = "Copied";
            };
            Grid.SetColumn(copy, 1);
            row.Children.Add(box);
            row.Children.Add(copy);
            root.Children.Add(row);
            if (!string.IsNullOrEmpty(secret.Detail))
                root.Children.Add(new TextBlock
                {
                    Text = secret.Detail,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    TextWrapping = TextWrapping.Wrap,
                });
        }
        root.Children.Add(new TextBlock
        {
            Text = "Copied values stay out of clipboard history and are cleared after a minute.",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        });
        Content = root;
        Closed += (_, _) => Forget();
    }

    private void Forget()
    {
        foreach (var box in _boxes) box.Text = "";
        _boxes.Clear();
        foreach (var secret in _secrets) secret.Forget();
        _secrets.Clear();
        Content = null;
    }
}

/// <summary>
/// Puts a secret on the clipboard so Windows keeps it out of clipboard
/// history and cloud clipboard sync, and takes it off again after a minute if
/// it is still there. Only a hash of the value is kept, to recognise it.
/// </summary>
internal static class SecretClipboard
{
    public static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(60);

    private static DispatcherQueueTimer? _timer;

    public static void Copy(string secret, DispatcherQueue queue)
    {
        var package = new DataPackage();
        package.SetText(secret);
        Clipboard.SetContentWithOptions(package, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false });
        var hash = Hash(secret);

        _timer?.Stop();
        _timer = queue.CreateTimer();
        _timer.Interval = ClearAfter;
        _timer.IsRepeating = false;
        _timer.Tick += async (_, _) =>
        {
            _timer = null;
            try
            {
                // Clear only our value: the person may have copied something else since.
                var content = Clipboard.GetContent();
                if (content.Contains(StandardDataFormats.Text) && Hash(await content.GetTextAsync()) == hash) Clipboard.Clear();
            }
            catch (Exception)
            {
                // The clipboard is busy or holds something unreadable; leave it.
            }
        };
        _timer.Start();
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
