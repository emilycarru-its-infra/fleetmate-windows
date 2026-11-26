using FleetMate.Core.Models.Devices;

namespace FleetMate.Core.Devices;

/// <summary>The Intune actions in the Device Actions panel, in the panel's order.</summary>
public enum DeviceActionKind
{
    Sync, Restart, Lock, Retire, Wipe, FreshStart, AutopilotReset, Offboard, DeleteRecord, ReinstallApp, OsUpdate, PushCimian,
}

/// <summary>
/// Which cards the Device Actions panel shows for a selection: only the
/// actions that reach every selected device. Intune's need every device
/// enrolled; Fresh Start, Autopilot Reset and Cimian need every device to be
/// Windows; Autopilot's need every device registered; Apple's need one
/// organization to hold every device. Shared by both Windows apps.
/// </summary>
public sealed record DeviceActionSections
{
    /// <summary>Sync, Restart, Lock, Retire, Wipe, Delete Record, Reinstall App and OS Update.</summary>
    public bool Intune { get; init; }
    /// <summary>Fresh Start, Autopilot Reset and Push Cimian Run.</summary>
    public bool WindowsIntune { get; init; }

    public bool AutopilotGroupTag { get; init; }
    public bool AutopilotAssignUser { get; init; }
    public bool AutopilotUnassignUser { get; init; }
    public bool AutopilotSync { get; init; }
    public bool AutopilotDelete { get; init; }

    public bool AppleAssign { get; init; }
    public bool AppleUnassign { get; init; }
    public bool AppleMigrate { get; init; }
    public bool AppleScheduleMigration { get; init; }
    public bool AppleUpdateDeadline { get; init; }
    public bool AppleCancelMigration { get; init; }
    public bool AppleRelease { get; init; }
    /// <summary>The Apple heading: the organization's name, else its service ("Apple School Manager").</summary>
    public string AppleHeader { get; init; } = "Apple Organization";

    public bool AppleHeaderShown => AppleAssign || AppleMigrate || AppleRelease;
    public bool AutopilotHeaderShown => AutopilotSync;
    /// <summary>The Intune heading shows when another service's cards sit above it.</summary>
    public bool IntuneHeaderShown => (AutopilotHeaderShown || AppleHeaderShown) && Intune;
    public bool NoActions => !Intune && !AutopilotHeaderShown && !AppleHeaderShown;

    /// <summary>The selection spans more than one platform, so each device gets only the wipe options its platform takes.</summary>
    public bool WipeMixedPlatforms { get; init; }
    /// <summary>Keep user data and protected wipe: Windows only.</summary>
    public bool WipeWindowsOptions { get; init; }
    /// <summary>The recovery PIN: macOS and iOS.</summary>
    public bool WipeUnlockCode { get; init; }
    /// <summary>The erase behaviour: macOS.</summary>
    public bool WipeObliteration { get; init; }

    /// <summary>An enrolled device takes the whole pass; a registered-only device takes the directory steps.</summary>
    public bool Offboard { get; init; }

    public static DeviceActionSections For(IReadOnlyList<DeviceListRow> selected, AppleOrgProfile? appleProfile)
    {
        var allEnrolled = selected.Count > 0 && selected.All(r => r.IsEnrolled);
        var allWindows = selected.All(r => r.IsWindows);
        var identities = selected.Select(r => r.Autopilot).ToList();
        bool Apple(AppleOrgActionKind kind) => AppleOrgJoin.IsAvailable(kind, selected, appleProfile);

        var platforms = selected.Where(r => r.Intune != null)
            .Select(r => DevicePlatforms.From(r.Intune!.OperatingSystem)).ToHashSet();

        return new DeviceActionSections
        {
            Intune = allEnrolled,
            WindowsIntune = allEnrolled && allWindows,
            AutopilotGroupTag = AutopilotAction.SetGroupTag.IsAvailable(identities),
            AutopilotAssignUser = AutopilotAction.AssignUser.IsAvailable(identities),
            AutopilotUnassignUser = AutopilotAction.UnassignUser.IsAvailable(identities),
            AutopilotSync = AutopilotAction.Sync.IsAvailable(identities),
            AutopilotDelete = AutopilotAction.Delete.IsAvailable(identities),
            AppleAssign = Apple(AppleOrgActionKind.Assign),
            AppleUnassign = Apple(AppleOrgActionKind.Unassign),
            AppleScheduleMigration = Apple(AppleOrgActionKind.ScheduleMigration),
            AppleUpdateDeadline = Apple(AppleOrgActionKind.UpdateMigrationDeadline),
            AppleMigrate = Apple(AppleOrgActionKind.ScheduleMigration) || Apple(AppleOrgActionKind.UpdateMigrationDeadline),
            AppleCancelMigration = Apple(AppleOrgActionKind.CancelMigration),
            AppleRelease = Apple(AppleOrgActionKind.Release),
            AppleHeader = appleProfile == null ? "Apple Organization" : selected.FirstOrDefault()?.OrgName ?? appleProfile.ServiceName,
            WipeMixedPlatforms = platforms.Count > 1,
            WipeWindowsOptions = platforms.Contains(DevicePlatform.Windows),
            WipeUnlockCode = platforms.Contains(DevicePlatform.MacOS) || platforms.Contains(DevicePlatform.IOS),
            WipeObliteration = platforms.Contains(DevicePlatform.MacOS),
            Offboard = selected.Count > 0 && selected.All(r => r.IsEnrolled || r.Autopilot != null),
        };
    }
}

/// <summary>A confirmation to show before an action: its title and question.</summary>
public sealed record DeviceActionConfirm(string Title, string Message);

/// <summary>
/// What the Device Actions panel says before, during and after each action,
/// worded as on the Mac. Both Windows apps show these.
/// </summary>
public static class DeviceActionText
{
    /// <summary>The question asked before an action, or null for one that runs at once (Sync, Reinstall, OS Update, Cimian).</summary>
    public static DeviceActionConfirm? Confirm(DeviceActionKind kind, int count, bool keepUserData = false) => kind switch
    {
        DeviceActionKind.Restart => new("Confirm Restart",
            $"Are you sure you want to restart {count} device(s)? Active user sessions will be terminated."),
        DeviceActionKind.Lock => new("Confirm Lock", $"Are you sure you want to lock {count} device(s)?"),
        DeviceActionKind.Retire => new("Confirm Retire",
            $"Remove company data and unenroll {count} device(s)? Personal data is left intact."),
        DeviceActionKind.Wipe => new("Confirm Wipe",
            $"This will factory-reset {count} device(s){(keepUserData ? ", keeping user data where the platform allows" : ", erasing all data")}. This cannot be undone."),
        DeviceActionKind.FreshStart => new("Confirm Fresh Start",
            $"This will reinstall Windows on {count} device(s){(keepUserData ? ", preserving user data" : ", removing user data")}. Preinstalled OEM apps are removed and the device stays enrolled."),
        DeviceActionKind.AutopilotReset => new("Confirm Autopilot Reset",
            $"Autopilot-reset {count} device(s)? Apps and settings are removed; enrollment is kept and the device re-provisions."),
        DeviceActionKind.DeleteRecord => new("Confirm Delete Record",
            $"Delete the Intune record for {count} device(s)? This is server-side only and cannot be undone from here."),
        _ => null,
    };

    /// <summary>The line shown while the action runs.</summary>
    public static string Progress(DeviceActionKind kind, int count) => kind switch
    {
        DeviceActionKind.Sync => $"Syncing {count} device(s)...",
        DeviceActionKind.Restart => $"Restarting {count} device(s)...",
        DeviceActionKind.Lock => $"Locking {count} device(s)...",
        DeviceActionKind.Retire => $"Retiring {count} device(s)...",
        DeviceActionKind.Wipe => $"Wiping {count} device(s)...",
        DeviceActionKind.FreshStart => $"Starting Fresh Start on {count} device(s)...",
        DeviceActionKind.AutopilotReset => $"Autopilot-resetting {count} device(s)...",
        DeviceActionKind.Offboard => $"Offboarding {count} device(s)...",
        DeviceActionKind.DeleteRecord => $"Deleting {count} record(s)...",
        DeviceActionKind.ReinstallApp => $"Triggering reinstall on {count} device(s)...",
        DeviceActionKind.OsUpdate => $"Checking updates on {count} device(s)...",
        _ => $"Pushing Cimian run to {count} device(s)...",
    };

    /// <summary>The line shown when the action finishes: how many it reached and how many failed.</summary>
    public static string Result(DeviceActionKind kind, int succeeded, int failed, string? appName = null) => kind switch
    {
        DeviceActionKind.Sync => failed == 0 ? $"Successfully synced {succeeded} device(s)" : $"Synced {succeeded}, {failed} failed",
        DeviceActionKind.Restart => failed == 0 ? $"Successfully sent reboot to {succeeded} device(s)" : $"Rebooted {succeeded}, {failed} failed",
        DeviceActionKind.Lock => failed == 0 ? $"Successfully locked {succeeded} device(s)" : $"Locked {succeeded}, {failed} failed",
        DeviceActionKind.Retire => failed == 0 ? $"Successfully sent retire to {succeeded} device(s)" : $"Retired {succeeded}, {failed} failed",
        DeviceActionKind.Wipe => failed == 0 ? $"Successfully sent wipe to {succeeded} device(s)" : $"Wiped {succeeded}, {failed} failed",
        DeviceActionKind.FreshStart => failed == 0 ? $"Fresh Start sent to {succeeded} device(s)" : $"Fresh Start sent to {succeeded}, {failed} failed",
        DeviceActionKind.AutopilotReset => failed == 0 ? $"Autopilot reset sent to {succeeded} device(s)" : $"Autopilot reset sent to {succeeded}, {failed} failed",
        DeviceActionKind.Offboard => failed == 0
            ? $"Offboarded {succeeded + failed} device(s)"
            : $"Offboarded {succeeded} device(s), {failed} with failures",
        DeviceActionKind.DeleteRecord => failed == 0 ? $"Deleted {succeeded} record(s)" : $"Deleted {succeeded}, {failed} failed",
        DeviceActionKind.ReinstallApp => failed == 0
            ? $"Triggered {appName ?? "app"} reinstall on {succeeded} device(s)"
            : $"Triggered on {succeeded}, {failed} failed",
        DeviceActionKind.OsUpdate => failed == 0 ? $"Triggered update check on {succeeded} device(s)" : $"Triggered on {succeeded}, {failed} failed",
        _ => failed == 0
            ? $"Cimian push initiated on {succeeded} device(s) - sync forced, remediation will create trigger file on check-in"
            : $"Push initiated on {succeeded}, {failed} sync(s) failed",
    };

    /// <summary>"1 Autopilot identity", "3 Autopilot identities".</summary>
    public static string Identities(int count) => $"{count} Autopilot identit{(count == 1 ? "y" : "ies")}";

    /// <summary>The question before an Autopilot action on <paramref name="count"/> identities.</summary>
    public static DeviceActionConfirm AutopilotConfirm(AutopilotAction action, int count, string? value = null) => action switch
    {
        AutopilotAction.SetGroupTag => new("Set Group Tag",
            $"Do you want to {(string.IsNullOrEmpty(value) ? "clear the group tag on" : $"set group tag \"{value}\" on")} {Identities(count)}?"),
        AutopilotAction.AssignUser => new("Assign User", $"Assign {value} to {Identities(count)}?"),
        AutopilotAction.UnassignUser => new("Unassign User", $"Remove the assigned user from {Identities(count)}?"),
        AutopilotAction.Sync => new("Sync Autopilot", "Sync Intune with the Autopilot service? This applies to the whole tenant."),
        _ => new("Delete Autopilot Identity",
            $"Delete {Identities(count)}? The devices are untouched but stop receiving an Autopilot profile. This cannot be undone."),
    };

    /// <summary>The line an Autopilot action runs under: "Setting group tag".</summary>
    public static string AutopilotVerb(AutopilotAction action) => action switch
    {
        AutopilotAction.SetGroupTag => "Setting group tag",
        AutopilotAction.AssignUser => "Assigning user",
        AutopilotAction.UnassignUser => "Unassigning user",
        AutopilotAction.Sync => "Syncing Autopilot",
        _ => "Deleting identity",
    };

    /// <summary>"Setting group tag: done on 3 device(s)." or "…: 2 done, 1 failed — reason".</summary>
    public static string AutopilotResult(string verb, int total, IReadOnlyList<string> failures) =>
        failures.Count == 0
            ? $"{verb}: done on {total} device(s)."
            : $"{verb}: {total - failures.Count} done, {failures.Count} failed — {failures[0]}";

    /// <summary>Whether <paramref name="upn"/> looks like a user principal name, as the Assign User card asks.</summary>
    public static bool IsUserPrincipalName(string upn) => upn.Trim().Contains('@');

    /// <summary>"Assign: 3 devices to Example MDM" — the line an Apple organization action is confirmed and run under.</summary>
    public static string AppleWhat(AppleOrgAction action, int count, string detail) =>
        $"{action.Title}: {count} device{(count == 1 ? "" : "s")} {detail}".TrimEnd();

    public static string AppleResult(AppleOrgAction action, AppleOrgActivityResult result, int count) =>
        result.Succeeded
            ? $"{action.Title}: done on {count} device(s)."
            : $"{action.Title}: Apple reports {result.Status} (activity {result.ActivityId}).";

    /// <summary>The detail after "Release: 3 devices".</summary>
    public const string AppleReleaseDetail = "— they leave the organization and lose their enrollment assignment. This cannot be undone";

    /// <summary>
    /// A migration deadline picked as a day: the end of that local day, when
    /// it falls after <paramref name="now"/> and within Apple's 90-day cap; otherwise null.
    /// </summary>
    public static DateTimeOffset? MigrationDeadline(DateTime? day, DateTimeOffset now)
    {
        if (day is not { } d) return null;
        var deadline = new DateTimeOffset(d.Date.AddHours(23).AddMinutes(59));
        return deadline > now && deadline <= AppleOrgAction.LatestDeadline(now) ? deadline : null;
    }

    /// <summary>A few devices are re-read one by one after an Apple action; more re-read the whole organization.</summary>
    public const int AppleRereadLimit = 15;

    /// <summary>The note under the Offboard card's Delete Intune Record box, or null when it cancels nothing.</summary>
    public static string? OffboardWarning(OffboardPlan plan) => plan.CancelsPendingAction
        ? $"The pending action lives on the Intune record — deleting it before the device checks in cancels the {(plan.TerminalAction == OffboardTerminalAction.Wipe ? "wipe" : "retire")}."
        : null;

    /// <summary>Fresh Start with no Windows device in the selection.</summary>
    public const string FreshStartNoWindows = "Fresh Start applies to Windows devices only — none selected";

    /// <summary>What an action says in demo mode, where nothing is sent.</summary>
    public static string Demo(string title) => $"Demo mode: {title} was not sent. Nothing leaves the app in a demo.";
}
