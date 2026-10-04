using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services.Devices;

namespace FleetMate.GUI.Views.Devices;

/// <summary>
/// Wipe options per platform, and Offboard: the Intune action, then the
/// Autopilot, Entra and Intune records, in one confirmed pass. Ported from the
/// macOS client so the options and step names match.
/// </summary>
public partial class IntunePage
{
    private void InitializeLifecycleControls()
    {
        WipeObliterationComboBox.ItemsSource = Enum.GetValues<WipeOptions.ObliterationBehavior>()
            .Select(b => new ComboBoxItem { Content = WipeOptions.DisplayName(b), Tag = b }).ToList();
        WipeObliterationComboBox.SelectedIndex = 0;

        OffboardTerminalComboBox.ItemsSource = Enum.GetValues<OffboardPlan.TerminalAction>()
            .Select(a => new ComboBoxItem { Content = OffboardPlan.DisplayName(a), Tag = a }).ToList();
        OffboardTerminalComboBox.SelectedIndex = 0;

        OffboardEntraComboBox.ItemsSource = Enum.GetValues<OffboardPlan.EntraAction>()
            .Select(a => new ComboBoxItem { Content = OffboardPlan.DisplayName(a), Tag = a }).ToList();
        OffboardEntraComboBox.SelectedIndex = 0;
    }

    /// <summary>Show the wipe options the selected platforms accept, and Offboard when every device can take it.</summary>
    private void UpdateLifecycleSections(List<DeviceListRow> selected)
    {
        var platforms = selected.Where(r => r.Intune != null).Select(r => r.Intune!.Platform()).ToHashSet();
        var hasWindows = platforms.Contains(DevicePlatform.Windows);
        var hasApple = platforms.Contains(DevicePlatform.MacOS) || platforms.Contains(DevicePlatform.IOS);

        WipeMixedPlatformText.Visibility = Show(platforms.Count > 1);
        WipeKeepUserDataCheckBox.Visibility = Show(hasWindows);
        WipeProtectedCheckBox.Visibility = Show(hasWindows);
        WipeUnlockCodeTextBox.Visibility = Show(hasApple);
        WipeObliterationComboBox.Visibility = Show(platforms.Contains(DevicePlatform.MacOS));

        // An enrolled device takes the whole pass; a registered-only device
        // takes the directory steps, with the Intune ones reported skipped.
        OffboardSection.Visibility = Show(selected.Count > 0 && selected.All(r => r.IsEnrolled || r.Autopilot != null));
        UpdateOffboardWarning();
    }

    private static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private WipeOptions CurrentWipeOptions() => new()
    {
        KeepEnrollmentData = WipeKeepEnrollmentCheckBox.IsChecked == true,
        KeepUserData = WipeKeepUserDataCheckBox.IsChecked == true,
        UseProtectedWipe = WipeProtectedCheckBox.IsChecked == true,
        MacOsUnlockCode = string.IsNullOrWhiteSpace(WipeUnlockCodeTextBox.Text) ? null : WipeUnlockCodeTextBox.Text.Trim(),
        Obliteration = (WipeObliterationComboBox.SelectedItem as ComboBoxItem)?.Tag as WipeOptions.ObliterationBehavior?
                       is { } b && b != WipeOptions.ObliterationBehavior.Default ? b : null,
    };

    private OffboardPlan CurrentOffboardPlan() => new()
    {
        Terminal = (OffboardTerminalComboBox.SelectedItem as ComboBoxItem)?.Tag is OffboardPlan.TerminalAction t ? t : OffboardPlan.TerminalAction.Wipe,
        WipeOptions = CurrentWipeOptions(),
        DeleteAutopilotRegistration = OffboardDeleteAutopilotCheckBox.IsChecked == true,
        Entra = (OffboardEntraComboBox.SelectedItem as ComboBoxItem)?.Tag is OffboardPlan.EntraAction e ? e : OffboardPlan.EntraAction.None,
        DeleteIntuneRecord = OffboardDeleteIntuneCheckBox.IsChecked == true,
    };

    private void OnOffboardPlanChanged(object sender, RoutedEventArgs e) => UpdateOffboardWarning();

    private void UpdateOffboardWarning()
    {
        if (OffboardTerminalComboBox == null || OffboardCancelsWarningText == null) return;
        var plan = CurrentOffboardPlan();
        OffboardUsesWipeOptionsText.Visibility = Show(plan.Terminal == OffboardPlan.TerminalAction.Wipe);
        OffboardCancelsWarningText.Visibility = Show(plan.CancelsPendingAction);
        OffboardCancelsWarningText.Text = $"The pending action lives on the Intune record — deleting it before the device checks in cancels the {(plan.Terminal == OffboardPlan.TerminalAction.Wipe ? "wipe" : "retire")}.";
    }

    private async void OnOffboardClicked(object sender, RoutedEventArgs e)
    {
        if (_graphService == null) return;
        var rows = SelectedRows().Where(r => r.IsEnrolled || r.Autopilot != null).ToList();
        var plan = CurrentOffboardPlan();
        if (rows.Count == 0) return;

        if (MessageBox.Show(plan.Summary(rows.Count), "Confirm Offboard", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        ShowActionMessage($"Offboarding {rows.Count} device(s)...", isLoading: true);
        OffboardResultsPanel.Children.Clear();

        var offboarder = new DeviceOffboarder(_graphService);
        var results = new List<OffboardResult>();
        foreach (var row in rows)
        {
            try
            {
                results.Add(row.Intune is { } device
                    ? await _graphService.OffboardDeviceAsync(device, plan, row.Autopilot, confirmed: true)
                    : await offboarder.OffboardOrphanAsync(row.SerialText, row.Autopilot,
                        await offboarder.FindOrphanEntraAsync(row.Autopilot), plan));
            }
            catch (Exception ex)
            {
                results.Add(new OffboardResult(row.SerialText, row.NameText, DevicePlatform.Other,
                    new[] { new OffboardStepResult("Offboard", OffboardStepResult.StepOutcome.Failed, ex.Message) }));
            }
        }

        foreach (var result in results.OrderBy(r => r.DeviceName ?? r.Identifier, StringComparer.OrdinalIgnoreCase))
            OffboardResultsPanel.Children.Add(ResultBlock(result));

        var failed = results.Count(r => !r.Success);
        ShowActionMessage(failed == 0
            ? $"Offboarded {results.Count} device(s)"
            : $"Offboarded {results.Count - failed} device(s), {failed} with failures", isError: failed > 0);
    }

    private FrameworkElement ResultBlock(OffboardResult result)
    {
        var block = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        block.Children.Add(new TextBlock { Text = result.DeviceName ?? result.Identifier, FontWeight = FontWeights.SemiBold, FontSize = 12 });
        foreach (var step in result.Steps)
        {
            var (glyph, color) = step.Outcome switch
            {
                OffboardStepResult.StepOutcome.Succeeded => ("✓", "#2E9E4F"),
                OffboardStepResult.StepOutcome.Failed => ("✕", "#E8890C"),
                _ => ("–", "#808080"),
            };
            var line = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
            line.Inlines.Add(new System.Windows.Documents.Run(glyph + " ")
                { Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)) });
            line.Inlines.Add(new System.Windows.Documents.Run(step.Display)
                { Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush") });
            block.Children.Add(line);
        }
        return block;
    }
}
