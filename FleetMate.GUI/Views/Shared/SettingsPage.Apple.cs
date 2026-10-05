using System.IO;
using System.Windows;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services.Devices;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// Settings › Apple: add and remove Apple School and Business Manager API
/// profiles. The key ID and private key go straight into Windows Credential
/// Manager; the key file itself is not kept or copied anywhere.
/// </summary>
public partial class SettingsPage
{
    private readonly AppleOrgCredentialStore _appleStore = new();

    private void OnAppleTabLoaded(object sender, RoutedEventArgs e) => RefreshAppleProfiles();

    private void RefreshAppleProfiles()
    {
        try
        {
            var profiles = _appleStore.Profiles();
            var labels = AppleOrgProfile.Labels(profiles);
            AppleProfilesList.ItemsSource = profiles.Select(p => new AppleProfileItem(p, $"{labels[p.Name]} — {p.Name}")).ToList();
            AppleProfilesList.DisplayMemberPath = nameof(AppleProfileItem.Display);
            AppleProfilesEmptyText.Visibility = profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppleProfileStatusText.Text = $"Could not read Credential Manager: {ex.Message}";
        }
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
