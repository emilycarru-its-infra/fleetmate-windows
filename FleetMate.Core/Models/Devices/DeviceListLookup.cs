using System.Text.RegularExpressions;

namespace FleetMate.Core.Models.Devices;

/// <summary>
/// Where a device's sources disagree: present in one system and missing from
/// another. Each check runs only once the systems it compares have been read,
/// so a source still loading never makes every device look missing from it.
/// </summary>
public static class DeviceDiscrepancies
{
    public const string None = "No Discrepancy";
    public const string OrgNotEnrolled = "In Apple Organization, Not Enrolled";
    public const string AutopilotNotEnrolled = "In Autopilot, Not Enrolled";
    public const string EnrolledUnregistered = "Enrolled, Not in Apple Organization or Autopilot";
    public const string OtherService = "Assigned to Another Service";
    public const string NoService = "Not Assigned to a Service";
    public const string NotInInventory = "Missing from Inventory";
    public const string Unknown = "Unknown to Every System";

    /// <summary>What has been read, so each check knows whether it can speak.</summary>
    public sealed record Sources(
        bool AutopilotRead,
        bool AppleOrgsRead,
        IReadOnlySet<string>? InventorySerials);

    /// <summary>
    /// Fill <see cref="DeviceListRow.Discrepancies"/> on every row. The
    /// service enrolled Apple devices should be assigned to is the one most of
    /// them already are, so no service name is configured or assumed.
    /// </summary>
    public static void Apply(IReadOnlyList<DeviceListRow> rows, Sources sources)
    {
        var homeService = sources.AppleOrgsRead ? HomeService(rows) : null;
        foreach (var row in rows) row.Discrepancies = Of(row, sources, homeService);
    }

    public static string? HomeService(IEnumerable<DeviceListRow> rows) =>
        rows.Where(r => r.IsEnrolled && r.Apple?.AssignedServerId != null && r.ServerName != null)
            .GroupBy(r => r.ServerName!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

    public static List<string> Of(DeviceListRow row, Sources sources, string? homeService)
    {
        var found = new List<string>();
        if (row.IsUnknown)
        {
            found.Add(Unknown);
            return found;
        }

        if (row.Apple != null && !row.IsEnrolled) found.Add(OrgNotEnrolled);
        if (row.Autopilot != null && !row.IsEnrolled) found.Add(AutopilotNotEnrolled);

        if (row.IsEnrolled && row.Apple == null && row.Autopilot == null)
        {
            // Only platforms a provisioning system could hold, and only once
            // that system has been read.
            var appleUnregistered = sources.AppleOrgsRead && row.IsApplePlatform;
            var windowsUnregistered = sources.AutopilotRead && row.IsWindows;
            if (appleUnregistered || windowsUnregistered) found.Add(EnrolledUnregistered);
        }

        if (row.IsEnrolled && row.Apple != null)
        {
            if (row.Apple.AssignedServerId == null) found.Add(NoService);
            else if (homeService != null && !string.Equals(row.ServerName, homeService, StringComparison.OrdinalIgnoreCase))
                found.Add(OtherService);
        }

        if (sources.InventorySerials is { Count: > 0 } inventory
            && row.SerialNumber is { } serial
            && !inventory.Contains(DeviceListJoin.Normalize(serial)))
            found.Add(NotInInventory);

        return found;
    }
}

/// <summary>
/// Reads a list of serial numbers typed, pasted or imported from a text or CSV
/// file. A CSV with a header naming a serial column contributes that column
/// only; anything else is split on commas, semicolons, tabs and line breaks.
/// </summary>
public static class SerialListParser
{
    public const int MaxSerials = 5000;

    private static readonly Regex Separators = new(@"[\s,;]+", RegexOptions.Compiled);
    private static readonly Regex SerialShape = new(@"^[A-Za-z0-9][A-Za-z0-9\-]{2,39}$", RegexOptions.Compiled);
    private static readonly string[] HeaderWords = { "serial", "serialnumber", "serial number", "serial_number", "serial no", "sn" };

    /// <summary>Distinct serials in first-seen order, normalized as the Devices join compares them.</summary>
    public static List<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        IEnumerable<string> tokens = SerialColumn(lines) ?? Separators.Split(text);
        var seen = new HashSet<string>();
        var serials = new List<string>();
        foreach (var raw in tokens)
        {
            var token = raw.Trim().Trim('"', '\'').Trim();
            if (token.Length == 0 || !SerialShape.IsMatch(token)) continue;
            if (HeaderWords.Contains(token.ToLowerInvariant())) continue;
            var serial = DeviceListJoin.Normalize(token);
            if (seen.Add(serial)) serials.Add(serial);
            if (serials.Count == MaxSerials) break;
        }
        return serials;
    }

    /// <summary>The serial column of a CSV whose first line names one; null otherwise.</summary>
    private static IEnumerable<string>? SerialColumn(string[] lines)
    {
        var first = lines.FirstOrDefault(l => l.Trim().Length > 0);
        if (first == null) return null;
        var delimiter = first.Contains('\t') ? '\t' : first.Contains(';') && !first.Contains(',') ? ';' : ',';
        var headers = SplitCsvLine(first, delimiter);
        if (headers.Count < 2) return null;
        var index = headers.FindIndex(h =>
        {
            var name = h.Trim().Trim('"').Trim().ToLowerInvariant();
            return name.Contains("serial") || name == "sn";
        });
        if (index < 0) return null;
        return lines.SkipWhile(l => l.Trim().Length == 0).Skip(1)
            .Select(l => SplitCsvLine(l, delimiter))
            .Where(cells => cells.Count > index)
            .Select(cells => cells[index]);
    }

    /// <summary>One CSV line's cells, honouring double-quoted cells that contain the delimiter.</summary>
    private static List<string> SplitCsvLine(string line, char delimiter)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == delimiter && !quoted)
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else current.Append(c);
        }
        cells.Add(current.ToString());
        return cells;
    }
}

/// <summary>
/// Narrows the Devices list to a looked-up list of serials: every row whose
/// serial is on it, plus a "Not Found" row for each serial no system knows.
/// </summary>
public static class SerialLookup
{
    public sealed record Result(List<DeviceListRow> Rows, int Matched, List<string> Unknown);

    public static Result Apply(IReadOnlyList<DeviceListRow> rows, IReadOnlyList<string> serials)
    {
        var wanted = new HashSet<string>(serials.Select(DeviceListJoin.Normalize));
        var output = new List<DeviceListRow>();
        var found = new HashSet<string>();
        foreach (var row in rows)
        {
            if (row.SerialNumber is not { } serial) continue;
            var key = DeviceListJoin.Normalize(serial);
            if (!wanted.Contains(key)) continue;
            output.Add(row);
            found.Add(key);
        }
        var unknown = serials.Select(DeviceListJoin.Normalize).Where(s => !found.Contains(s)).Distinct().ToList();
        output.AddRange(unknown.Select(DeviceListRow.Unknown));
        return new Result(output, found.Count, unknown);
    }
}

/// <summary>
/// The Devices table's saved arrangement: which optional columns show and the
/// order every column sits in. Reads the earlier format too, a bare list of
/// the optional columns shown.
/// </summary>
public sealed class DeviceColumnLayout
{
    /// <summary>Never hidden: every lookup and action starts from the serial.</summary>
    public const string AlwaysVisible = "Serial";

    public List<string> Shown { get; set; } = new();
    public List<string> Order { get; set; } = new();

    public static DeviceColumnLayout Parse(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            return new DeviceColumnLayout
            {
                Shown = doc.RootElement.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToList()
            };
        return System.Text.Json.JsonSerializer.Deserialize<DeviceColumnLayout>(json) ?? new();
    }

    public string Serialize() => System.Text.Json.JsonSerializer.Serialize(this);

    /// <summary>
    /// The columns in saved order. Columns the save does not name (added in a
    /// later version) keep their place after the column they follow by default.
    /// </summary>
    public List<string> Arrange(IReadOnlyList<string> defaults)
    {
        var known = new HashSet<string>(defaults);
        var result = Order.Where(known.Contains).Distinct().ToList();
        for (var i = 0; i < defaults.Count; i++)
        {
            if (result.Contains(defaults[i])) continue;
            var after = i == 0 ? -1 : result.IndexOf(defaults[i - 1]);
            result.Insert(after + 1, defaults[i]);
        }
        return result;
    }
}
