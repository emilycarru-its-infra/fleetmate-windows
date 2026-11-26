using System.ComponentModel;
using FleetMate.Core.Inventory;
using FleetMate.Core.Models.Inventory;

namespace FleetMate.WinUI.Pages.Inventory;

/// <summary>
/// Which list columns show, and how wide each is (the WPF list's widths). Rows
/// bind each cell's visibility here, so the Columns menu shows or hides a
/// column in every row at once. Kept per user (<see cref="InventoryColumns"/>).
/// </summary>
public sealed partial class AssetColumnLayout : INotifyPropertyChanged
{
    /// <summary>Each column's width in DIPs, by header.</summary>
    public static readonly IReadOnlyDictionary<string, double> Widths = new Dictionary<string, double>
    {
        ["Asset Tag"] = 112, ["Name"] = 200, ["Serial"] = 132, ["Model"] = 200, ["Status"] = 128,
        ["Assigned To"] = 148, ["Category"] = 112, ["Platform"] = 108, ["Manufacturer"] = 120, ["Usage"] = 96,
        ["Catalog"] = 96, ["Area"] = 96, ["Location"] = 128, ["Last Activity"] = 152,
    };

    private InventoryColumns _columns;
    private readonly bool _persist;

    /// <param name="persist">Keep the choice between launches; a demo or QA capture never writes it.</param>
    public AssetColumnLayout(bool persist)
    {
        _persist = persist;
        _columns = persist ? InventoryColumns.Load() : new InventoryColumns();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsVisible(string header) => _columns.IsVisible(header);

    public IReadOnlyList<string> Visible => _columns.Visible;

    /// <summary>The width every row spans: the visible columns and the row's padding.</summary>
    public double RowWidth => _columns.Visible.Sum(h => Widths[h]) + 24;

    /// <summary>Show or hide a column; false when it is the last one showing, which stays.</summary>
    public bool Set(string header, bool visible)
    {
        if (!_columns.Set(header, visible)) return false;
        if (_persist)
        {
            try { _columns.Save(); }
            catch (Exception ex) { Serilog.Log.Debug(ex, "Inventory: could not save the column layout"); }
        }
        // Every cell's visibility, at once.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        return true;
    }

    private bool V(string header) => _columns.IsVisible(header);

    public bool AssetTag => V("Asset Tag");
    public bool Name => V("Name");
    public bool Serial => V("Serial");
    public bool Model => V("Model");
    public bool Status => V("Status");
    public bool AssignedTo => V("Assigned To");
    public bool Category => V("Category");
    public bool Platform => V("Platform");
    public bool Manufacturer => V("Manufacturer");
    public bool Usage => V("Usage");
    public bool Catalog => V("Catalog");
    public bool Area => V("Area");
    public bool Location => V("Location");
    public bool LastActivity => V("Last Activity");
}

/// <summary>One list row: an asset's cells, read through <see cref="AssetListQuery.Cell"/>.</summary>
public sealed class AssetRow
{
    public AssetRow(SnipeAsset asset, AssetColumnLayout columns)
    {
        Asset = asset;
        Columns = columns;
    }

    public SnipeAsset Asset { get; }
    public AssetColumnLayout Columns { get; }

    public string AssetTag => AssetListQuery.Cell(Asset, "Asset Tag");
    public string Name => AssetListQuery.Cell(Asset, "Name");
    public string Serial => AssetListQuery.Cell(Asset, "Serial");
    public string Model => AssetListQuery.Cell(Asset, "Model");
    public string Status => AssetListQuery.Cell(Asset, "Status");
    public string AssignedTo => AssetListQuery.Cell(Asset, "Assigned To");
    public string Category => AssetListQuery.Cell(Asset, "Category");
    public string Platform => AssetListQuery.Cell(Asset, "Platform");
    public string Manufacturer => AssetListQuery.Cell(Asset, "Manufacturer");
    public string Usage => AssetListQuery.Cell(Asset, "Usage");
    public string Catalog => AssetListQuery.Cell(Asset, "Catalog");
    public string Area => AssetListQuery.Cell(Asset, "Area");
    public string Location => AssetListQuery.Cell(Asset, "Location");
    public string LastActivity => AssetListQuery.Cell(Asset, "Last Activity");

    /// <summary>What a screen reader says for the row.</summary>
    public string Description =>
        string.Join(", ", new[] { Name, AssetTag, Model, Status, AssignedTo }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>The status dot: the same colours as the detail's status badge.</summary>
    public Microsoft.UI.Xaml.Media.Brush StatusBrush => InventoryUi.StatusBrush(Asset.StatusLabel);

    public override string ToString() => Description;
}
