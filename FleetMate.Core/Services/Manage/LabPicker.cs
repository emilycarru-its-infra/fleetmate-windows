using FleetMate.Core.Models.Manage;

namespace FleetMate.Core.Services.Manage;

/// <summary>An area of the roster and its labs, for the lab picker.</summary>
public sealed record LabArea(string Name, IReadOnlyList<RosterRoom> Rooms)
{
    public int ComputerCount => Rooms.Sum(r => r.Count);
}

/// <summary>
/// The lab picker's logic (macOS parity): labs grouped by area, a filter,
/// the drag payloads a lab or a whole area carries into the selection, and
/// the counts the picker shows. The view only draws what this computes.
/// </summary>
public static class LabPicker
{
    /// <summary>Drag payloads carry a marker so a stray text drop cannot select anything.</summary>
    public const string DragPrefix = "fleetmate-lab-picker:";

    public const string OtherArea = "Other";

    /// <summary>The area a lab belongs to: the one most of its machines report.</summary>
    public static string AreaOf(RosterRoom room) =>
        room.Computers
            .Select(c => c.Area?.Trim())
            .Where(a => !string.IsNullOrEmpty(a))
            .GroupBy(a => a!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key)
            .FirstOrDefault() ?? OtherArea;

    /// <summary>Labs grouped by area, areas and labs in natural order.</summary>
    public static List<LabArea> Group(IEnumerable<RosterRoom> labs) =>
        labs.GroupBy(AreaOf, StringComparer.OrdinalIgnoreCase)
            .Select(g => new LabArea(g.Key, g.OrderBy(r => r.Number, NaturalComparer.Instance).ToList()))
            .OrderBy(a => a.Name == OtherArea ? 1 : 0)
            .ThenBy(a => a.Name, NaturalComparer.Instance)
            .ToList();

    /// <summary>
    /// Areas whose name matches keep every lab; otherwise only the labs whose
    /// number or name matches, and areas with none are dropped.
    /// </summary>
    public static List<LabArea> Filter(IReadOnlyList<LabArea> areas, string? query)
    {
        var q = query?.Trim() ?? "";
        if (q.Length == 0) return areas.ToList();
        var result = new List<LabArea>();
        foreach (var area in areas)
        {
            if (area.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) { result.Add(area); continue; }
            var rooms = area.Rooms.Where(r =>
                r.Number.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (r.DisplayName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
            if (rooms.Count > 0) result.Add(area with { Rooms = rooms });
        }
        return result;
    }

    public static string RoomPayload(RosterRoom room) => DragPrefix + "room:" + room.Id;
    public static string AreaPayload(LabArea area) => DragPrefix + "area:" + area.Name;

    /// <summary>
    /// Add what a drop carries to <paramref name="selectedIds"/>: one lab, or
    /// every lab in an area. Anything without the marker, or naming a lab or
    /// area that does not exist, is ignored. Returns true when it was ours.
    /// </summary>
    public static bool ApplyDrop(ISet<string> selectedIds, string? payload, IReadOnlyList<LabArea> areas)
    {
        if (payload == null || !payload.StartsWith(DragPrefix, StringComparison.Ordinal)) return false;
        var value = payload[DragPrefix.Length..];
        if (value.StartsWith("room:", StringComparison.Ordinal))
        {
            var id = value["room:".Length..];
            if (!areas.SelectMany(a => a.Rooms).Any(r => r.Id == id)) return false;
            selectedIds.Add(id);
            return true;
        }
        if (value.StartsWith("area:", StringComparison.Ordinal))
        {
            var name = value["area:".Length..];
            var area = areas.FirstOrDefault(a => a.Name == name);
            if (area == null) return false;
            foreach (var room in area.Rooms) selectedIds.Add(room.Id);
            return true;
        }
        return false;
    }

    /// <summary>The selected labs in natural order.</summary>
    public static List<RosterRoom> Selected(IEnumerable<RosterRoom> labs, IReadOnlySet<string> selectedIds) =>
        labs.Where(r => selectedIds.Contains(r.Id)).OrderBy(r => r.Number, NaturalComparer.Instance).ToList();

    /// <summary>"3 labs · 41 machines".</summary>
    public static string Summary(IReadOnlyCollection<RosterRoom> selected) =>
        $"{selected.Count} lab{(selected.Count == 1 ? "" : "s")} · {selected.Sum(r => r.Count)} machines";

    /// <summary>Machines across the selected labs, unique by serial.</summary>
    public static List<RosterComputer> Computers(IEnumerable<RosterRoom> rooms) =>
        rooms.SelectMany(r => r.Computers).GroupBy(c => c.Serial).Select(g => g.First()).ToList();

    /// <summary>Orders "B2" before "B10", like Finder's localizedStandardCompare.</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            x ??= ""; y ??= "";
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    var a = x[si..i].TrimStart('0');
                    var b = y[sj..j].TrimStart('0');
                    if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                    var c = string.CompareOrdinal(a, b);
                    if (c != 0) return c;
                }
                else
                {
                    var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
