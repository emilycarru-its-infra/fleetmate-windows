using System.Text.Json.Nodes;

namespace FleetMate.Core.Models.Devices;

/// <summary>The platforms a wipe or offboard treats differently.</summary>
public enum DevicePlatform { Windows, MacOS, IOS, Android, Other }

public static class DevicePlatforms
{
    /// <summary>The platform an Intune operatingSystem string names.</summary>
    public static DevicePlatform From(string? operatingSystem) => (operatingSystem ?? "").Trim().ToLowerInvariant() switch
    {
        var os when os.StartsWith("windows") => DevicePlatform.Windows,
        "macos" or "mac os" or "mac" => DevicePlatform.MacOS,
        "ios" or "ipados" => DevicePlatform.IOS,
        var os when os.StartsWith("android") => DevicePlatform.Android,
        _ => DevicePlatform.Other,
    };

    public static string DisplayName(this DevicePlatform platform) => platform switch
    {
        DevicePlatform.Windows => "Windows",
        DevicePlatform.MacOS => "macOS",
        DevicePlatform.IOS => "iOS",
        DevicePlatform.Android => "Android",
        _ => "Other",
    };
}

/// <summary>
/// Options for the Intune <c>wipe</c> action. Only the keys the target
/// platform accepts are sent: Graph rejects a macOS obliterationBehavior on a
/// Windows device and vice versa.
/// </summary>
public sealed record WipeOptions
{
    /// <summary>macOS 12+ Erase All Content and Settings behaviour, as Graph spells it.</summary>
    public static readonly string[] ObliterationBehaviors = { "default", "doNotObliterate", "obliterateWithWarning", "always" };

    /// <summary>Leave the device enrolled after the wipe (Windows/macOS).</summary>
    public bool KeepEnrollmentData { get; init; }
    /// <summary>Preserve user data, a reset rather than a full erase (Windows).</summary>
    public bool KeepUserData { get; init; }
    /// <summary>Windows protected wipe: retries until it succeeds and can't be circumvented.</summary>
    public bool UseProtectedWipe { get; init; }
    /// <summary>Keep the eSIM data plan on cellular devices.</summary>
    public bool PersistEsimDataPlan { get; init; }
    /// <summary>Six-digit recovery lock / firmware PIN applied on macOS and iOS wipes.</summary>
    public string? MacOsUnlockCode { get; init; }
    /// <summary>macOS 12+ only.</summary>
    public string? ObliterationBehavior { get; init; }

    /// <summary>The body for <c>POST managedDevices/{id}/wipe</c>, trimmed to what the platform accepts.</summary>
    public JsonObject RequestBody(DevicePlatform platform)
    {
        var body = new JsonObject
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
                // macOS has no notion of keeping user data: the erase is total.
                body["keepUserData"] = false;
                if (!string.IsNullOrEmpty(MacOsUnlockCode)) body["macOsUnlockCode"] = MacOsUnlockCode;
                if (ObliterationBehavior != null) body["obliterationBehavior"] = ObliterationBehavior;
                break;
            case DevicePlatform.IOS:
                body["keepUserData"] = false;
                if (!string.IsNullOrEmpty(MacOsUnlockCode)) body["macOsUnlockCode"] = MacOsUnlockCode;
                if (PersistEsimDataPlan) body["persistEsimDataPlan"] = true;
                break;
        }
        return body;
    }

    /// <summary>How the Devices wipe card names an obliteration behaviour (the macOS client's wording).</summary>
    public static string ObliterationDisplayName(string behavior) => behavior switch
    {
        "doNotObliterate" => "EACS only (fail if unavailable)",
        "obliterateWithWarning" => "Erase, warn first",
        "always" => "Always full erase",
        _ => "Default (EACS, fall back to erase)",
    };

    /// <summary>The body as the dry run prints it: keys sorted so two runs print identically.</summary>
    public static string Describe(JsonObject body) =>
        "{" + string.Join(",", body.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"\"{p.Key}\":{p.Value?.ToJsonString() ?? "null"}")) + "}";
}

/// <summary>Terminal Intune action of an offboard.</summary>
public enum OffboardTerminalAction { Wipe, Retire, None }

/// <summary>What happens to the Entra device object.</summary>
public enum OffboardEntraAction { None, Disable, Delete }

/// <summary>A decommission run: the terminal Intune action plus the downstream records to clean up.</summary>
public sealed record OffboardPlan
{
    public OffboardTerminalAction TerminalAction { get; init; } = OffboardTerminalAction.Wipe;
    public WipeOptions WipeOptions { get; init; } = new();
    /// <summary>Delete the Intune record, last. Deleting it earlier cancels a wipe the device hasn't picked up.</summary>
    public bool DeleteIntuneRecord { get; init; }
    /// <summary>Delete the Windows Autopilot registration, releasing the hardware hash. Windows only.</summary>
    public bool DeleteAutopilotRegistration { get; init; }
    public OffboardEntraAction EntraAction { get; init; } = OffboardEntraAction.None;

    public static string DisplayName(OffboardTerminalAction action) => action switch
    {
        OffboardTerminalAction.Wipe => "Wipe (factory reset)",
        OffboardTerminalAction.Retire => "Retire (remove company data)",
        _ => "Leave the device alone",
    };

    public static string DisplayName(OffboardEntraAction action) => action switch
    {
        OffboardEntraAction.Disable => "Disable the Entra device object",
        OffboardEntraAction.Delete => "Delete the Entra device object",
        _ => "Leave the Entra device object",
    };

    /// <summary>The confirmation text: every step the plan will take, for how many devices.</summary>
    public string Summary(int deviceCount)
    {
        var parts = new List<string>();
        if (TerminalAction == OffboardTerminalAction.Wipe) parts.Add("factory-reset");
        else if (TerminalAction == OffboardTerminalAction.Retire) parts.Add("retire");
        if (DeleteAutopilotRegistration) parts.Add("delete the Autopilot registration");
        if (EntraAction == OffboardEntraAction.Disable) parts.Add("disable the Entra device object");
        else if (EntraAction == OffboardEntraAction.Delete) parts.Add("delete the Entra device object");
        if (DeleteIntuneRecord) parts.Add("delete the Intune record");

        if (parts.Count == 0) return "No offboard steps are selected.";
        var steps = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
        return $"This will {steps} for {deviceCount} device(s). This cannot be undone.";
    }

    /// <summary>Deleting the record before the device checks in cancels the pending wipe or retire.</summary>
    public bool CancelsPendingAction => DeleteIntuneRecord && TerminalAction != OffboardTerminalAction.None;
}

public enum OffboardOutcome { Succeeded, Failed, Skipped }

public sealed record OffboardStepResult(string Step, OffboardOutcome Outcome, string? Detail = null)
{
    /// <summary>The step as a results list shows it: the step, then why, when there is a reason.</summary>
    public string Display => Detail == null ? Step : $"{Step} — {Detail}";
}

/// <summary>One step of a dry run: what would be sent, or why the step drops out.</summary>
public sealed record OffboardPlannedStep(string Step, bool WillRun, string Detail);

public sealed record OffboardResult(string Identifier, string? DeviceName, DevicePlatform Platform,
    IReadOnlyList<OffboardStepResult> Steps)
{
    /// <summary>Nothing failed. Skipped steps are expected (Autopilot on a Mac, an object already gone).</summary>
    public bool Success => Steps.All(s => s.Outcome != OffboardOutcome.Failed);
}

/// <summary>The directory records left for an identifier that has no Intune record.</summary>
public sealed record OrphanDeviceRecords(AutopilotDevice? Autopilot, EntraDevice? Entra)
{
    public bool IsEmpty => Autopilot == null && Entra == null;
}
