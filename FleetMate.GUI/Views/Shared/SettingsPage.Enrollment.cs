using System.IO;
using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services.Devices;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// Settings › Enrollment: where the enrollment records joined into Devices
/// come from. Windows registrations are read through the Devices connection;
/// Mac, iPad and iPhone records from the Apple School and Business Manager
/// API profiles added here. The key ID and private key go straight into
/// Windows Credential Manager; the key file itself is not kept or copied
/// anywhere.
/// </summary>
public partial class SettingsPage
{
    private readonly AppleOrgCredentialStore _appleStore = new();

    private void OnEnrollmentTabLoaded(object sender, RoutedEventArgs e) => RefreshAppleProfiles();

    private void RefreshAppleProfiles()
    {
        var count = 0;
        try
        {
            var profiles = _appleStore.Profiles();
            count = profiles.Count;
            var labels = AppleOrgProfile.Labels(profiles);
            AppleProfilesList.ItemsSource = profiles.Select(p => new AppleProfileItem(p, $"{labels[p.Name]} — {p.Name}")).ToList();
            AppleProfilesList.DisplayMemberPath = nameof(AppleProfileItem.Display);
            AppleProfilesEmptyText.Visibility = profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppleProfileStatusText.Text = $"Could not read Credential Manager: {ex.Message}";
        }
        ShowEnrollmentSources(count);
    }

    /// <summary>The two sources of enrollment records and whether each is connected.</summary>
    private void ShowEnrollmentSources(int appleProfiles)
    {
        var config = CurrentApp?.Config;
        var graph = !string.IsNullOrWhiteSpace(config?.Graph?.TenantId);
        var off = !AppModules.IsOn(UserPreferences.HiddenModules, AppModules.Enrollment);
        EnrollmentSourcesPanel.Children.Clear();
        if (off)
            EnrollmentSourcesPanel.Children.Add(Caption("Enrollment is switched off in General, so neither source is read.", top: 0));
        EnrollmentSourcesPanel.Children.Add(SourceRow("\uE7F8", "Windows",
            graph ? "Registrations are read through the Devices connection." : "Connect Devices to read Windows registrations."));
        EnrollmentSourcesPanel.Children.Add(SourceRow("\uE8EA", "Mac, iPad and iPhone",
            appleProfiles == 0
                ? "Add an enrollment organization below."
                : $"{appleProfiles} organization{(appleProfiles == 1 ? "" : "s")} read."));
    }

    private FrameworkElement SourceRow(string glyph, string title, string detail)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var icon = Glyph(glyph, 16);
        icon.Margin = new Thickness(0, 2, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        text.Children.Add(Caption(detail, top: 1));
        row.Children.Add(text);
        return row;
    }

    private void OnBrowseAppleKey(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Private key Apple issued",
            Filter = "Private key (*.p8;*.pem)|*.p8;*.pem|All files (*.*)|*.*"
        };
        if (picker.ShowDialog() == true) AppleKeyPathBox.Text = picker.FileName;
    }

    private void OnAddAppleProfile(object sender, RoutedEventArgs e)
    {
        var name = AppleProfileNameBox.Text.Trim();
        var clientId = AppleClientIdBox.Text.Trim();
        var keyId = AppleKeyIdBox.Text.Trim();
        if (name.Length == 0 || clientId.Length == 0 || keyId.Length == 0 || !File.Exists(AppleKeyPathBox.Text))
        {
            AppleProfileStatusText.Text = "Fill in the name, client ID and key ID, and choose the private key file.";
            return;
        }
        try
        {
            _appleStore.Save(name, clientId, keyId, File.ReadAllText(AppleKeyPathBox.Text));
            var profile = new AppleOrgProfile(name, clientId);
            AppleProfileStatusText.Text = $"Saved {profile.ServiceName} profile \"{name}\". Refresh Devices to read it.";
            AppleProfileNameBox.Text = AppleClientIdBox.Text = AppleKeyIdBox.Text = AppleKeyPathBox.Text = "";
            RefreshAppleProfiles();
        }
        catch (Exception ex)
        {
            AppleProfileStatusText.Text = $"Not saved: {ex.Message}";
        }
    }

    private void OnRemoveAppleProfile(object sender, RoutedEventArgs e)
    {
        if (AppleProfilesList.SelectedItem is not AppleProfileItem item) return;
        if (MessageBox.Show($"Remove the {item.Profile.ServiceName} profile \"{item.Profile.Name}\" from Credential Manager?",
                "Remove Profile", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _appleStore.Delete(item.Profile.Name);
        AppleProfileStatusText.Text = $"Removed \"{item.Profile.Name}\".";
        RefreshAppleProfiles();
    }

    private sealed record AppleProfileItem(AppleOrgProfile Profile, string Display);
}
