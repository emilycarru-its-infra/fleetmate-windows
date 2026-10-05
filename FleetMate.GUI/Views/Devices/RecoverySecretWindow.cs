using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Models.Devices;

namespace FleetMate.GUI.Views.Devices;

/// <summary>
/// The only place a revealed secret is shown. Closing the sheet empties its
/// text boxes and drops the secrets, so nothing outlives it but whatever
/// the person copied, and that is cleared from the clipboard after a minute.
/// </summary>
public sealed class RecoverySecretWindow : Window
{
    private readonly List<RevealedSecret> _secrets;
    private readonly List<TextBox> _boxes = new();

    public RecoverySecretWindow(string deviceName, RecoverySecretKind kind, List<RevealedSecret> secrets)
    {
        _secrets = secrets;
        Title = $"{kind.DisplayName()} — {deviceName}";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SystemControlBackgroundAltHighBrush");
        SetResourceReference(ForegroundProperty, "SystemControlForegroundBaseHighBrush");

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = kind.DisplayName(), FontSize = 16, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock
        {
            Text = deviceName,
            Margin = new Thickness(0, 2, 0, 12),
            Opacity = 0.7
        });

        foreach (var secret in secrets)
        {
            root.Children.Add(new TextBlock { Text = secret.Label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2) });
            var row = new DockPanel();
            var copy = new Button { Content = "Copy", Margin = new Thickness(8, 0, 0, 0), MinWidth = 70 };
            DockPanel.SetDock(copy, Dock.Right);
            var box = new TextBox
            {
                Text = secret.Value,
                IsReadOnly = true,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap
            };
            _boxes.Add(box);
            var current = secret;
            copy.Click += (_, _) =>
            {
                SecretClipboard.Copy(current.Value);
                copy.Content = "Copied";
            };
            row.Children.Add(copy);
            row.Children.Add(box);
            root.Children.Add(row);
            if (!string.IsNullOrEmpty(secret.Detail))
                root.Children.Add(new TextBlock { Text = secret.Detail, FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
        }

        root.Children.Add(new TextBlock
        {
            Text = "Copied values stay out of clipboard history and are cleared after a minute.",
            FontSize = 11,
            Opacity = 0.7,
            Margin = new Thickness(0, 14, 0, 10),
            TextWrapping = TextWrapping.Wrap
        });
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        root.Children.Add(close);
        Content = root;

        Closed += (_, _) => Forget();
    }

    private void Forget()
    {
        foreach (var box in _boxes) box.Clear();
        _boxes.Clear();
        foreach (var secret in _secrets) secret.Forget();
        _secrets.Clear();
        Content = null;
    }
}
