using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FleetMate.Core.Inventory;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Shared;
using Microsoft.UI.Xaml;

namespace FleetMate.WinUI.Pages.Inventory;

/// <summary>One filter of the Filters flyout: its label, its choices and the one picked.</summary>
public sealed partial class AssetFilterChoice : ObservableObject
{
    private readonly Action _changed;

    public AssetFilterChoice(AssetFilter filter, Action changed)
    {
        Filter = filter;
        _changed = changed;
    }

    public AssetFilter Filter { get; }
    public string Label => AssetListQuery.Label(Filter);
    public string AutomationId => $"Inventory{Filter}Filter";

    [ObservableProperty]
    private IReadOnlyList<string> _options = new[] { AssetListQuery.All };

    [ObservableProperty]
    private string? _selected = AssetListQuery.All;

    partial void OnSelectedChanged(string? value) => _changed();
}

/// <summary>
/// The Inventory list, free of WinUI controls: the loaded assets, the search,
/// the filters and the sort (<see cref="AssetListQuery"/>), and the rows they
/// leave. The page binds to it and feeds it the toolbar search and graph clicks.
/// </summary>
public sealed partial class InventoryViewModel : ObservableObject
{
    private readonly AssetListQuery _query = new();
    private IReadOnlyList<SnipeAsset> _assets = Array.Empty<SnipeAsset>();
    private bool _rebuildingOptions;

    public InventoryViewModel(bool persistColumns)
    {
        Columns = new AssetColumnLayout(persistColumns);
        Filters = AssetListQuery.Filters.Select(f => new AssetFilterChoice(f, OnFilterChanged)).ToList();
    }

    public AssetColumnLayout Columns { get; }

    public IReadOnlyList<AssetFilterChoice> Filters { get; }

    public ObservableCollection<AssetRow> Rows { get; } = new();

    public string SortField => _query.SortField;
    public bool SortDescending => _query.SortDescending;

    [ObservableProperty]
    private string _countText = "";

    [ObservableProperty]
    private bool _hasActiveFilters;

    /// <summary>The page's empty-list message, or null when there are rows.</summary>
    [ObservableProperty]
    private string? _emptyMessage;

    public IReadOnlyList<SnipeAsset> Assets => _assets;

    /// <summary>Show <paramref name="assets"/>, keeping each filter's choice while the new assets still offer it.</summary>
    public void SetAssets(IReadOnlyList<SnipeAsset> assets, string? loadError)
    {
        _assets = assets;
        _loadError = loadError;
        _rebuildingOptions = true;
        foreach (var choice in Filters)
        {
            var keep = choice.Selected;
            choice.Options = AssetListQuery.Options(assets, choice.Filter);
            choice.Selected = keep != null && choice.Options.Contains(keep) ? keep : AssetListQuery.All;
        }
        _rebuildingOptions = false;
        Refresh();
    }

    public void SetSearch(string text)
    {
        _query.Search = text;
        Refresh();
    }

    public void SortBy(string field)
    {
        _query.SortBy(field);
        OnPropertyChanged(nameof(SortField));
        OnPropertyChanged(nameof(SortDescending));
        Refresh();
    }

    public void ClearFilters()
    {
        _rebuildingOptions = true;
        foreach (var choice in Filters) choice.Selected = AssetListQuery.All;
        _rebuildingOptions = false;
        Refresh();
    }

    /// <summary>
    /// A graph click: pick the option that matches <paramref name="value"/>,
    /// tolerating case and punctuation drift as on the Mac (a status wedge's
    /// "deployable" finds the Status Type filter's "Deployable").
    /// </summary>
    public void ApplyGraphFilter(string category, string value)
    {
        var filter = category switch
        {
            InventoryGraphCategory.Category => AssetFilter.Category,
            InventoryGraphCategory.StatusType => AssetFilter.StatusType,
            _ => (AssetFilter?)null,
        };
        if (filter is not { } f) return;
        var choice = Filters.First(c => c.Filter == f);
        var wanted = WidgetFilterValues.Match(value, choice.Options)[0];
        if (choice.Options.Contains(wanted)) choice.Selected = wanted;
    }

    private string? _loadError;

    private void OnFilterChanged()
    {
        if (!_rebuildingOptions) Refresh();
    }

    private void Refresh()
    {
        foreach (var choice in Filters) _query[choice.Filter] = choice.Selected;
        HasActiveFilters = _query.HasActiveFilters;

        var list = _query.Apply(_assets);
        Rows.Clear();
        foreach (var asset in list) Rows.Add(new AssetRow(asset, Columns));

        CountText = _loadError != null && _assets.Count == 0
            ? InventoryFigures.AssetsFailureHeadline(_loadError)
            : list.Count == _assets.Count ? $"{list.Count:N0} assets" : $"{list.Count:N0} of {_assets.Count:N0} assets";
        EmptyMessage = list.Count > 0 ? null
            : _loadError != null && _assets.Count == 0 ? null
            : _assets.Count == 0 ? "No assets found in Snipe-IT."
            : "No assets match the search and filters.";
    }

    public Visibility ClearFiltersVisibility => HasActiveFilters ? Visibility.Visible : Visibility.Collapsed;

    partial void OnHasActiveFiltersChanged(bool value) => OnPropertyChanged(nameof(ClearFiltersVisibility));
}
