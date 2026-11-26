using System.Text;
using FleetMate.Core.Models.Devices;

namespace FleetMate.Core.Devices;

/// <summary>One per-setting state as the lightbox lists it: its name, state, tone and detail lines.</summary>
public sealed record ComplianceSettingView(string Name, string State, DeviceTone Tone, bool IsBad, IReadOnlyList<string> Details);

/// <summary>
/// The compliance troubleshooting lightbox: one policy on one device. Its
/// header, the per-setting states (failing rows first), the policy definition
/// and the plain-text report Copy puts on the clipboard. The layout is the
/// shared contract with the macOS app; both Windows apps draw it from here.
/// </summary>
public sealed class ComplianceReport
{
    public ComplianceReport(IntuneDevice device, DeviceCompliancePolicyState policy)
    {
        Device = device;
        Policy = policy;
    }

    public IntuneDevice Device { get; }
    public DeviceCompliancePolicyState Policy { get; }
    public List<ComplianceSettingState> SettingStates { get; set; } = new();
    public CompliancePolicyDefinition? Definition { get; set; }
    public List<string> AssignedGroupNames { get; set; } = new();

    public string PolicyName => Policy.DisplayName ?? "Unknown Policy";

    /// <summary>The platform badge: the definition's platform once read, else the policy state's.</summary>
    public string? Platform => Definition?.PlatformType ?? Policy.PlatformType;

    public int? Version => Definition?.Version ?? Policy.Version;

    /// <summary>"v3", or empty without a version.</summary>
    public string VersionText => Version.HasValue ? $"v{Version}" : "";

    /// <summary>The state badge.</summary>
    public (DeviceTone Tone, string Text) StateBadge => Policy.State?.ToLowerInvariant() switch
    {
        "compliant" => (DeviceTone.Good, "Compliant"),
        "noncompliant" => (DeviceTone.Attention, "Non-Compliant"),
        "error" => (DeviceTone.Attention, "Error"),
        "conflict" => (DeviceTone.Caution, "Conflict"),
        "ingraceperiod" => (DeviceTone.Caution, "In Grace Period"),
        _ => (DeviceTone.Neutral, Policy.State ?? "Unknown"),
    };

    /// <summary>"Name · serial · user · checked in … · evaluated …".</summary>
    public string DeviceLine
    {
        get
        {
            var parts = new List<string> { Device.DeviceName };
            if (!string.IsNullOrEmpty(Device.SerialNumber)) parts.Add(Device.SerialNumber!);
            if (!string.IsNullOrEmpty(Device.UserDisplayName ?? Device.UserPrincipalName))
                parts.Add((Device.UserDisplayName ?? Device.UserPrincipalName)!);
            if (Device.LastSyncDateTime is { } sync) parts.Add($"checked in {sync:yyyy-MM-dd HH:mm}");
            if (Policy.LastReportedDateTime is { } reported) parts.Add($"evaluated {reported:yyyy-MM-dd HH:mm}");
            return string.Join(" · ", parts);
        }
    }

    public string SettingsHeading => $"Setting States ({SettingStates.Count})";

    /// <summary>Non-compliant, error and conflict rows first, then the rest, by name.</summary>
    public List<ComplianceSettingView> Settings() => SettingStates
        .OrderByDescending(s => s.IsBad)
        .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
        .Select(s => new ComplianceSettingView(s.DisplayName, s.State ?? "unknown",
            s.IsBad ? DeviceTone.Attention : s.State?.ToLowerInvariant() == "compliant" ? DeviceTone.Good : DeviceTone.Neutral,
            s.IsBad, Details(s)))
        .ToList();

    private static List<string> Details(ComplianceSettingState s)
    {
        var lines = new List<string>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrEmpty(value)) lines.Add($"{label}: {value}");
        }
        Add("Current value", s.CurrentValue);
        // "0" means no error — showing it on every compliant row is noise.
        if (s.ErrorCode is { } code && code != 0)
            Add("Error", $"0x{code:X8}{(string.IsNullOrEmpty(s.ErrorDescription) ? "" : $" — {s.ErrorDescription}")}");
        else if (!string.IsNullOrEmpty(s.ErrorDescription) && s.ErrorDescription != "0")
            Add("Error", s.ErrorDescription);
        if (s.Sources is { Count: > 0 })
            Add("Source policies", string.Join(", ", s.Sources.Select(x => x.DisplayName ?? x.Id)));
        Add("User", s.UserPrincipalName);
        return lines;
    }

    /// <summary>Why the definition column has nothing to show, or null when it does.</summary>
    public string? DefinitionNote =>
        Definition == null ? "The policy definition could not be loaded."
        : Definition.AccessDenied ? "Graph refused the policy definition read — the app registration needs DeviceManagementConfiguration.Read.All."
        : null;

    public const string NoSettingStates = "No per-setting states reported for this policy.";

    /// <summary>The device's compliance page in the Intune admin center.</summary>
    public string IntuneUrl => DeviceLinks.IntuneCompliance(Device.Id);

    /// <summary>
    /// The plain-text, ticket-ready report: the policy line with its state,
    /// the device block, every setting, then requirements, actions and assignments.
    /// </summary>
    public string Text()
    {
        var report = new StringBuilder();
        report.AppendLine($"Compliance policy: {Policy.DisplayName ?? "Unknown"} — {Policy.State ?? "unknown"}"
            + (Version.HasValue ? $" (v{Version})" : "")
            + (string.IsNullOrEmpty(Platform) ? "" : $" [{Platform}]"));
        report.AppendLine();
        report.AppendLine($"Device: {Device.DeviceName}");
        if (!string.IsNullOrEmpty(Device.SerialNumber)) report.AppendLine($"Serial: {Device.SerialNumber}");
        if (!string.IsNullOrEmpty(Device.UserDisplayName ?? Device.UserPrincipalName))
            report.AppendLine($"Primary user: {Device.UserDisplayName ?? Device.UserPrincipalName}");
        if (!string.IsNullOrEmpty(Device.OperatingSystem))
            report.AppendLine($"OS: {Device.OperatingSystem} {Device.OsVersion}".TrimEnd());
        report.AppendLine($"Overall compliance: {Device.ComplianceState ?? "unknown"}");
        if (Device.LastSyncDateTime is { } sync) report.AppendLine($"Last check-in: {sync:yyyy-MM-dd HH:mm}");
        if (Policy.LastReportedDateTime is { } reported) report.AppendLine($"Last evaluation: {reported:yyyy-MM-dd HH:mm}");
        report.AppendLine($"Intune id: {Device.Id}");

        report.AppendLine();
        report.AppendLine($"Setting states ({SettingStates.Count}):");
        foreach (var state in SettingStates.OrderByDescending(s => s.IsBad).ThenBy(s => s.DisplayName))
        {
            var line = $"  [{state.State ?? "unknown"}] {state.DisplayName}";
            if (!string.IsNullOrEmpty(state.CurrentValue)) line += $" — current: {state.CurrentValue}";
            if (state.ErrorCode is { } code && code != 0) line += $" — error: 0x{code:X8} {state.ErrorDescription}".TrimEnd();
            else if (!string.IsNullOrEmpty(state.ErrorDescription) && state.ErrorDescription != "0") line += $" — error: {state.ErrorDescription}";
            report.AppendLine(line);
            if (state.Sources is { Count: > 0 })
                report.AppendLine($"      sources: {string.Join(", ", state.Sources.Select(s => s.DisplayName ?? s.Id))}");
            if (!string.IsNullOrEmpty(state.UserPrincipalName)) report.AppendLine($"      user: {state.UserPrincipalName}");
        }

        if (Definition is { AccessDenied: false })
        {
            if (Definition.Requirements.Count > 0)
            {
                report.AppendLine();
                report.AppendLine("Policy requirements:");
                foreach (var requirement in Definition.Requirements)
                    report.AppendLine($"  {requirement.Key}: {requirement.Value}");
            }
            if (Definition.ScheduledActions.Count > 0)
            {
                report.AppendLine();
                report.AppendLine("Actions for noncompliance:");
                foreach (var action in Definition.ScheduledActions) report.AppendLine($"  {action}");
            }
            if (AssignedGroupNames.Count > 0)
            {
                report.AppendLine();
                report.AppendLine("Assigned to:");
                foreach (var name in AssignedGroupNames) report.AppendLine($"  {name}");
            }
        }

        report.AppendLine();
        report.AppendLine($"Intune: {IntuneUrl}");
        return report.ToString();
    }

    /// <summary>What the header says after Sync: sent, or why it failed.</summary>
    public static string SyncResult(bool success, string? message) => success
        ? "Sync sent — the device re-evaluates at next check-in"
        : $"Sync failed: {message ?? "no response"}";
}

/// <summary>Links into the Intune admin center for a device.</summary>
public static class DeviceLinks
{
    public static string IntuneCompliance(string intuneDeviceId) =>
        $"https://intune.microsoft.com/#view/Microsoft_Intune_Devices/DeviceSettingsMenuBlade/~/compliance/mdmDeviceId/{intuneDeviceId}";
}
