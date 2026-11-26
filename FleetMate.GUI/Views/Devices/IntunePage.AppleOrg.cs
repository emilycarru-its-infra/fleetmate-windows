using System.Windows;
using FleetMate.Core.Devices;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services.Devices;

namespace FleetMate.GUI.Views.Devices;

/// <summary>
/// Apple School and Business Manager in the Devices list: every configured
/// organization is read at once after Intune (the list never waits on it),
/// and the action cards appear only when one organization holds every
/// selected device. Apple actions only ever receive serial numbers.
/// </summary>
public partial class IntunePage
{
    /// <summary>The organizations read, kept with the page like the Autopilot identities.</summary>
    private static List<AppleOrgSnapshot> _appleOrgs = new();
    private static readonly Dictionary<string, AppleOrgService> AppleServices = new();

    private async Task LoadAppleOrgsAsync()
    {
        if (!EnrollmentOn)
        {
            _appleOrgs = new();
            return;
        }
        List<AppleOrgProfile> profiles;
        try { profiles = new AppleOrgCredentialStore().Profiles(); }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Devices: could not list Apple organization profiles");
            return;
        }
        if (profiles.Count == 0) return;

        AppleLoadingText.Visibility = Visibility.Visible;
        try
        {
            // Apple's quota is per organization, so read them all at once:
            // the wait is the slowest organization, not the sum.
            var reads = profiles.Select(async p =>
            {
                try
                {
                    var service = AppleService(p.Name);
                    return service == null ? null : await service.SnapshotAsync();
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "Devices: failed to read {Service} profile {Profile}", p.ServiceName, p.Name);
                    ShowActionMessage($"{p.ServiceName}: {ex.Message}", isError: true);
                    return null;
                }
            });
            _appleOrgs = (await Task.WhenAll(reads)).OfType<AppleOrgSnapshot>().ToList();
        }
        finally
        {
            AppleLoadingText.Visibility = Visibility.Collapsed;
        }
        if (_intuneReady) RebuildRows();
    }

    private static AppleOrgService? AppleService(string profileName)
    {
        lock (AppleServices)
        {
            if (AppleServices.TryGetValue(profileName, out var cached)) return cached;
            var service = AppleOrgService.Connect(profileName);
            if (service != null) AppleServices[profileName] = service;
            return service;
        }
    }

    private AppleOrgSnapshot? OrgOf(DeviceListRow row) =>
        row.Apple == null ? null : _appleOrgs.FirstOrDefault(o => o.Profile.Name == row.Apple.OrgId);

    private AppleOrgContext? AppleContext(DeviceListRow row)
    {
        if (row.Apple is not { } device || OrgOf(row) is not { } org) return null;
        var serial = device.SerialNumber;
        return new AppleOrgContext(device, row.OrgName ?? org.Profile.ServiceName, row.ServerName,
            () => AppleService(org.Profile.Name)?.AppleCareAsync(serial) ?? Task.FromResult(new List<AppleCareAgreement>()),
            () => ReadActivationLockAsync(org.Profile.Name, serial));
    }

    /// <summary>Read once per device per session; the column shows it from then on.</summary>
    private async Task<AppleActivationLock> ReadActivationLockAsync(string profileName, string serial)
    {
        if (ActivationLockCache.Get(serial) is { } known) return known;
        var state = AppleService(profileName) is { } service
            ? await service.ActivationLockAsync(serial)
            : AppleActivationLock.Unknown;
        ActivationLockCache.Set(serial, state);
        // Rows are plain values: redraw so the optional column picks it up.
        DevicesDataGrid.Items.Refresh();
        return state;
    }

    /// <summary>Show the Apple cards <paramref name="s"/> allows for the selection.</summary>
    private void UpdateAppleSections(DeviceActionSections s, AppleOrgSnapshot? org)
    {
        static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

        AppleAssignSection.Visibility = Show(s.AppleAssign);
        AppleUnassignButton.Visibility = Show(s.AppleUnassign);
        AppleMigrationSection.Visibility = Show(s.AppleMigrate);
        AppleScheduleMigrationButton.Visibility = Show(s.AppleScheduleMigration);
        AppleUpdateDeadlineButton.Visibility = Show(s.AppleUpdateDeadline);
        AppleCancelMigrationButton.Visibility = Show(s.AppleCancelMigration);
        AppleReleaseSection.Visibility = Show(s.AppleRelease);
        AppleActionsHeader.Visibility = Show(s.AppleHeaderShown);
        AppleActionsHeader.Text = s.AppleHeader;

        if (org != null)
        {
            AppleServerCombo.ItemsSource = org.Servers;
            AppleMigrationServerCombo.ItemsSource = org.Servers;
            AppleMigrationDeadline.DisplayDateStart = DateTime.Today.AddDays(1);
            AppleMigrationDeadline.DisplayDateEnd = DateTime.Today.AddDays(AppleOrgAction.MaxMigrationDays);
        }
    }

    private async void OnAppleAssignClicked(object sender, RoutedEventArgs e)
    {
        if (AppleServerCombo.SelectedItem is not AppleOrgServer server)
        {
            ShowActionMessage("Choose a device management service.", isError: true);
            return;
        }
        await RunAppleAsync(new AppleOrgAction(AppleOrgActionKind.Assign, server.Id), $"to {server.Name}");
    }

    private async void OnAppleUnassignClicked(object sender, RoutedEventArgs e)
    {
        var serverId = SelectedRows().Select(r => r.Apple?.AssignedServerId).FirstOrDefault();
        var name = SelectedRows().FirstOrDefault()?.ServerName ?? serverId;
        await RunAppleAsync(new AppleOrgAction(AppleOrgActionKind.Unassign, serverId), $"from {name}");
    }

    private async void OnAppleScheduleMigrationClicked(object sender, RoutedEventArgs e)
    {
        if (AppleMigrationServerCombo.SelectedItem is not AppleOrgServer server || MigrationDeadline() is not { } deadline)
        {
            ShowActionMessage("Choose a destination service and a deadline within 90 days.", isError: true);
            return;
        }
        await RunAppleAsync(new AppleOrgAction(AppleOrgActionKind.ScheduleMigration, server.Id, deadline),
            $"to {server.Name} by {deadline.LocalDateTime:yyyy-MM-dd}");
    }

    private async void OnAppleUpdateDeadlineClicked(object sender, RoutedEventArgs e)
    {
        if (MigrationDeadline() is not { } deadline)
        {
            ShowActionMessage("Choose a deadline within 90 days.", isError: true);
            return;
        }
        await RunAppleAsync(new AppleOrgAction(AppleOrgActionKind.UpdateMigrationDeadline, Deadline: deadline),
            $"to {deadline.LocalDateTime:yyyy-MM-dd}");
    }

    private async void OnAppleCancelMigrationClicked(object sender, RoutedEventArgs e) =>
        await RunAppleAsync(new AppleOrgAction(AppleOrgActionKind.CancelMigration), "");

    private async void OnAppleReleaseClicked(object sender, RoutedEventArgs e) =>
        await RunAppleAsync(new AppleOrgAction(AppleOrgActionKind.Release),
            DeviceActionText.AppleReleaseDetail, MessageBoxImage.Warning);

    /// <summary>The picked deadline at the end of that local day, within Apple's 90-day cap.</summary>
    private DateTimeOffset? MigrationDeadline() =>
        DeviceActionText.MigrationDeadline(AppleMigrationDeadline.SelectedDate, DateTimeOffset.Now);

    private async Task RunAppleAsync(AppleOrgAction action, string detail, MessageBoxImage image = MessageBoxImage.Question)
    {
        var selected = SelectedRows();
        var orgName = AppleOrgJoin.SingleOrg(selected);
        if (orgName == null || AppleService(orgName) is not { } service) return;
        var serials = selected.Select(r => r.Apple!.SerialNumber).ToList();

        var what = DeviceActionText.AppleWhat(action, serials.Count, detail);
        if (MessageBox.Show($"{what}?", service.Profile.ServiceName, MessageBoxButton.YesNo, image) != MessageBoxResult.Yes) return;

        ShowActionMessage($"{what}...", isLoading: true);
        try
        {
            var result = await service.PerformAsync(action, serials);
            ShowActionMessage(DeviceActionText.AppleResult(action, result, serials.Count), isError: !result.Succeeded);
        }
        catch (Exception ex)
        {
            ShowActionMessage($"{action.Title} failed: {ex.Message}", isError: true);
            return;
        }

        // A few devices are re-read one by one; more than that re-reads the
        // whole organization, which costs fewer requests.
        if (serials.Count <= DeviceActionText.AppleRereadLimit)
        {
            var fresh = (await service.RereadAsync(serials)).ToDictionary(d => DeviceListJoin.Normalize(d.SerialNumber));
            _appleOrgs = _appleOrgs.Select(o => o.Profile.Name != orgName ? o : o with
            {
                Devices = o.Devices.Select(d => fresh.GetValueOrDefault(DeviceListJoin.Normalize(d.SerialNumber)) ?? d).ToList()
            }).ToList();
            RebuildRows();
        }
        else
        {
            _appleOrgs = new();
            await LoadAppleOrgsAsync();
        }
    }
}
