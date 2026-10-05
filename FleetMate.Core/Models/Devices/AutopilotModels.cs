using System.Text;
using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Devices;

/// <summary>Where a Windows device stands between Autopilot and Intune.</summary>
public enum AutopilotRegistration
{
    RegisteredAndEnrolled,
    RegisteredNotEnrolled,
    EnrolledNotRegistered,
}

public static class AutopilotLabels
{
    public static string Label(this AutopilotRegistration r) => r switch
    {
        AutopilotRegistration.RegisteredAndEnrolled => "Registered",
        AutopilotRegistration.RegisteredNotEnrolled => "Registered, Not Enrolled",
        _ => "Not Registered",
    };

    /// <summary>The deployment profile assignment, as a reader would say it.</summary>
    public static string ProfileStatusLabel(this AutopilotDevice a) => a.DeploymentProfileAssignmentStatus?.ToLowerInvariant() switch
    {
        "assignedinsync" or "assignedoutofsync" or "assignedunkownsyncstate" or "assignedunknownsyncstate" => "Assigned",
        "notassigned" => "Not Assigned",
        "pending" => "Pending",
        "failed" => "Failed",
        null or "" or "unknown" => "Unknown",
        var s => s,
    };

    /// <summary>Autopilot's own enrollment state for the identity.</summary>
    public static string EnrollmentStateLabel(this AutopilotDevice a) => a.EnrollmentState?.ToLowerInvariant() switch
    {
        "enrolled" => "Enrolled",
        "notcontacted" => "Not Contacted",
        "pendingreset" => "Pending Reset",
        "failed" => "Failed",
        "blocked" => "Blocked",
        null or "" or "unknown" => "Unknown",
        var s => s,
    };

    public static string GroupTagLabel(this AutopilotDevice a) =>
        string.IsNullOrWhiteSpace(a.GroupTag) ? "No Group Tag" : a.GroupTag.Trim();

    /// <summary>Graph reports an all-zero GUID when nothing is linked.</summary>
    public static string? Linked(string? id) =>
        string.IsNullOrWhiteSpace(id) || id == "00000000-0000-0000-0000-000000000000" ? null : id.ToLowerInvariant();
}

/// <summary>What can be asked of Autopilot for a selection of identities.</summary>
public enum AutopilotAction { SetGroupTag, AssignUser, UnassignUser, Sync, Delete }

public static class AutopilotActions
{
    /// <summary>
    /// An action is offered only when it reaches every selected device: a
    /// device with no Autopilot identity rules every Autopilot action out,
    /// and Unassign needs every device to have a user.
    /// </summary>
    public static bool IsAvailable(this AutopilotAction action, IReadOnlyCollection<AutopilotDevice?> identities)
    {
        if (identities.Count == 0 || identities.Any(i => i == null)) return false;
        return action != AutopilotAction.UnassignUser
               || identities.All(i => !string.IsNullOrWhiteSpace(i!.UserPrincipalName));
    }
}

// ── Hardware hash import ─────────────────────────────────────────────────

/// <summary>One row of a hardware hash CSV, as Get-WindowsAutopilotInfo writes it.</summary>
public sealed record AutopilotHashEntry(string SerialNumber, string HardwareHash,
    string? ProductKey = null, string? GroupTag = null, string? AssignedUser = null)
{
    /// <summary>With a group tag set, every entry gets it; otherwise each keeps its own.</summary>
    public AutopilotHashEntry WithGroupTag(string? tag) =>
        string.IsNullOrWhiteSpace(tag) ? this : this with { GroupTag = tag.Trim() };
}

public sealed record AutopilotHashIssue(int Line, string Message);

public sealed class AutopilotHashCsv
{
    /// <summary>Intune accepts at most this many devices in one import.</summary>
    public const int MaxEntries = 500;

    public List<AutopilotHashEntry> Entries { get; } = new();
    public List<AutopilotHashIssue> Issues { get; } = new();

    /// <summary>UTF-8 or UTF-16 (PowerShell's Out-File default), with or without a BOM.</summary>
    public static AutopilotHashCsv Parse(byte[] data)
    {
        string text;
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) text = Encoding.Unicode.GetString(data, 2, data.Length - 2);
        else if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) text = Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        else text = Encoding.UTF8.GetString(data);
        return Parse(text);
    }

    public static AutopilotHashCsv Parse(string text)
    {
        var lines = text.Replace("﻿", "").Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var headerIndex = lines.FindIndex(l => !string.IsNullOrWhiteSpace(l));
        if (headerIndex < 0) throw new FormatException("The file lists no devices.");

        var header = Fields(lines[headerIndex]).Select(h => h.Trim().ToLowerInvariant()).ToList();
        int? Column(params string[] names)
        {
            var i = header.FindIndex(names.Contains);
            return i < 0 ? null : i;
        }
        var serialCol = Column("device serial number", "serial number", "serialnumber")
            ?? throw new FormatException("The file has no Device Serial Number column.");
        var hashCol = Column("hardware hash", "hardwarehash", "hardware identifier")
            ?? throw new FormatException("The file has no Hardware Hash column.");
        var productCol = Column("windows product id", "product id", "productkey");
        var tagCol = Column("group tag", "grouptag", "order id");
        var userCol = Column("assigned user", "assigneduser", "assigned user principal name");

        var csv = new AutopilotHashCsv();
        var seen = new HashSet<string>();
        for (var offset = headerIndex + 1; offset < lines.Count; offset++)
        {
            if (string.IsNullOrWhiteSpace(lines[offset])) continue;
            var lineNumber = offset + 1;
            var row = Fields(lines[offset]);
            string? Value(int? col) =>
                col is { } c && c < row.Count && !string.IsNullOrWhiteSpace(row[c]) ? row[c].Trim() : null;

            if (Value(serialCol) is not { } serial) { csv.Issues.Add(new(lineNumber, "No serial number.")); continue; }
            if (Value(hashCol) is not { } hash) { csv.Issues.Add(new(lineNumber, $"{serial}: no hardware hash.")); continue; }
            if (!IsBase64(hash)) { csv.Issues.Add(new(lineNumber, $"{serial}: the hardware hash is not valid base64.")); continue; }
            if (!seen.Add(DeviceListJoin.Normalize(serial)))
            {
                csv.Issues.Add(new(lineNumber, $"{serial}: listed more than once; the first row is used."));
                continue;
            }
            csv.Entries.Add(new AutopilotHashEntry(serial, hash, Value(productCol), Value(tagCol), Value(userCol)));
        }

        if (csv.Entries.Count == 0 && csv.Issues.Count == 0) throw new FormatException("The file lists no devices.");
        if (csv.Entries.Count > MaxEntries)
            csv.Issues.Add(new(0, $"Intune imports at most {MaxEntries} devices at once; split the file."));
        return csv;
    }

    private static bool IsBase64(string s)
    {
        var buffer = new byte[s.Length];
        return Convert.TryFromBase64String(s, buffer, out _);
    }

    /// <summary>Split one CSV line, honouring double-quoted fields and doubled quotes.</summary>
    internal static List<string> Fields(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else current.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields;
    }
}

/// <summary>An imported hash's progress, as Intune reports it.</summary>
public sealed class ImportedAutopilotIdentity
{
    public sealed class ImportState
    {
        /// <summary>unknown, pending, partial, complete or error.</summary>
        [JsonPropertyName("deviceImportStatus")] public string? DeviceImportStatus { get; set; }
        [JsonPropertyName("deviceRegistrationId")] public string? DeviceRegistrationId { get; set; }
        [JsonPropertyName("deviceErrorCode")] public int? DeviceErrorCode { get; set; }
        [JsonPropertyName("deviceErrorName")] public string? DeviceErrorName { get; set; }
    }

    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("serialNumber")] public string? SerialNumber { get; set; }
    [JsonPropertyName("groupTag")] public string? GroupTag { get; set; }
    [JsonPropertyName("state")] public ImportState? State { get; set; }

    public bool IsFinished => State?.DeviceImportStatus?.ToLowerInvariant() is "complete" or "error";
    public bool Succeeded => State?.DeviceImportStatus?.ToLowerInvariant() == "complete";

    /// <summary>Intune's error name, or its code when it gives no name.</summary>
    public string? FailureReason => State?.DeviceImportStatus?.ToLowerInvariant() != "error" ? null
        : !string.IsNullOrEmpty(State.DeviceErrorName) ? State.DeviceErrorName
        : State.DeviceErrorCode is { } code ? $"error {code}" : "error";
}

public sealed class ImportedAutopilotIdentitiesResponse
{
    [JsonPropertyName("value")] public List<ImportedAutopilotIdentity> Value { get; set; } = new();
}
