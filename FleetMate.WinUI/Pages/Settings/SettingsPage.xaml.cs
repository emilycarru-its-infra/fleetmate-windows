using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FleetMate.WinUI.Pages.Settings;

/// <summary>
/// Settings: General (enabled modules, configuration store), Authentication
/// (sign-in status), Appearance (theme, ticket badges) and About. The logic
/// is <see cref="SettingsViewModel"/>'s; this page only switches sections.
/// </summary>
public sealed partial class SettingsPage : Page, Shell.ICaptureViews
{
    public SettingsPage()
    {
        ViewModel = new SettingsViewModel(App.Services);
        InitializeComponent();
        ViewModel.AuthSystems.CollectionChanged += (_, _) => UpdateAuthEmpty();
        UpdateAuthEmpty();
    }

    public SettingsViewModel ViewModel { get; }

    private void Sections_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var section = sender.SelectedItem?.Tag as string ?? "General";
        GeneralPanel.Visibility = Show(section == "General");
        AuthenticationPanel.Visibility = Show(section == "Authentication");
        AppearancePanel.Visibility = Show(section == "Appearance");
        AboutPanel.Visibility = Show(section == "About");
        if (section == "Authentication") ViewModel.RefreshAuthCommand.Execute(null);
    }

    /// <summary>Show a section by name, for the QA capture and for "Configure…" links.</summary>
    internal void ShowSection(string section)
    {
        foreach (var item in Sections.Items)
            if (Equals(item.Tag, section)) Sections.SelectedItem = item;
    }

    /// <summary>The sections after General, for the QA capture.</summary>
    public IReadOnlyList<string> CaptureViews { get; } = new[] { "authentication", "appearance", "about" };

    public Task ShowCaptureViewAsync(string view)
    {
        ShowSection(char.ToUpperInvariant(view[0]) + view[1..]);
        return Task.CompletedTask;
    }

    private void UpdateAuthEmpty() =>
        NoAuthText.Visibility = Show(!ViewModel.HasAuthSystems);

    private static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
}
