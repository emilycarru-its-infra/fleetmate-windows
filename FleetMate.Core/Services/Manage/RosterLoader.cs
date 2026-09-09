using FleetMate.Core.Models.Manage;
using Serilog;

namespace FleetMate.Core.Services.Manage;

/// <summary>
/// Reads the enrollment roster (computers.csv) and splits it into sidebar
/// sections. Header-driven and case-insensitive, so both the Windows roster
/// (lower-case headers with a separate <c>hostname</c> column) and the Mac
/// roster (capitalised headers, hostname in <c>allocation</c>) load.
/// </summary>
public class RosterLoader
{
    /// <summary>Include rows whose status is not an "Active" variant (returned, donated, recycled...).</summary>
    public bool IncludeRetired { get; init; }

    /// <summary>Include the Provisioning catalog (machines not yet assigned) in the sections.</summary>
    public bool IncludeProvisioning { get; init; }

    public FleetRoster Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Log.Warning("Roster not found at {Path}", path);
            return FleetRoster.Empty;
        }
        return Parse(File.ReadAllText(path));
    }

    public FleetRoster Parse(string csv)
    {
        var lines = (csv ?? "").Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var headerIndex = lines.FindIndex(l => !string.IsNullOrWhiteSpace(l));
        if (headerIndex < 0) return FleetRoster.Empty;

        var header = ParseLine(lines[headerIndex]).Select(h => h.Trim().TrimStart('﻿').ToLowerInvariant()).ToList();
        int Col(string name) => header.IndexOf(name);
        var iSerial = Col("serial");
        var iCatalog = Col("catalog");
        var iArea = Col("area");
        var iLocation = Col("location");
        var iAsset = Col("asset");
        var iUsage = Col("usage");
        var iStatus = Col("status");
        var iAllocation = Col("allocation");
        var iUsername = Col("username");
        var iPlatform = Col("platform");
        var iFleet = Col("fleet");
        var iHostname = Col("hostname");

        if (iSerial < 0)
        {
            Log.Warning("Roster has no serial column; header was {Header}", string.Join(",", header));
            return FleetRoster.Empty;
        }

        var source = new List<RosterComputer>();
        var retired = 0;

        foreach (var line in lines.Skip(headerIndex + 1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = ParseLine(line);
            string Get(int i) => i >= 0 && i < f.Count ? f[i].Trim() : "";

            var serial = Get(iSerial);
            if (serial.Length == 0) continue;

            // Mac roster: no hostname column, the allocation column holds the hostname.
            var hostname = iHostname >= 0 ? Get(iHostname) : Get(iAllocation);

            var computer = new RosterComputer
            {
                Serial = serial,
                Catalog = Get(iCatalog),
                Area = Get(iArea),
                Location = Get(iLocation),
                Asset = Get(iAsset),
                Usage = Get(iUsage),
                Status = Get(iStatus),
                Allocation = Get(iAllocation),
                Username = Get(iUsername),
                Platform = Get(iPlatform),
                Fleet = Get(iFleet),
                Hostname = hostname
            };

            if (!IncludeRetired && !computer.IsInService)
            {
                retired++;
                continue;
            }
            source.Add(computer);
        }

        var labs = new List<RosterComputer>();
        var kiosks = new List<RosterComputer>();
        var staff = new List<RosterComputer>();
        var faculty = new List<RosterComputer>();

        foreach (var c in source)
        {
            var catalog = c.Catalog;
            var usage = c.Usage;
            if (Eq(catalog, "Curriculum") && Eq(usage, "Shared") && !Eq(c.Area, "Podium"))
                labs.Add(c);
            else if (Eq(catalog, "Kiosk"))
                kiosks.Add(c);
            else if (Eq(catalog, "Staff") && Eq(usage, "Assigned"))
                staff.Add(c);
            else if (Eq(catalog, "Faculty") && Eq(usage, "Assigned"))
                faculty.Add(c);
            else if (IncludeProvisioning && Eq(catalog, "Provisioning"))
                labs.Add(c);
        }

        return new FleetRoster
        {
            Labs = GroupLabs(labs),
            Kiosks = GroupByLocation(kiosks),
            Staff = GroupBy(staff, c => c.Area.Length == 0 ? "Other" : c.Area),
            Faculty = GroupBy(faculty, c => FirstLetter(c.Allocation.Length > 0 ? c.Allocation : c.DisplayName)),
            Source = source.OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
            RetiredCount = retired
        };
    }

    /// <summary>
    /// Labs group by the fleet column when it is set (the real lab grouping),
    /// falling back to the room number. A fleet room shows its dominant room
    /// number with the fleet as the display name; a room-only group shows the
    /// room number with the dominant area as the display name.
    /// </summary>
    private static List<RosterRoom> GroupLabs(List<RosterComputer> labs)
    {
        var rooms = new List<RosterRoom>();

        foreach (var g in labs.Where(c => c.Fleet.Length > 0).GroupBy(c => c.Fleet))
        {
            rooms.Add(new RosterRoom
            {
                Number = Dominant(g.Select(c => c.Location)) ?? g.Key,
                DisplayName = g.Key,
                Computers = Sorted(g)
            });
        }

        foreach (var g in labs.Where(c => c.Fleet.Length == 0 && c.Location.Length > 0).GroupBy(c => c.Location))
        {
            rooms.Add(new RosterRoom
            {
                Number = g.Key,
                DisplayName = Dominant(g.Select(c => c.Area)),
                Computers = Sorted(g)
            });
        }

        var unplaced = labs.Where(c => c.Fleet.Length == 0 && c.Location.Length == 0).ToList();
        if (unplaced.Count > 0)
            rooms.Add(new RosterRoom { Number = "No room", Computers = Sorted(unplaced) });

        return rooms
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Number, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<RosterRoom> GroupByLocation(List<RosterComputer> computers) =>
        GroupBy(computers, c => c.Location.Length > 0 ? c.Location : (c.Area.Length > 0 ? c.Area : "Other"));

    private static List<RosterRoom> GroupBy(List<RosterComputer> computers, Func<RosterComputer, string> key) =>
        computers
            .GroupBy(key)
            .Select(g => new RosterRoom { Number = g.Key, Computers = Sorted(g) })
            .OrderBy(r => r.Number, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<RosterComputer> Sorted(IEnumerable<RosterComputer> computers) =>
        computers.OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    private static string? Dominant(IEnumerable<string> values)
    {
        var top = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .GroupBy(v => v)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return top?.Key;
    }

    private static string FirstLetter(string s) =>
        s.Length > 0 && char.IsLetter(s[0]) ? char.ToUpperInvariant(s[0]).ToString() : "#";

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>RFC-4180-ish line split: commas inside double quotes are kept, doubled quotes unescape.</summary>
    public static List<string> ParseLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }
        fields.Add(current.ToString());
        return fields;
    }
}
