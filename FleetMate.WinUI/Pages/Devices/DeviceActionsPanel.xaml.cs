using FleetMate.Core.Devices;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Serilog;

namespace FleetMate.WinUI.Pages.Devices;

/// <summary>
/// The Device Actions panel: only the actions that reach every selected
/// device (<see cref="DeviceActionSections"/>), each confirmed before it
/// changes anything, with the progress and outcome said at the top. Intune
/// actions only ever receive Intune IDs, Autopilot's only identity IDs and
/// Apple's only serial numbers. Wording and rules come from FleetMate.Core,
/// shared with FleetMate.GUI. In a demo nothing is sent.
/// </summary>
public sealed partial class DeviceActionsPanel : UserControl
{
    private DevicesPage? _page;
    private List<MobileApp> _apps = new();

    public DeviceActionsPanel()
    {
        InitializeComponent();
        WipeObliterationCombo.ItemsSource = WipeOptions.ObliterationBehaviors
            .Select(b => new ComboBoxItem { Content = WipeOptions.ObliterationDisplayName(b), Tag = b }).ToList();
        WipeObliterationCombo.SelectedIndex = 0;
        OffboardTerminalCombo.ItemsSource = Enum.GetValues<OffboardTerminalAction>()
            .Select(a => new ComboBoxItem { Content = OffboardPlan.DisplayName(a), Tag = a }).ToList();
        OffboardTerminalCombo.SelectedIndex = 0;
        OffboardEntraCombo.ItemsSource = Enum.GetValues<OffboardEntraAction>()
            .Select(a => new ComboBoxItem { Content = OffboardPlan.DisplayName(a), Tag = a }).ToList();
        OffboardEntraCombo.SelectedIndex = 0;
        AppleMigrationDeadline.MinDate = DateTimeOffset.Now.Date.AddDays(1);
        AppleMigrationDeadline.MaxDate = DateTimeOffset.Now.Date.AddDays(AppleOrgAction.MaxMigrationDays);
    }

    public void Attach(DevicesPage page) => _page = page;

    /// <summary>Raised by the panel's close button.</summary>
    public event EventHandler? CloseRequested;

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private GraphService? Graph => App.Services.Graph;
    private IReadOnlyList<DeviceListRow> Selected => _page?.ViewModel.Selected ?? Array.Empty<DeviceListRow>();

    // MARK: - Sections

    /// <summary>Show the selection's summary and the cards that apply to every selected device.</summary>
    public void Show(IReadOnlyList<DeviceListRow> selected, DeviceActionSections s, AppleOrgSnapshot? org)
    {
        SelectedTitle.Text = DeviceListText.SelectedTitle(selected.Count);
        SelectedNames.Text = DeviceListText.SelectedNames(selected);

        IntuneSections.Visibility = DeviceUi.Show(s.Intune);
        IntuneMoreSections.Visibility = DeviceUi.Show(s.Intune);
        WindowsSections.Visibility = DeviceUi.Show(s.WindowsIntune);
        PushCimianSection.Visibility = DeviceUi.Show(s.WindowsIntune);

        AutopilotGroupTagSection.Visibility = DeviceUi.Show(s.AutopilotGroupTag);
        AutopilotAssignUserSection.Visibility = DeviceUi.Show(s.AutopilotAssignUser);
        AutopilotUnassignUserSection.Visibility = DeviceUi.Show(s.AutopilotUnassignUser);
        AutopilotSyncSection.Visibility = DeviceUi.Show(s.AutopilotSync);
        AutopilotDeleteSection.Visibility = DeviceUi.Show(s.AutopilotDelete);
        AutopilotHeader.Visibility = DeviceUi.Show(s.AutopilotHeaderShown);

        AppleAssignSection.Visibility = DeviceUi.Show(s.AppleAssign);
        AppleUnassignButton.Visibility = DeviceUi.Show(s.AppleUnassign);
        AppleMigrationSection.Visibility = DeviceUi.Show(s.AppleMigrate);
        AppleScheduleMigrationButton.Visibility = DeviceUi.Show(s.AppleScheduleMigration);
        AppleUpdateDeadlineButton.Visibility = DeviceUi.Show(s.AppleUpdateDeadline);
        AppleCancelMigrationButton.Visibility = DeviceUi.Show(s.AppleCancelMigration);
        AppleReleaseSection.Visibility = DeviceUi.Show(s.AppleRelease);
        AppleHeader.Visibility = DeviceUi.Show(s.AppleHeaderShown);
        AppleHeader.Text = s.AppleHeader;
        if (org != null)
        {
            AppleServerCombo.ItemsSource = org.Servers;
            AppleMigrationServerCombo.ItemsSource = org.Servers;
        }

        IntuneHeader.Visibility = DeviceUi.Show(s.IntuneHeaderShown);
        NoActionsText.Visibility = DeviceUi.Show(s.NoActions);

        WipeMixedText.Visibility = DeviceUi.Show(s.WipeMixedPlatforms);
        WipeKeepUserDataBox.Visibility = DeviceUi.Show(s.WipeWindowsOptions);
        WipeProtectedBox.Visibility = DeviceUi.Show(s.WipeWindowsOptions);
        WipeUnlockCodeBox.Visibility = DeviceUi.Show(s.WipeUnlockCode);
        WipeObliterationCombo.Visibility = DeviceUi.Show(s.WipeObliteration);
        OffboardSection.Visibility = DeviceUi.Show(s.Offboard);
        UpdateOffboardWarning();
    }

    /// <summary>Open every card, for the QA capture.</summary>
    internal void ExpandAll()
    {
        foreach (var expander in FindExpanders(this)) expander.IsExpanded = true;
    }

    private static IEnumerable<Expander> FindExpanders(DependencyObject root)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Expander e) yield return e;
            foreach (var nested in FindExpanders(child)) yield return nested;
        }
    }

    // MARK: - Messages

    /// <summary>Say how an action is going: running, done, or failed.</summary>
    public void ShowMessage(string message, bool isError = false, bool isLoading = false)
    {
        Message.Message = message;
        Message.Severity = isError ? InfoBarSeverity.Error : isLoading ? InfoBarSeverity.Informational : InfoBarSeverity.Success;
        MessageProgress.Visibility = DeviceUi.Show(isLoading);
        Message.IsOpen = true;
    }

    private bool Demo(string title)
    {
        if (!App.Services.DemoMode) return false;
        ShowMessage(DeviceActionText.Demo(title));
        return true;
    }

    private Task<bool> ConfirmAsync(DeviceActionConfirm confirm, bool destructive = true) =>
        DeviceUi.ConfirmAsync(XamlRoot, confirm, destructive);

    private List<string> IntuneIds() => Selected.Where(r => r.Intune != null).Select(r => r.Intune!.Id).ToList();

    /// <summary>
    /// Run one Intune action on the selection, confirmed first when it changes
    /// a device, and say how many it reached.
    /// </summary>
    private async Task RunAsync(DeviceActionKind kind, string title, IReadOnlyList<string> ids,
        Func<GraphService, IReadOnlyList<string>, Task<int>> run, bool keepUserData = false, string? appName = null)
    {
        if (ids.Count == 0) return;
        if (DeviceActionText.Confirm(kind, ids.Count, keepUserData) is { } confirm && !await ConfirmAsync(confirm)) return;
        if (Demo(title) || Graph is not { } graph) return;
        ShowMessage(DeviceActionText.Progress(kind, ids.Count), isLoading: true);
        try
        {
            var succeeded = await run(graph, ids);
            var failed = ids.Count - succeeded;
            ShowMessage(DeviceActionText.Result(kind, succeeded, failed, appName), isError: failed > 0);
        }
        catch (Exception ex)
        {
            ShowMessage($"Error: {ex.Message}", isError: true);
        }
    }

    private static int Succeeded(IEnumerable<GraphService.DeviceActionResult> results) => results.Count(r => r.Success);

    // MARK: - Intune

    private async void Sync_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(DeviceActionKind.Sync, "Sync", IntuneIds(), async (g, ids) => Succeeded(await g.SyncDevicesAsync(ids)));

    private async void Restart_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(DeviceActionKind.Restart, "Restart", IntuneIds(), async (g, ids) => Succeeded(await g.RebootDevicesAsync(ids)));

    private async void Retire_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(DeviceActionKind.Retire, "Retire", IntuneIds(),
            async (g, ids) => Succeeded(await g.RetireDevicesAsync(ids, confirmed: true)));

    private async void Lock_Click(object sender, RoutedEventArgs e)
    {
        var pin = string.IsNullOrWhiteSpace(LockPinBox.Password) ? null : LockPinBox.Password;
        await RunAsync(DeviceActionKind.Lock, "Lock", IntuneIds(),
            async (g, ids) => Succeeded(await g.RemoteLockDevicesAsync(ids, pin, confirmed: true)));
        LockPinBox.Password = "";
    }

    private async void Wipe_Click(object sender, RoutedEventArgs e)
    {
        // Wipe takes device records, not bare ids, so each body is built for its own platform.
        var devices = Selected.Where(r => r.Intune != null).Select(r => r.Intune!).ToList();
        var options = CurrentWipeOptions();
        await RunAsync(DeviceActionKind.Wipe, "Wipe", devices.Select(d => d.Id).ToList(),
            async (g, _) => Succeeded(await g.WipeDevicesAsync(devices, options, confirmed: true)), options.KeepUserData);
    }

    private async void FreshStart_Click(object sender, RoutedEventArgs e)
    {
        // Windows only: run against the Windows subset rather than refusing a mixed selection.
        var targets = Selected.Where(r => r.Intune != null && r.IsWindows).Select(r => r.Intune!.Id).ToList();
        if (targets.Count == 0)
        {
            ShowMessage(DeviceActionText.FreshStartNoWindows, isError: true);
            return;
        }
        var keep = FreshStartKeepUserDataBox.IsChecked == true;
        await RunAsync(DeviceActionKind.FreshStart, "Fresh Start", targets,
            async (g, ids) => Succeeded(await g.FreshStartDevicesAsync(ids, keep, confirmed: true)), keep);
    }

    private async void AutopilotReset_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(DeviceActionKind.AutopilotReset, "Autopilot Reset", IntuneIds(), async (g, ids) =>
        {
            var ok = 0;
            foreach (var id in ids) if ((await g.AutopilotResetDeviceAsync(id, confirmed: true)).Success) ok++;
            return ok;
        });

    private async void DeleteRecord_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(DeviceActionKind.DeleteRecord, "Delete Record", IntuneIds(), async (g, ids) =>
        {
            var ok = 0;
            foreach (var id in ids) if ((await g.DeleteManagedDeviceAsync(id, confirmed: true)).Success) ok++;
            return ok;
        });

    private async void AppSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (App.Services.DemoMode)
        {
            _apps = new List<MobileApp> { new() { Id = "demo-app", DisplayName = "Demo Editor" } };
            AppCombo.ItemsSource = _apps;
            return;
        }
        if (Graph is not { } graph) return;
        try
        {
            var query = sender.Text?.Trim();
            _apps = string.IsNullOrEmpty(query) ? await graph.GetMobileAppsAsync(limit: 100) : await graph.SearchMobileAppsAsync(query, 50);
            AppCombo.ItemsSource = _apps;
        }
        catch (Exception ex)
        {
            ShowMessage($"Error loading apps: {ex.Message}", isError: true);
        }
    }

    private async void ReinstallApp_Click(object sender, RoutedEventArgs e)
    {
        if (AppCombo.SelectedItem is not MobileApp app)
        {
            ShowMessage("Please select an app to reinstall", isError: true);
            return;
        }
        // Reinstall is triggered by a sync.
        await RunAsync(DeviceActionKind.ReinstallApp, "Reinstall", IntuneIds(),
            async (g, ids) => Succeeded(await g.SyncDevicesAsync(ids)), appName: app.DisplayName ?? "app");
    }

    private async void OsUpdate_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(DeviceActionKind.OsUpdate, "Update check", IntuneIds(), async (g, ids) => Succeeded(await g.SyncDevicesAsync(ids)));

    private async void PushCimian_Click(object sender, RoutedEventArgs e)
    {
        var ids = IntuneIds();
        if (ids.Count == 0)
        {
            ShowMessage("Select devices to push Cimian run", isError: true);
            return;
        }
        // A forced sync makes the remediation pick up the trigger on check-in.
        await RunAsync(DeviceActionKind.PushCimian, "Cimian push", ids, async (g, list) => Succeeded(await g.SyncDevicesAsync(list)));
    }

    // MARK: - Wipe and Offboard

    private WipeOptions CurrentWipeOptions() => new()
    {
        KeepEnrollmentData = WipeKeepEnrollmentBox.IsChecked == true,
        KeepUserData = WipeKeepUserDataBox.Visibility == Visibility.Visible && WipeKeepUserDataBox.IsChecked == true,
        UseProtectedWipe = WipeProtectedBox.Visibility == Visibility.Visible && WipeProtectedBox.IsChecked == true,
        MacOsUnlockCode = string.IsNullOrWhiteSpace(WipeUnlockCodeBox.Password) ? null : WipeUnlockCodeBox.Password.Trim(),
        // "default" is Graph's own default, so it's left out of the request.
        ObliterationBehavior = (WipeObliterationCombo.SelectedItem as ComboBoxItem)?.Tag is string b && b != "default" ? b : null,
    };

    private OffboardPlan CurrentOffboardPlan() => new()
    {
        TerminalAction = (OffboardTerminalCombo.SelectedItem as ComboBoxItem)?.Tag is OffboardTerminalAction t ? t : OffboardTerminalAction.Wipe,
        WipeOptions = CurrentWipeOptions(),
        DeleteAutopilotRegistration = OffboardDeleteAutopilotBox.IsChecked == true,
        EntraAction = (OffboardEntraCombo.SelectedItem as ComboBoxItem)?.Tag is OffboardEntraAction a ? a : OffboardEntraAction.None,
        DeleteIntuneRecord = OffboardDeleteIntuneBox.IsChecked == true,
    };

    private void OffboardPlan_Changed(object sender, RoutedEventArgs e) => UpdateOffboardWarning();

    private void UpdateOffboardWarning()
    {
        if (OffboardTerminalCombo == null || OffboardWarning == null) return;
        var plan = CurrentOffboardPlan();
        OffboardUsesWipeOptionsText.Visibility = DeviceUi.Show(plan.TerminalAction == OffboardTerminalAction.Wipe);
        var warning = DeviceActionText.OffboardWarning(plan);
        OffboardWarning.Message = warning ?? "";
        OffboardWarning.IsOpen = warning != null;
    }

    private async void Offboard_Click(object sender, RoutedEventArgs e)
    {
        var rows = Selected.Where(r => r.IsEnrolled || r.Autopilot != null).ToList();
        var plan = CurrentOffboardPlan();
        if (rows.Count == 0) return;
        if (!await ConfirmAsync(new DeviceActionConfirm("Confirm Offboard", plan.Summary(rows.Count)))) return;
        if (Demo("Offboard") || Graph is not { } graph) return;

        ShowMessage(DeviceActionText.Progress(DeviceActionKind.Offboard, rows.Count), isLoading: true);
        OffboardResults.Children.Clear();
        var offboarder = new DeviceOffboarder(graph);
        var results = new List<OffboardResult>();
        foreach (var row in rows)
        {
            try
            {
                results.Add(row.Intune is { } device
                    ? await offboarder.OffboardAsync(device, plan)
                    : await offboarder.OffboardOrphanAsync(row.SerialText, await OrphanRecordsAsync(graph, row), plan));
            }
            catch (Exception ex)
            {
                results.Add(new OffboardResult(row.SerialText, row.NameText, DevicePlatform.Other,
                    new[] { new OffboardStepResult("Offboard", OffboardOutcome.Failed, ex.Message) }));
            }
        }
        foreach (var result in results.OrderBy(r => r.DeviceName ?? r.Identifier, StringComparer.OrdinalIgnoreCase))
            OffboardResults.Children.Add(ResultBlock(result));
        var failed = results.Count(r => !r.Success);
        ShowMessage(DeviceActionText.Result(DeviceActionKind.Offboard, results.Count - failed, failed), isError: failed > 0);
    }

    /// <summary>The directory records of a device Intune no longer has, from the Autopilot identity the row carries.</summary>
    private static async Task<OrphanDeviceRecords> OrphanRecordsAsync(GraphService graph, DeviceListRow row)
    {
        var entraId = AutopilotLabels.Linked(row.Autopilot?.AzureActiveDirectoryDeviceId);
        var entra = entraId == null ? null : await graph.GetEntraDeviceByDeviceIdAsync(entraId);
        return new OrphanDeviceRecords(row.Autopilot, entra);
    }

    private static FrameworkElement ResultBlock(OffboardResult result)
    {
        var block = new StackPanel { Spacing = 2 };
        block.Children.Add(new TextBlock
        {
            Text = result.DeviceName ?? result.Identifier,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        foreach (var step in result.Steps)
        {
            var (glyph, tone) = step.Outcome switch
            {
                OffboardOutcome.Succeeded => ("", DeviceTone.Good),
                OffboardOutcome.Failed => ("", DeviceTone.Attention),
                _ => ("", DeviceTone.Neutral),
            };
            var line = new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], TextWrapping = TextWrapping.Wrap };
            line.Inlines.Add(new Run { Text = glyph + " ", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"), Foreground = DeviceUi.ToneBrush(tone) });
            line.Inlines.Add(new Run { Text = step.Display });
            block.Children.Add(line);
        }
        return block;
    }

    // MARK: - Autopilot

    private List<AutopilotDevice> SelectedIdentities() => Selected.Where(r => r.Autopilot != null).Select(r => r.Autopilot!).ToList();

    private async void AutopilotSetGroupTag_Click(object sender, RoutedEventArgs e)
    {
        var tag = AutopilotGroupTagBox.Text.Trim();
        await RunAutopilotAsync(AutopilotAction.SetGroupTag, tag, (g, id) => g.SetAutopilotGroupTagAsync(id.Id, tag));
    }

    private async void AutopilotAssignUser_Click(object sender, RoutedEventArgs e)
    {
        var upn = AutopilotUserBox.Text.Trim();
        if (!DeviceActionText.IsUserPrincipalName(upn))
        {
            ShowMessage("Enter the user's principal name (user@domain).", isError: true);
            return;
        }
        await RunAutopilotAsync(AutopilotAction.AssignUser, upn, (g, id) => g.AssignAutopilotUserAsync(id.Id, upn));
    }

    private async void AutopilotUnassignUser_Click(object sender, RoutedEventArgs e) =>
        await RunAutopilotAsync(AutopilotAction.UnassignUser, null, (g, id) => g.UnassignAutopilotUserAsync(id.Id));

    private async void AutopilotDelete_Click(object sender, RoutedEventArgs e) =>
        await RunAutopilotAsync(AutopilotAction.Delete, null, (g, id) => g.DeleteAutopilotIdentityAsync(id.Id, confirmed: true));

    private async void AutopilotSync_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(DeviceActionText.AutopilotConfirm(AutopilotAction.Sync, 0), destructive: false)) return;
        if (Demo("Autopilot sync") || Graph is not { } graph) return;
        ShowMessage("Syncing Autopilot...", isLoading: true);
        var result = await graph.SyncAutopilotAsync();
        ShowMessage(result.Success ? "Autopilot sync requested." : $"Autopilot sync failed: {result.Message}", isError: !result.Success);
    }

    private async Task RunAutopilotAsync(AutopilotAction action, string? value,
        Func<GraphService, AutopilotDevice, Task<GraphService.DeviceActionResult>> run)
    {
        var identities = SelectedIdentities();
        if (identities.Count == 0) return;
        if (!await ConfirmAsync(DeviceActionText.AutopilotConfirm(action, identities.Count, value), destructive: action == AutopilotAction.Delete)) return;
        var verb = DeviceActionText.AutopilotVerb(action);
        if (Demo(verb) || Graph is not { } graph) return;

        ShowMessage($"{verb} on {identities.Count} device(s)...", isLoading: true);
        var failures = new List<string>();
        foreach (var identity in identities)
        {
            var result = await run(graph, identity);
            if (!result.Success) failures.Add(result.Message ?? "failed");
        }
        ShowMessage(DeviceActionText.AutopilotResult(verb, identities.Count, failures), isError: failures.Count > 0);
        if (_page != null) await _page.ReloadAutopilotAsync();
    }

    // MARK: - Apple organization

    private async void AppleAssign_Click(object sender, RoutedEventArgs e)
    {
        if (AppleServerCombo.SelectedItem is not AppleOrgServer server)
        {
            ShowMessage("Choose a device management service.", isError: true);
            return;
        }
        await RunAppleAsync(new AppleOrgAction(AppleOrgActionKind.Assign, server.Id), $"to {server.Name}");
    }

    private async void AppleUnassign_Click(object sender, RoutedEventArgs e)
    {
        var serverId = Selected.Select(r => r.Apple?.AssignedServerId).FirstOrDefault();
        var name = Selected.FirstOrDefault()?.ServerName ?? serverId;
        await RunAppleAsync(new AppleOrgAction(AppleOrgActionKind.Unassign, serverId), $"from {name}");
    }

    private DateTimeOffset? MigrationDeadline() =>
        DeviceActionText.MigrationDeadline(AppleMigrationDeadline.Date?.Date, DateTimeOffset.Now);

    private async void AppleScheduleMigration_Click(object sender, RoutedEventArgs e)
    {
        if (AppleMigrationServerCombo.SelectedItem is not AppleOrgServer server || MigrationDeadline() is not { } deadline)
        {
            ShowMessage("Choose a destination service and a deadline within 90 days.", isError: true);
            return;
        }
        await RunAppleAsync(new AppleOrgAction(AppleOrgActionKind.ScheduleMigration, server.Id, deadline),
            $"to {server.Name} by {deadline.LocalDateTime:yyyy-MM-dd}");
    }

    private async void AppleUpdateDeadline_Click(object sender, RoutedEventArgs e)
    {
        if (MigrationDeadline() is not { } deadline)
        {
            ShowMessage("Choose a deadline within 90 days.", isError: true);
            return;
        }
        await RunAppleAsync(new AppleOrgAction(AppleOrgActionKind.UpdateMigrationDeadline, Deadline: deadline), $"to {deadline.LocalDateTime:yyyy-MM-dd}");
    }

    private async void AppleCancelMigration_Click(object sender, RoutedEventArgs e) =>
        await RunAppleAsync(new AppleOrgAction(AppleOrgActionKind.CancelMigration), "");

    private async void AppleRelease_Click(object sender, RoutedEventArgs e) =>
        await RunAppleAsync(new AppleOrgAction(AppleOrgActionKind.Release), DeviceActionText.AppleReleaseDetail, destructive: true);

    private async Task RunAppleAsync(AppleOrgAction action, string detail, bool destructive = false)
    {
        var selected = Selected.ToList();
        var orgName = AppleOrgJoin.SingleOrg(selected);
        if (orgName == null) return;
        var serials = selected.Select(r => r.Apple!.SerialNumber).ToList();
        var what = DeviceActionText.AppleWhat(action, serials.Count, detail);
        var serviceName = App.Services.CachedAppleOrgs.FirstOrDefault(o => o.Profile.Name == orgName)?.Profile.ServiceName ?? "Apple";
        if (!await ConfirmAsync(new DeviceActionConfirm(serviceName, $"{what}?"), destructive)) return;
        if (Demo(action.Title) || App.Services.AppleService(orgName) is not { } service) return;

        ShowMessage($"{what}...", isLoading: true);
        try
        {
            var result = await service.PerformAsync(action, serials);
            ShowMessage(DeviceActionText.AppleResult(action, result, serials.Count), isError: !result.Succeeded);
        }
        catch (Exception ex)
        {
            ShowMessage($"{action.Title} failed: {ex.Message}", isError: true);
            return;
        }
        try
        {
            if (_page != null) await _page.RereadAppleAsync(orgName, serials);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Devices: could not re-read the Apple organization after {Action}", action.Title);
        }
    }
}
