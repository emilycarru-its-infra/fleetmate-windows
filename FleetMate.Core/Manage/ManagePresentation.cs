using CommunityToolkit.Mvvm.ComponentModel;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Manage;
using FleetMate.Core.Services.Manage;
using FleetMate.Core.Services.Projects;
using Serilog;

namespace FleetMate.Core.Manage;

/// <summary>
/// A sidebar room row, macOS density: section icon, title, and an
/// "area · room · N machines" caption. For Curriculum the title is the
/// roster's fleet value (the room number when the row has no fleet); the
/// caption's area is the most common lease area among members and its room
/// the most common location, omitted when it equals the title. Shared by the
/// WPF and WinUI Manage tabs.
/// </summary>
public partial class SidebarRoom : ObservableObject
{
    public RosterRoom Room { get; init; } = new();
    public RosterSection Section { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    /// <summary>The Segoe Fluent Icons glyph for the row's section.</summary>
    public string Glyph { get; init; } = "";

    /// <summary>True while a sidebar row drag hovers this row; the row paints its drop line.</summary>
    [ObservableProperty] private bool _isDropTarget;

    public static SidebarRoom From(RosterRoom room, RosterSection section)
    {
        // Curriculum titles by the roster's fleet value, falling back to the
        // room number when the row has no fleet — read from the members, since
        // a fleet-less room's DisplayName carries its area, not a fleet.
        var fleet = Dominant(room.Computers.Select(c => c.Fleet));
        var title = section == RosterSection.Labs && fleet is { Length: > 0 } ? fleet : room.Number;

        var area = Dominant(room.Computers.Select(c => c.Area));
        var location = Dominant(room.Computers.Select(c => c.Location));

        var parts = new List<string>();
        if (area is { Length: > 0 } && !string.Equals(area, title, StringComparison.OrdinalIgnoreCase)) parts.Add(area);
        if (location is { Length: > 0 } && !string.Equals(location, title, StringComparison.OrdinalIgnoreCase)) parts.Add(location);
        parts.Add(ManageText.Machines(room.Count));

        return new SidebarRoom
        {
            Room = room,
            Section = section,
            Title = title,
            Subtitle = string.Join(" · ", parts),
            Glyph = SectionGlyph(section),
        };
    }

    public static string SectionGlyph(RosterSection section) => section switch
    {
        RosterSection.Labs => "\uE825",
        RosterSection.Kiosks => "\uE7F4",
        RosterSection.Staff => "\uE716",
        RosterSection.Faculty => "\uE7BE",
        _ => "\uE825",
    };

    private static string? Dominant(IEnumerable<string> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v))
              .GroupBy(v => v)
              .OrderByDescending(g => g.Count())
              .FirstOrDefault()?.Key;
}

/// <summary>
/// Sidebar row order: rows drag onto another row in the same section and take
/// its slot; the order persists per section as row titles. Rows missing from
/// the saved list keep the roster's order after the arranged ones, so new labs
/// append rather than disappear.
/// </summary>
public static class SidebarOrder
{
    /// <summary>Saved order first (matching by title), then the rest in roster order.</summary>
    public static List<SidebarRoom> Apply(IReadOnlyList<string>? saved, IReadOnlyList<SidebarRoom> rows)
    {
        if (saved == null || saved.Count == 0) return rows.ToList();
        var ordered = new List<SidebarRoom>();
        foreach (var id in saved)
        {
            var hit = rows.FirstOrDefault(r => r.Title == id && !ordered.Contains(r));
            if (hit != null) ordered.Add(hit);
        }
        ordered.AddRange(rows.Where(r => !ordered.Contains(r)));
        return ordered;
    }

    /// <summary>
    /// <paramref name="dragged"/> moved onto <paramref name="target"/>'s slot:
    /// the target moves down when the drag came from below it, up when from
    /// above. Null when the move changes nothing.
    /// </summary>
    public static List<SidebarRoom>? Move(IReadOnlyList<SidebarRoom> rows, SidebarRoom dragged, SidebarRoom target)
    {
        if (ReferenceEquals(dragged, target)) return null;
        var list = rows.ToList();
        var targetIndex = list.IndexOf(target);
        if (targetIndex < 0 || !list.Remove(dragged)) return null;
        list.Insert(Math.Min(targetIndex, list.Count), dragged);
        return list;
    }
}

/// <summary>The results pane's filter, as on Mac: All, Success, Failed, Offline.</summary>
public enum ResultFilter { All, Success, Failed, Offline }

/// <summary>The Manage tab's wording and small decisions, shared by both apps.</summary>
public static class ManageText
{
    /// <summary>"30 machines", "1 machine".</summary>
    public static string Machines(int count) => count == 1 ? "1 machine" : $"{count} machines";

    /// <summary>The header line under the machine list: "3 selected · 12 online".</summary>
    public static string SelectionCount(int selected, int online) =>
        selected == 0 ? $"{online} online" : $"{selected} selected · {online} online";

    /// <summary>"22 labs · 445 machines · Platform/Agent · fetched 14:32", or why there is no roster.</summary>
    public static string RosterFooter(ManageViewModel vm, string provenance) =>
        vm.RosterLoaded
            ? $"{vm.Roster.Labs.Count} labs · {vm.Roster.Source.Count} machines · {provenance}"
            : vm.RosterStatus is { Length: > 0 } status ? status : "Check the roster path in Settings › Manage";

    /// <summary>The empty state's second line.</summary>
    public static string EmptyHint(ManageViewModel vm, bool sshConfigured) =>
        !vm.RosterLoaded ? vm.RosterStatus
        : !sshConfigured ? "Rooms load from the roster. Machine details need the SSH key configured in Settings (Manage)."
        : "Rooms come from the roster configured in Settings.";

    public static string ResultsTitle(string lastRunLabel) => lastRunLabel.Length > 0 ? $"Results: {lastRunLabel}" : "Results";

    public static string ResultCounts(ManageViewModel vm) =>
        vm.Results.Count == 0 ? "" : $"{vm.ResultSuccessCount} ok · {vm.ResultFailedCount} failed · {vm.ResultOfflineCount} offline";

    /// <summary>The trust badge: "Caution (inferred)".</summary>
    public static string TrustLabel(CommandTrustLevel trust, bool inferred) => trust.Label() + (inferred ? " (inferred)" : "");

    public static bool IsFailure(CommandRunStatus status) =>
        status is CommandRunStatus.Failed or CommandRunStatus.AuthFailed or CommandRunStatus.Timeout;

    public static IEnumerable<CommandResultViewModel> Filter(IEnumerable<CommandResultViewModel> results, ResultFilter filter) => filter switch
    {
        ResultFilter.Success => results.Where(r => r.Status == CommandRunStatus.Success),
        ResultFilter.Failed => results.Where(r => IsFailure(r.Status)),
        ResultFilter.Offline => results.Where(r => r.Status == CommandRunStatus.Offline),
        _ => results,
    };

    /// <summary>"Select failed": the serials whose run failed or never connected, to run again.</summary>
    public static HashSet<string> FailedSerials(IEnumerable<CommandResultViewModel> results) =>
        results.Where(r => IsFailure(r.Status) || r.Status == CommandRunStatus.Offline).Select(r => r.Serial).ToHashSet();

    /// <summary>Rows a "selected" action works on: checked rows win; otherwise the highlighted ones; otherwise every row.</summary>
    public static List<MachineRowViewModel> TargetRows(ManageViewModel vm, IEnumerable<MachineRowViewModel> highlighted)
    {
        var checkedRows = vm.SelectedRows.ToList();
        if (checkedRows.Count > 0) return checkedRows;
        var picked = highlighted.ToList();
        return picked.Count > 0 ? picked : vm.Rows.ToList();
    }

    public static string HostnameOf(MachineRowViewModel row) => row.Computer.HasHostname ? row.Computer.Hostname : row.Name;

    public static string HostnameOf(CommandResultViewModel result) => result.Hostname.Length > 0 ? result.Hostname : result.Name;

    /// <summary>
    /// A roster-shaped CSV yields hostname (or serial when there is no hostname);
    /// anything else is passed through as lines. The Add Device import.
    /// </summary>
    public static string ExtractDeviceLines(string content)
    {
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 2) return content;
        var header = RosterLoader.ParseLine(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
        var iHost = header.IndexOf("hostname");
        var iSerial = header.IndexOf("serial");
        var iAlloc = header.IndexOf("allocation");
        if (iHost < 0 && iSerial < 0) return content;

        var result = new List<string>();
        foreach (var line in lines.Skip(1))
        {
            var f = RosterLoader.ParseLine(line);
            string Get(int i) => i >= 0 && i < f.Count ? f[i].Trim() : "";
            var host = Get(iHost);
            if (host.Length == 0 && iHost < 0) host = Get(iAlloc);
            var value = host.Length > 0 ? host : Get(iSerial);
            if (value.Length > 0) result.Add(value);
        }
        return string.Join(Environment.NewLine, result);
    }

    /// <summary>
    /// The Add Device import: each pasted line as a group device, matched to the
    /// roster by hostname or serial so it keeps the roster's identity. Returns
    /// how many were added and how many were already there or invalid.
    /// </summary>
    public static (int Added, int Skipped) ImportDevices(ManageViewModel vm, CustomGroup group, string text)
    {
        int added = 0, skipped = 0;
        foreach (var (hostname, ip) in ManageViewModel.ParseDeviceLines(text))
        {
            var roster = hostname.Length > 0
                ? vm.Roster.Source.FirstOrDefault(c => c.Hostname.Equals(hostname, StringComparison.OrdinalIgnoreCase) || c.Serial.Equals(hostname, StringComparison.OrdinalIgnoreCase))
                : null;
            var name = roster != null && roster.HasHostname ? roster.Hostname : hostname;
            if (vm.AddDevice(group, name, ip, roster?.Serial ?? "")) added++; else skipped++;
        }
        return (added, skipped);
    }

    /// <summary>"Added 3 to "Lab 2", 1 already there or invalid".</summary>
    public static string Added(int added, int skipped, string group) =>
        $"Added {added} to \"{group}\"" + (skipped > 0 ? $", {skipped} already there or invalid" : "");
}

/// <summary>
/// Where the Manage roster comes from: fetched from its source repository
/// through Azure DevOps on load and on Reload and cached per user, with the
/// configured local file as the fallback. Both apps show the provenance in the
/// sidebar footer.
/// </summary>
public static class RosterSource
{
    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FleetMate", "cache", "computers.csv");

    /// <summary>"cached 14:32", or null when nothing is cached.</summary>
    public static string? CachedProvenance(string cachePath) =>
        File.Exists(cachePath) ? $"cached {File.GetLastWriteTime(cachePath):HH:mm}" : null;

    private static string FetchFailed(string cachePath) =>
        CachedProvenance(cachePath) is { } cached ? $"{cached} (fetch failed)" : "local file (fetch failed)";

    /// <summary>
    /// Fetch the roster into <paramref name="cachePath"/>. Returns whether a
    /// fresh copy was written and the provenance to show either way.
    /// </summary>
    public static async Task<(bool Fetched, string Provenance)> FetchAsync(FleetMateConfig config, string cachePath)
    {
        var manage = config.Manage ?? new ManageConfig();
        if (!manage.FetchesRoster) return (false, "local file");
        var devOps = config.AzureDevOps;
        if (devOps == null || string.IsNullOrWhiteSpace(devOps.Organization)) return (false, "local file (DevOps not configured)");
        try
        {
            using var service = new AzureDevOpsService(devOps);
            var content = await service.GetRepositoryItemContentAsync(manage.RosterRepoProject, manage.RosterRepo, manage.RosterRepoPath);
            if (string.IsNullOrWhiteSpace(content)) return (false, FetchFailed(cachePath));
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await File.WriteAllTextAsync(cachePath, content);
            return (true, $"{manage.RosterRepoProject}/{manage.RosterRepo} · fetched {DateTime.Now:HH:mm}");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Roster fetch failed");
            return (false, FetchFailed(cachePath));
        }
    }
}

/// <summary>One labelled value of the machine inspector.</summary>
public sealed record DetailField(string Label, string Value);

/// <summary>One group of the machine inspector: Roster, Network, Session, Remote access, Running applications.</summary>
public sealed record DetailSection(string Title, IReadOnlyList<DetailField> Fields, string? Text = null);

/// <summary>
/// The machine inspector's sections, as FleetMate for Mac groups them. Empty
/// values are left out, so a section only lists what is known.
/// </summary>
public static class MachineDetail
{
    public static IReadOnlyList<DetailSection> Sections(MachineRowViewModel row, bool scanned)
    {
        var c = row.Computer;
        var sections = new List<DetailSection>
        {
            Section("Roster",
                ("Serial", c.IsAdhoc ? "ad hoc" : c.Serial), ("Asset", c.Asset), ("Catalog", c.Catalog), ("Area", c.Area),
                ("Location", c.Location), ("Fleet", c.Fleet), ("Usage", c.Usage), ("Status", c.Status), ("Username", c.Username)),
        };

        if (row.HasAddress)
            sections.Add(Section("Network",
                ("Address", row.Ip), ("Source", row.SourceLabel), ("SSH (22)", row.SshOpen ? "answers" : "no answer"),
                ("RDP (3389)", row.RdpOpen ? "answers" : "no answer")));
        else
            sections.Add(new DetailSection("Network", Array.Empty<DetailField>(), scanned ? "No address found" : "Not scanned yet"));

        if (row.Probe is { } p)
        {
            sections.Add(Section("Session",
                ("Console user", p.AtLoginWindow ? "Sign-in screen" : p.ConsoleUser), ("Windows", p.OsVersion), ("Uptime", p.Uptime),
                ("Join", row.JoinLabel), ("Client identifier", p.CimianClientIdentifier), ("Last managed run", p.CimianLastRun),
                ("Free space", p.FreeSpaceGb.Length > 0 ? $"{p.FreeSpaceGb} GB" : ""), ("Fetched", p.FetchedAt.ToString("HH:mm"))));
            sections.Add(Section("Remote access",
                ("Remote Desktop", p.RdpEnabled ? (p.RdpNlaRequired ? "enabled (NLA)" : "enabled") : "disabled"),
                ("RDP port", p.RdpPortListening ? "listening" : "not listening"),
                ("SSH port", p.SshPortListening ? "listening" : "not listening"), ("sshd", p.SshdStatus)));
            if (p.TopApps.Count > 0)
                sections.Add(new DetailSection("Running applications", Array.Empty<DetailField>(), string.Join(", ", p.TopApps)));
        }
        return sections;
    }

    private static DetailSection Section(string title, params (string Label, string Value)[] fields) =>
        new(title, fields.Where(f => !string.IsNullOrWhiteSpace(f.Value)).Select(f => new DetailField(f.Label, f.Value)).ToList());
}

/// <summary>The machine list's sortable columns.</summary>
public enum MachineColumn { Status, Machine, Hostname, Address, User, Os, Uptime, Join, ClientId, RemoteAccess }

/// <summary>Sorting the machine list by a column header, as the WPF grid and the Mac table do.</summary>
public static class MachineSort
{
    public static IEnumerable<MachineRowViewModel> Apply(IEnumerable<MachineRowViewModel> rows, MachineColumn? column, bool ascending)
    {
        if (column is not { } col) return rows;
        Func<MachineRowViewModel, string> key = col switch
        {
            MachineColumn.Status => r => r.StatusKey,
            MachineColumn.Hostname => r => r.Hostname,
            MachineColumn.Address => r => AddressKey(r.Ip),
            MachineColumn.User => r => r.UserLabel,
            MachineColumn.Os => r => r.OsLabel,
            MachineColumn.Uptime => r => r.UptimeLabel,
            MachineColumn.Join => r => r.JoinLabel,
            MachineColumn.ClientId => r => r.CimianLabel,
            MachineColumn.RemoteAccess => r => r.RemoteAccessLabel,
            _ => r => r.FriendlyName,
        };
        return ascending
            ? rows.OrderBy(key, StringComparer.OrdinalIgnoreCase)
            : rows.OrderByDescending(key, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Addresses sort numerically: 192.0.2.9 before 192.0.2.10.</summary>
    internal static string AddressKey(string ip) =>
        System.Net.IPAddress.TryParse(ip, out var a) && a.GetAddressBytes() is { Length: 4 } b
            ? string.Concat(b.Select(x => x.ToString("D3")))
            : ip;
}
