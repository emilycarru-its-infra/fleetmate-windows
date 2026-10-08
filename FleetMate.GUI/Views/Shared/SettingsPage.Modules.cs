using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Config;

namespace FleetMate.GUI.Views.Shared;

/// <summary>Settings › General: Setup Wizard and Enabled Modules; Appearance: Text size.</summary>
public partial class SettingsPage
{
    private void LoadPreferences(FleetMateConfig config)
    {
        BuildModulesPanel(config);
        TextSizeSlider.Value = UserPreferences.TextScale;
        ShowTextSize(UserPreferences.TextScale);
    }

    private void BuildModulesPanel(FleetMateConfig config)
    {
        ModulesPanel.Children.Clear();
        var hidden = UserPreferences.HiddenModules;
        foreach (var module in AppModules.All)
        {
            var shown = !hidden.Contains(module.Tag);
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };

            var toggle = new CheckBox
            {
                IsChecked = shown,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = shown ? $"Hide the {module.Title} tab" : $"Show the {module.Title} tab",
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(toggle, $"SettingsModule{module.Tag}");
            System.Windows.Automation.AutomationProperties.SetName(toggle, module.Title);
            var tag = module.Tag;
            toggle.Click += (_, _) =>
            {
                UserPreferences.SetModuleShown(tag, toggle.IsChecked == true);
                // Hiding the last tab is refused; the rebuild puts the box back.
                BuildModulesPanel(config);
            };
            DockPanel.SetDock(toggle, Dock.Left);
            row.Children.Add(toggle);

            // Switched on but missing its endpoint: offer the way to set it up
            // instead of leaving an empty tab.
            if (shown && !AppModules.IsConfigured(module.Tag, config))
            {
                var configure = new Button
                {
                    Content = "Configure",
                    Padding = new Thickness(10, 3, 10, 3),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Enter this module's endpoint on the Authentication tab",
                };
                configure.Click += (_, _) => SelectSettingsTab("Authentication");
                DockPanel.SetDock(configure, Dock.Right);
                row.Children.Add(configure);
            }

            var text = new StackPanel { Margin = new Thickness(8, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = module.Title, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock
            {
                Text = shown && !AppModules.IsConfigured(module.Tag, config)
                    ? module.Subtitle + " · not set up yet"
                    : module.Subtitle,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
            });
            row.Children.Add(text);
            ModulesPanel.Children.Add(row);
        }
    }

    private void SelectSettingsTab(string header)
    {
        foreach (var item in SettingsTabs.Items.OfType<TabItem>())
            if (item.Header as string == header)
            {
                SettingsTabs.SelectedItem = item;
                return;
            }
    }

    private void OnSetupWizardClicked(object sender, RoutedEventArgs e)
    {
        var wizard = new SetupWizardWindow { Owner = Window.GetWindow(this) };
        wizard.ShowDialog();
        LoadSettings();
        _ = RefreshAuthCardsAsync();
    }

    /// <summary>
    /// Ctrl+Plus and Ctrl+Minus change the same setting from anywhere, so an
    /// open Settings page follows them instead of showing the old size.
    /// </summary>
    private void OnPreferencesChanged() => Dispatcher.InvokeAsync(() =>
    {
        var scale = UserPreferences.TextScale;
        if (Math.Abs(TextSizeSlider.Value - scale) < 0.001) return;
        _syncingTextSize = true;
        try { TextSizeSlider.Value = scale; }
        finally { _syncingTextSize = false; }
        ShowTextSize(scale);
    });

    private bool _syncingTextSize;

    private void OnTextSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoadingSettings || _syncingTextSize || !IsLoaded) return;
        ShowTextSize(e.NewValue);
        UserPreferences.SetTextScale(e.NewValue);
    }

    private void OnTextSizeResetClicked(object sender, RoutedEventArgs e) =>
        TextSizeSlider.Value = AppTextScale.Default;

    private void ShowTextSize(double scale)
    {
        TextSizeLabel.Text = AppTextScale.Label(scale);
        TextSizeResetButton.IsEnabled = !AppTextScale.IsDefault(scale);
    }
}
