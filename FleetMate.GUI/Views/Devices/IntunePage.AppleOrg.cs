using System.Windows;
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

    /// <summary>Show the Apple cards that fit every selected device; true when any does.</summary>
    private bool UpdateAppleSections(List<DeviceListRow> selected)
    {
        var orgName = AppleOrgJoin.SingleOrg(selected);
        var org = orgName == null ? null : _appleOrgs.FirstOrDefault(o => o.Profile.Name == orgName);
        var profile = org?.Profile;
        bool Can(AppleOrgActionKind kind) => AppleOrgJoin.IsAvailable(kind, selected, profile);
        static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

        var assign = Can(AppleOrgActionKind.Assign);
        var migrate = Can(AppleOrgActionKind.ScheduleMigration) || Can(AppleOrgActionKind.UpdateMigrationDeadline);
        var release = Can(AppleOrgActionKind.Release);

        AppleAssignSection.Visibility = Show(assign);
        AppleUnassignButton.Visibility = Show(Can(AppleOrgActionKind.Unassign));
        AppleMigrationSection.Visibility = Show(migrate);
        AppleScheduleMigrationButton.Visibility = Show(Can(AppleOrgActionKind.ScheduleMigration));
        AppleUpdateDeadlineButton.Visibility = Show(Can(AppleOrgActionKind.UpdateMigrationDeadline));
        AppleCancelMigrationButton.Visibility = Show(Can(AppleOrgActionKind.CancelMigration));
        AppleReleaseSection.Visibility = Show(release);
        AppleActionsHeader.Visibility = Show(assign || migrate || release);
        if (profile != null) AppleActionsHeader.Text = selected[0].OrgName ?? profile.ServiceName;

        if (org != null)
        {
            AppleServerCombo.ItemsSource = org.Servers;
            AppleMigrationServerCombo.ItemsSource = org.Servers;
            AppleMigrationDeadline.DisplayDateStart = DateTime.Today.AddDays(1);
            AppleMigrationDeadline.DisplayDateEnd = DateTime.Today.AddDays(AppleOrgAction.MaxMigrationDays);
        }
        return assign || migrate || release;
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
            "— they leave the organization and lose their enrollment assignment. This cannot be undone", MessageBoxImage.Warning);

    /// <summary>The picked deadline at the end of that local day, within Apple's 90-day cap.</summary>
    private DateTimeOffset? MigrationDeadline()
    {
        if (AppleMigrationDeadline.SelectedDate is not { } day) return null;
        var deadline = new DateTimeOffset(day.Date.AddHours(23).AddMinutes(59));
        return deadline > DateTimeOffset.Now && deadline <= AppleOrgAction.LatestDeadline(DateTimeOffset.Now) ? deadline : null;
    }

    private async Task RunAppleAsync(AppleOrgAction action, string detail, MessageBoxImage image = MessageBoxImage.Question)
    {
        var selected = SelectedRows();
        var orgName = AppleOrgJoin.SingleOrg(selected);
        if (orgName == null || AppleService(orgName) is not { } service) return;
        var serials = selected.Select(r => r.Apple!.SerialNumber).ToList();

        var what = $"{action.Title}: {serials.Count} device{(serials.Count == 1 ? "" : "s")} {detail}".TrimEnd();
        if (MessageBox.Show($"{what}?", service.Profile.ServiceName, MessageBoxButton.YesNo, image) != MessageBoxResult.Yes) return;

        ShowActionMessage($"{what}...", isLoading: true);
        try
        {
            var result = await service.PerformAsync(action, serials);
            ShowActionMessage(result.Succeeded
                    ? $"{action.Title}: done on {serials.Count} device(s)."
                    : $"{action.Title}: Apple reports {result.Status} (activity {result.ActivityId}).",
                isError: !result.Succeeded);
        }
        catch (Exception ex)
        {
            ShowActionMessage($"{action.Title} failed: {ex.Message}", isError: true);
            return;
        }

        // A few devices are re-read one by one; more than that re-reads the
        // whole organization, which costs fewer requests.
        if (serials.Count <= 15)
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
