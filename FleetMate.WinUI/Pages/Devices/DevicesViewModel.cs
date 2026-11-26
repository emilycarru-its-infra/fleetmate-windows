using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FleetMate.Core.Devices;
using FleetMate.Core.Models.Devices;

namespace FleetMate.WinUI.Pages.Devices;

/// <summary>
/// The Devices list's state, free of any view: every row (Intune joined to
/// Autopilot and the Apple organizations), the filters, the serial lookup,
/// the search, the sort and the selection. The joins, filters, counts and
/// which actions apply all come from FleetMate.Core, the same code
/// FleetMate.GUI runs; this holds the state and raises the changes.
/// </summary>
public sealed partial class DevicesViewModel : ObservableObject
{
    private readonly Dictionary<DeviceFacet, HashSet<string>> _facets =
        Enum.GetValues<DeviceFacet>().ToDictionary(f => f, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private List<DeviceListRow> _all = new();
    private List<string>? _lookupSerials;
    private string _search = "";
    private bool _autopilotRead;

    /// <summary>Every row, before filters.</summary>
    public IReadOnlyList<DeviceListRow> AllRows => _all;

    /// <summary>The rows the list shows, filtered, looked up, searched and sorted.</summary>
    public ObservableCollection<DeviceListRow> Rows { get; } = new();

    /// <summary>The selected rows, in list order.</summary>
    public IReadOnlyList<DeviceListRow> Selected { get; private set; } = Array.Empty<DeviceListRow>();

    [ObservableProperty]
    private string _countText = "";

    [ObservableProperty]
    private string _filtersButtonText = DeviceListText.FiltersButton(0);

    [ObservableProperty]
    private string _serialsButtonText = DeviceListText.SerialsButton(null);

    [ObservableProperty]
    private string _selectionText = "";

    [ObservableProperty]
    private bool _hasSelection;

    /// <summary>The column the list is sorted by, or null for the join's own order.</summary>
    [ObservableProperty]
    private string? _sortColumn;

    [ObservableProperty]
    private bool _sortDescending;

    /// <summary>Which action cards the selection gets.</summary>
    [ObservableProperty]
    private DeviceActionSections _sections = DeviceActionSections.For(Array.Empty<DeviceListRow>(), null);

    /// <summary>Raised after <see cref="Rows"/> is refilled, so the view can restore its selection.</summary>
    public event EventHandler? RowsChanged;

    /// <summary>The looked-up serials, or null while every device shows.</summary>
    public IReadOnlyList<string>? LookupSerials => _lookupSerials;

    public int ActiveFilterCount => _facets.Values.Sum(v => v.Count);

    public bool IsChecked(DeviceFacet facet, string value) => _facets[facet].Contains(value);

    /// <summary>
    /// The filter categories and their values with counts, Apple
    /// organization's first; the Autopilot categories once its identities are read.
    /// </summary>
    public IEnumerable<(DeviceFacet Facet, List<(string Value, int Count)> Values)> FilterGroups() =>
        Enum.GetValues<DeviceFacet>()
            .Where(f => _autopilotRead || !DeviceFacets.AutopilotOnly.Contains(f))
            .Select(f => (f, DeviceFacets.Counts(_all, f)));

    /// <summary>
    /// Join Intune, Autopilot and the Apple organizations again and check each
    /// row for discrepancies, keeping the filters, lookup and search.
    /// </summary>
    public void Rebuild(IReadOnlyList<IntuneDevice> devices, IReadOnlyList<AutopilotDevice> autopilot,
        IReadOnlyList<AppleOrgSnapshot> orgs, IReadOnlySet<string>? inventorySerials)
    {
        _autopilotRead = autopilot.Count > 0;
        _all = AppleOrgJoin.Enrich(DeviceListJoin.Merge(devices, autopilot), orgs);
        DeviceDiscrepancies.Apply(_all, new DeviceDiscrepancies.Sources(
            AutopilotRead: autopilot.Count > 0,
            AppleOrgsRead: orgs.Count > 0,
            InventorySerials: inventorySerials));
        Apply();
    }

    public void SetFacet(DeviceFacet facet, string value, bool on)
    {
        if (on) _facets[facet].Add(value);
        else _facets[facet].Remove(value);
        Apply();
    }

    /// <summary>
    /// Show only devices whose <paramref name="facet"/> is one of
    /// <paramref name="values"/> (none clears that category): how widgets and
    /// links filter the list from outside.
    /// </summary>
    public void SetFacetFilter(DeviceFacet facet, IEnumerable<string> values)
    {
        _facets[facet].Clear();
        _facets[facet].UnionWith(values);
        Apply();
    }

    public void ClearFilters()
    {
        foreach (var set in _facets.Values) set.Clear();
        Apply();
    }

    /// <summary>Narrow the list to these serials, or show every device again with null.</summary>
    public void SetSerialLookup(IReadOnlyList<string>? serials)
    {
        _lookupSerials = serials is { Count: > 0 } ? serials.ToList() : null;
        SerialsButtonText = DeviceListText.SerialsButton(_lookupSerials?.Count);
        Apply();
    }

    public void SetSearch(string? text)
    {
        var next = text?.Trim() ?? "";
        if (next == _search) return;
        _search = next;
        Apply();
    }

    public string Search => _search;

    /// <summary>Sort by <paramref name="header"/>: ascending first, then descending on a second click.</summary>
    public void SortBy(string header)
    {
        if (DeviceListColumns.Find(header) is not { Sortable: true }) return;
        if (SortColumn == header) SortDescending = !SortDescending;
        else
        {
            SortColumn = header;
            SortDescending = false;
        }
        Apply();
    }

    /// <summary>What the list shows now, from Core's filters; a hidden row also leaves the selection.</summary>
    public void Apply()
    {
        IReadOnlyList<DeviceListRow> source = _all;
        SerialLookup.Result? lookup = null;
        if (_lookupSerials != null)
        {
            lookup = SerialLookup.Apply(_all, _lookupSerials);
            source = lookup.Rows;
        }
        var visible = DeviceFacets.Apply(source, _facets);
        if (_search.Length > 0) visible = visible.Where(r => r.Matches(_search));
        var rows = SortColumn is { } column ? DeviceListColumns.Sort(visible, column, SortDescending) : visible.ToList();

        Rows.Clear();
        foreach (var row in rows) Rows.Add(row);
        CountText = DeviceListText.Count(rows.Count, _all.Count, lookup, _lookupSerials?.Count ?? 0);
        FiltersButtonText = DeviceListText.FiltersButton(ActiveFilterCount);
        RowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The selection changed: its summary and the action cards that apply to every selected device.</summary>
    public void SetSelection(IReadOnlyList<DeviceListRow> selected, AppleOrgProfile? appleProfile)
    {
        Selected = selected;
        HasSelection = selected.Count > 0;
        SelectionText = HasSelection ? $"• {selected.Count} selected" : "";
        Sections = DeviceActionSections.For(selected, appleProfile);
    }
}
