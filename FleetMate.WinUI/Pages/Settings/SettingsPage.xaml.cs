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
        Edit = new SettingsEditViewModel(App.Services);
        InitializeComponent();
        // TicketsMate has no Manage tab and no agent sessions.
        if (Core.Config.AppEdition.Current.IsTicketsOnly)
            foreach (var item in Sections.Items.Where(i => i.Tag is "Manage" or "Terminal").ToList())
                Sections.Items.Remove(item);
        ViewModel.AuthSystems.CollectionChanged += (_, _) => UpdateAuthEmpty();
        UpdateAuthEmpty();
    }

    public SettingsViewModel ViewModel { get; }

    /// <summary>Connections, Manage and Terminal.</summary>
    public SettingsEditViewModel Edit { get; }

    private void Sections_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var section = sender.SelectedItem?.Tag as string ?? "General";
        GeneralPanel.Visibility = Show(section == "General");
        AuthenticationPanel.Visibility = Show(section == "Authentication");
        AppearancePanel.Visibility = Show(section == "Appearance");
        AboutPanel.Visibility = Show(section == "About");
        var cards = section switch
        {
            "Connections" => Edit.Connections,
            "Manage" => Edit.Manage,
            "Terminal" => Edit.Terminal,
            _ => null,
        };
        EditPanel.Visibility = Show(cards != null);
        EditCards.ItemsSource = cards;
        SshKeyStatusText.Visibility = Show(section == "Manage");
        if (section == "Authentication") ViewModel.RefreshAuthCommand.Execute(null);
    }

    /// <summary>Show a section by name, for the QA capture and for "Configure…" links.</summary>
    internal void ShowSection(string section)
    {
        foreach (var item in Sections.Items)
            if (Equals(item.Tag, section)) Sections.SelectedItem = item;
    }

    /// <summary>The sections after General, for the QA capture.</summary>
    public IReadOnlyList<string> CaptureViews =>
        Sections.Items.Skip(1).Select(i => ((string)i.Tag).ToLowerInvariant()).ToList();

    public Task ShowCaptureViewAsync(string view)
    {
        ShowSection(char.ToUpperInvariant(view[0]) + view[1..]);
        return Task.CompletedTask;
    }

    private async void SetupWizardButton_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.RunSetupWizardCommand.ExecuteAsync(XamlRoot);

    private void UpdateAuthEmpty() =>
        NoAuthText.Visibility = Show(!ViewModel.HasAuthSystems);

    private static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
}
