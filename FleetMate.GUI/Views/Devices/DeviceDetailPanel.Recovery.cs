using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Models.Devices;
using Serilog;

namespace FleetMate.GUI.Views.Devices;

/// <summary>
/// The Recovery section: reveal the selected device's recovery secrets on
/// request, only those its platform has. Every reveal is confirmed first,
/// and the value appears only in <see cref="RecoverySecretWindow"/>.
/// </summary>
public partial class DeviceDetailPanel
{
    private void RenderRecovery(IntuneDevice device)
    {
        var kinds = RecoverySecretKinds.Available(device.OperatingSystem);
        if (kinds.Count == 0) return;

        var host = Section("Recovery", "");
        foreach (var kind in kinds)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var reveal = new Button { Content = "Reveal…", MinWidth = 80, Padding = new Thickness(10, 3, 10, 3) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(reveal, $"Reveal{kind}Button");
            DockPanel.SetDock(reveal, Dock.Right);
            row.Children.Add(reveal);
            row.Children.Add(new TextBlock { Text = kind.DisplayName(), VerticalAlignment = VerticalAlignment.Center });
            host.Children.Add(row);

            var k = kind;
            reveal.Click += async (_, _) => await RevealAsync(device, k, reveal);
        }
    }

    private async Task RevealAsync(IntuneDevice device, RecoverySecretKind kind, Button button)
    {
        if (_graphService == null) return;
        var name = string.IsNullOrWhiteSpace(device.DeviceName) ? device.SerialNumber ?? device.Id : device.DeviceName;
        var confirm = MessageBox.Show(Window.GetWindow(this)!,
            $"Show the {kind.DisplayName()} for {name}?\n\nThe request is recorded in the directory audit log.",
            "Reveal recovery secret", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        button.IsEnabled = false;
        try
        {
            var secrets = await _graphService.RevealRecoverySecretAsync(kind, device);
            // Another device may have been selected while this one loaded.
            if (_device?.Id != device.Id) { secrets.ForEach(s => s.Forget()); return; }
            new RecoverySecretWindow(name, kind, secrets) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (RecoverySecretException ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, ex.Message, kind.DisplayName(), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            // The type only: an exception is never formatted with a secret in it,
            // but its message is not ours to vouch for.
            Log.Warning("Revealing {Kind} for {DeviceId} failed: {Error}", kind, device.Id, ex.GetType().Name);
            MessageBox.Show(Window.GetWindow(this)!, "FleetMate couldn't read it. Check the elevation status and try again.",
                kind.DisplayName(), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
}
