namespace FleetMate.Core.Models.Devices;

/// <summary>
/// The platform a managed device runs, derived from operatingSystem.
///
/// Wipe semantics diverge sharply by platform — obliterationBehavior is macOS
/// only, Fresh Start and protected wipe are Windows only, and Autopilot
/// registrations exist for Windows alone — so every lifecycle action gates on
/// this rather than trusting the caller to pass a coherent option set.
/// </summary>
public enum DevicePlatform { MacOS, Windows, IOS, Android, Other }

public static class DevicePlatforms
{
    public static DevicePlatform FromOperatingSystem(string? operatingSystem)
    {
        var os = (operatingSystem ?? "").ToLowerInvariant();
        if (os.Contains("macos") || os.Contains("mac os") || os.Contains("osx")) return DevicePlatform.MacOS;
        if (os.Contains("windows")) return DevicePlatform.Windows;
        if (os.Contains("ios") || os.Contains("ipados")) return DevicePlatform.IOS;
        if (os.Contains("android")) return DevicePlatform.Android;
        return DevicePlatform.Other;
    }

    public static DevicePlatform Platform(this IntuneDevice device) => FromOperatingSystem(device.OperatingSystem);

    public static string DisplayName(this DevicePlatform platform) => platform switch
    {
        DevicePlatform.MacOS => "macOS",
        DevicePlatform.Windows => "Windows",
        DevicePlatform.IOS => "iOS/iPadOS",
        DevicePlatform.Android => "Android",
        _ => "Other",
    };
}

/// <summary>
/// Options for the Intune wipe action. Only the keys that apply to the target
/// platform are sent — Graph rejects a macOS obliterationBehavior on a Windows
/// device and vice versa — so the body is assembled per platform.
/// </summary>
public sealed record WipeOptions
{
    /// <summary>macOS 12+ Erase All Content and Settings behaviour.</summary>
    public enum ObliterationBehavior { Default, DoNotObliterate, ObliterateWithWarning, Always }

    /// <summary>Leave the device enrolled after the wipe (Windows/macOS).</summary>
    public bool KeepEnrollmentData { get; init; }
    /// <summary>Preserve user data — a reset rather than a full erase (Windows).</summary>
    public bool KeepUserData { get; init; }
    /// <summary>
    /// Windows protected wipe: retries until it succeeds and cannot be
    /// circumvented by the user, at the cost of possibly leaving the device
    /// unbootable if interrupted.
    /// </summary>
    public bool UseProtectedWipe { get; init; }
    /// <summary>Keep the eSIM data plan on cellular devices.</summary>
    public bool PersistEsimDataPlan { get; init; }
    /// <summary>Six-digit recovery lock / firmware PIN applied on macOS and iOS wipes.</summary>
    public string? MacOsUnlockCode { get; init; }
    /// <summary>macOS 12+ only.</summary>
    public ObliterationBehavior? Obliteration { get; init; }

    public static string WireValue(ObliterationBehavior behavior) => behavior switch
    {
        ObliterationBehavior.DoNotObliterate => "doNotObliterate",
        ObliterationBehavior.ObliterateWithWarning => "obliterateWithWarning",
        ObliterationBehavior.Always => "always",
        _ => "default",
    };

    public static string DisplayName(ObliterationBehavior behavior) => behavior switch
    {
        ObliterationBehavior.DoNotObliterate => "EACS only (fail if unavailable)",
        ObliterationBehavior.ObliterateWithWarning => "Erase, warn first",
        ObliterationBehavior.Always => "Always full erase",
        _ => "Default (EACS, fall back to erase)",
    };

    /// <summary>The body for POST managedDevices/{id}/wipe, trimmed to the keys the platform accepts.</summary>
    public Dictionary<string, object> RequestBody(DevicePlatform platform)
    {
        var body = new Dictionary<string, object>
        {
            ["keepEnrollmentData"] = KeepEnrollmentData,
            ["keepUserData"] = KeepUserData,
        };
        switch (platform)
        {
            case DevicePlatform.Windows:
                if (UseProtectedWipe) body["useProtectedWipe"] = true;
                break;
            case DevicePlatform.MacOS:
                // macOS has no notion of keeping user data — the erase is total.
                body["keepUserData"] = false;
                if (!string.IsNullOrEmpty(MacOsUnlockCode)) body["macOsUnlockCode"] = MacOsUnlockCode!;
                if (Obliteration is { } behavior) body["obliterationBehavior"] = WireValue(behavior);
                break;
            case DevicePlatform.IOS:
                body["keepUserData"] = false;
                if (!string.IsNullOrEmpty(MacOsUnlockCode)) body["macOsUnlockCode"] = MacOsUnlockCode!;
                if (PersistEsimDataPlan) body["persistEsimDataPlan"] = true;
                break;
        }
        return body;
    }
}

/// <summary>
/// A device decommission run: the terminal Intune action plus whichever
/// downstream records should be cleaned up with it.
/// </summary>
public sealed record OffboardPlan
{
    public enum TerminalAction { Wipe, Retire, None }
    public enum EntraAction { None, Disable, Delete }

    public TerminalAction Terminal { get; init; } = TerminalAction.Wipe;
    public WipeOptions WipeOptions { get; init; } = new();
    /// <summary>
    /// Delete the Intune managedDevice record. Destructive to a pending wipe:
    /// if the device has not checked in and picked the command up, removing the
    /// record cancels it.
    /// </summary>
    public bool DeleteIntuneRecord { get; init; }
    /// <summary>Delete the Windows Autopilot registration so the hardware hash is released. Windows only.</summary>
    public bool DeleteAutopilotRegistration { get; init; }
    public EntraAction Entra { get; init; } = EntraAction.None;

    public static string DisplayName(TerminalAction action) => action switch
    {
        TerminalAction.Wipe => "Wipe (factory reset)",
        TerminalAction.Retire => "Retire (remove company data)",
        _ => "Leave the device alone",
    };

    public static string DisplayName(EntraAction action) => action switch
    {
        EntraAction.Disable => "Disable the Entra device object",
        EntraAction.Delete => "Delete the Entra device object",
        _ => "Leave the Entra device object",
    };

    /// <summary>The confirmation text: every step the plan will take, for how many devices.</summary>
    public string Summary(int deviceCount)
    {
        var parts = new List<string>();
        if (Terminal == TerminalAction.Wipe) parts.Add("factory-reset");
        else if (Terminal == TerminalAction.Retire) parts.Add("retire");
        if (DeleteAutopilotRegistration) parts.Add("delete the Autopilot registration");
        if (Entra == EntraAction.Disable) parts.Add("disable the Entra device object");
        else if (Entra == EntraAction.Delete) parts.Add("delete the Entra device object");
        if (DeleteIntuneRecord) parts.Add("delete the Intune record");

        if (parts.Count == 0) return "No offboard steps are selected.";
        var steps = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
        return $"This will {steps} for {deviceCount} device(s). This cannot be undone.";
    }

    /// <summary>Deleting the record before the device checks in cancels the pending wipe or retire.</summary>
    public bool CancelsPendingAction => DeleteIntuneRecord && Terminal != TerminalAction.None;
}

public sealed record OffboardStepResult(string Step, OffboardStepResult.StepOutcome Outcome, string? Detail = null)
{
    public enum StepOutcome { Succeeded, Failed, Skipped }

    public string Display => Detail == null ? Step : $"{Step} — {Detail}";
}

/// <summary>One step of a dry run: what would be sent, or why the step drops out.</summary>
public sealed record OffboardPlannedStep(string Step, bool WillRun, string Detail);

public sealed record OffboardResult(string Identifier, string? DeviceName, DevicePlatform Platform,
    IReadOnlyList<OffboardStepResult> Steps)
{
    /// <summary>
    /// A run is a success when nothing failed — skipped steps are expected
    /// (Autopilot on a Mac, an Entra object that was already gone).
    /// </summary>
    public bool Success => Steps.All(s => s.Outcome != OffboardStepResult.StepOutcome.Failed);
}
