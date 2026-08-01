using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using FleetMate.Core.Models.Inventory;

namespace FleetMate.WinUI.ViewModels;

/// <summary>
/// Row display model for the Inventory (Snipe-IT assets) list. Wraps a
/// <see cref="SnipeAsset"/> with formatted columns + a status colour.
/// </summary>
public sealed class AssetRowViewModel
{
    public SnipeAsset Asset { get; }

    public AssetRowViewModel(SnipeAsset asset) => Asset = asset;

    public string Tag => string.IsNullOrEmpty(Asset.AssetTag) ? "—" : Asset.AssetTag;
    public string Name => Asset.Name ?? Asset.Model?.Name ?? "(asset)";
    public string Model => Asset.Model?.Name ?? "—";
    public string Serial => string.IsNullOrEmpty(Asset.Serial) ? "—" : Asset.Serial!;
    public string Status => Asset.StatusLabel?.Name ?? "—";
    public string AssignedTo => Asset.AssignedTo?.Name ?? "—";

    public Brush StatusBrush => new SolidColorBrush(Asset.StatusLabel?.StatusMeta?.ToLowerInvariant() switch
    {
        "deployable" => Colors.Green,
        "deployed" => Colors.DodgerBlue,
        "pending" => Colors.Gold,
        "undeployable" => Colors.Red,
        "archived" => Colors.Gray,
        _ => Colors.Gray,
    });

    public bool Matches(string q) =>
        Tag.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Name.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Serial.Contains(q, StringComparison.OrdinalIgnoreCase)
        || AssignedTo.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Model.Contains(q, StringComparison.OrdinalIgnoreCase);
}
