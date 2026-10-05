using System.Text.Json;

namespace FleetMate.Core.Models.Inventory;

/// <summary>
/// Which Inventory list columns are showing (macOS parity: the Columns menu).
/// Every column shows by default, and the last visible one can't be hidden.
/// The choice is kept per user in a small JSON file.
/// </summary>
public sealed class InventoryColumns
{
    /// <summary>The list's columns, by header, in display order.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        "Asset Tag", "Name", "Serial", "Model", "Status", "Assigned To", "Category", "Platform",
        "Manufacturer", "Usage", "Catalog", "Area", "Location", "Last Activity",
    };

    private readonly HashSet<string> _visible;

    public InventoryColumns(IEnumerable<string>? visible = null)
    {
        _visible = new HashSet<string>((visible ?? All).Where(All.Contains), StringComparer.Ordinal);
        if (_visible.Count == 0) _visible.UnionWith(All);
    }

    public bool IsVisible(string header) => _visible.Contains(header);

    /// <summary>Visible headers in display order.</summary>
    public IReadOnlyList<string> Visible => All.Where(_visible.Contains).ToList();

    /// <summary>Show or hide a column. Returns false when hiding would leave none showing.</summary>
    public bool Set(string header, bool visible)
    {
        if (!All.Contains(header)) return false;
        if (visible) return _visible.Add(header) || true;
        if (_visible.Count == 1 && _visible.Contains(header)) return false;
        _visible.Remove(header);
        return true;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FleetMate", "inventory-columns.json");

    /// <summary>The saved choice, or every column when there is none or it can't be read.</summary>
    public static InventoryColumns Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return new InventoryColumns();
            return new InventoryColumns(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new InventoryColumns();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(Visible));
    }
}

/// <summary>The Inventory Location filter: every location the loaded assets sit in.</summary>
public static class AssetLocationFilter
{
    public const string All = "All";

    /// <summary>"All", then each distinct location name, sorted.</summary>
    public static IReadOnlyList<string> Options(IEnumerable<SnipeAsset> assets) =>
        new[] { All }.Concat(assets.Select(a => a.LocationName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// With a location chosen, only assets at it pass. An asset with no
    /// location never matches a chosen one (as on the Mac).
    /// </summary>
    public static bool Matches(SnipeAsset asset, string? selected) =>
        string.IsNullOrEmpty(selected) || selected == All || asset.LocationName == selected;
}
