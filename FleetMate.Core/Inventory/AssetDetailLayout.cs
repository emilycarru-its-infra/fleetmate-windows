using System.Globalization;
using FleetMate.Core.Models.Inventory;

namespace FleetMate.Core.Inventory;

/// <summary>One taxonomy group of the asset detail: its slug, title, Segoe Fluent glyph and the fields seeded into it.</summary>
public sealed record AssetFieldGroup(string Slug, string Title, string Glyph, bool Mono, IReadOnlyList<string> Fields);

/// <summary>
/// One row of an asset card. <see cref="EditKey"/> is the Snipe-IT API field
/// an edit PATCHes, or null when the row is read-only. <see cref="AlwaysShow"/>
/// rows show "—" when empty; other rows are left out.
/// </summary>
public sealed record AssetDetailRow(string Label, string? Value, bool Copyable = false, bool Mono = false,
    string? EditKey = null, bool AlwaysShow = false)
{
    public bool Shows => AlwaysShow || !string.IsNullOrEmpty(Value);
}

/// <summary>A lifecycle bar: title, the trailing milestone text, and the share of the span elapsed (0…1).</summary>
public sealed record AssetLifecycleBar(string Title, string Trailing, double Fraction)
{
    /// <summary>The span is over: drawn as overdue rather than running.</summary>
    public bool Overdue => Fraction >= 1;
}

/// <summary>How an asset's status badge is coloured, by Snipe-IT status meta.</summary>
public enum AssetStatusTone { Deployed, Deployable, Pending, Other }

/// <summary>
/// What the asset detail shows, shared by both Windows apps: the taxonomy
/// cards (Inventory, Status, Assignment, Hardware, Procurement, Management,
/// Networking, Identity, Other, Metadata), the lifecycle bars, the icons and
/// the History segment's text. The macOS AssetDetailSidebar's rules.
/// </summary>
public static class AssetDetailLayout
{
    /// <summary>
    /// The six groups the Snipe-IT fork seeds, in its render order. The
    /// server's field_group slug on a custom field wins when present; this
    /// mirror only decides where a field lands when the API doesn't say.
    /// </summary>
    public static readonly IReadOnlyList<AssetFieldGroup> FieldGroups = new AssetFieldGroup[]
    {
        new("inventory", "Inventory", "", false, new[] { "Device Management Service", "Fleet", "Catalog" }),
        new("specs", "Specs", "", false, new[]
        {
            "Platform", "Colour", "Display", "Display Resolution", "Memory", "Storage", "Chip", "CPU", "GPU", "NPU",
            "Architecture", "Cellular", "Version",
        }),
        new("management", "Management", "", false, Array.Empty<string>()),
        new("networking", "Networking", "", false, new[]
        {
            "Hostname", "Address", "Driver", "Queue(s)", "Virtual Queue", "Toner Contract", "Options",
        }),
        new("procurement", "Procurement", "", false, new[] { "License Type" }),
        new("identity", "Identity", "", true, new[]
        {
            "Entra ID", "Intune ID", "Defender ID", "Object ID", "Micro ID", "Identifier", "IMEI", "Enrollment Status",
        }),
    };

    private static readonly HashSet<string> HiddenFields = new(StringComparer.OrdinalIgnoreCase) { "Username" };

    public static AssetFieldGroup Group(string slug) => FieldGroups.First(g => g.Slug == slug);

    // MARK: - Glyphs

    public const string StatusGlyph = "";
    public const string AssignmentGlyph = "";
    public const string HardwareGlyph = "";
    public const string MetadataGlyph = "";
    public const string NotesGlyph = "";
    public const string OtherGlyph = "";
    public const string HandbookGlyph = "";
    public const string PersonGlyph = "";
    public const string PlaceGlyph = "";
    public const string CopyGlyph = "";
    public const string EditGlyph = "";
    public const string SaveGlyph = "";
    public const string CancelGlyph = "";
    public const string AttachmentGlyph = "";

    /// <summary>The hero icon for a category.</summary>
    public static string CategoryGlyph(string? category)
    {
        var name = (category ?? "").ToLowerInvariant();
        if (name.Contains("laptop")) return "";
        if (name.Contains("display") || name.Contains("monitor")) return "";
        if (name.Contains("desktop")) return "";
        if (name.Contains("phone")) return "";
        if (name.Contains("printer")) return "";
        if (name.Contains("camera")) return "";
        if (name.Contains("audio") || name.Contains("speaker")) return "";
        if (name.Contains("network") || name.Contains("switch") || name.Contains("router")) return "";
        return "";
    }

    /// <summary>The icon for a history event, by its action.</summary>
    public static string ActionGlyph(string action)
    {
        var a = action.ToLowerInvariant();
        if (a.StartsWith("checkout")) return "";
        if (a.StartsWith("checkin")) return "";
        if (a.Contains("audit")) return "";
        if (a.Contains("upload")) return "";
        if (a.Contains("create")) return "";
        if (a.Contains("delete")) return "";
        if (a.Contains("restore")) return "";
        if (a.Contains("request")) return "";
        if (a.Contains("update")) return "";
        return "";
    }

    public static AssetStatusTone StatusTone(SnipeStatusLabel? status) => status?.StatusMeta switch
    {
        "deployed" => AssetStatusTone.Deployed,
        "deployable" => AssetStatusTone.Deployable,
        "pending" => AssetStatusTone.Pending,
        _ => AssetStatusTone.Other,
    };

    // MARK: - Header

    /// <summary>Re-Allocate shows for an asset that is checked out, or ready to be.</summary>
    public static bool CanReAllocate(SnipeAsset asset) =>
        asset.AssignedTo != null || asset.StatusLabel?.StatusMeta == "deployable";

    /// <summary>
    /// Who has it and where it is. An asset checked out to a room is not
    /// assigned to a person named after the room: when the assignee is a
    /// location, only the place line shows.
    /// </summary>
    public static (string? Person, string? Place) WhoWhere(SnipeAsset asset)
    {
        var toLocation = asset.AssignedTo?.Type == "location";
        var person = asset.AssignedTo?.Name is { Length: > 0 } who && !toLocation ? who : null;
        var place = asset.LocationName ?? (toLocation ? asset.AssignedTo?.Name : null);
        return (person, string.IsNullOrEmpty(place) ? null : place);
    }

    /// <summary>The Device EOL and Warranty bars, each when its span is known.</summary>
    public static List<AssetLifecycleBar> Lifecycle(SnipeAsset asset, DateTime today)
    {
        var bars = new List<AssetLifecycleBar>();
        var purchase = ParseDate(asset.PurchaseDate?.Date);
        var eol = ParseDate(asset.AssetEolDate?.Date);
        var warranty = ParseDate(asset.WarrantyExpires?.Date);

        if (LifecycleFraction(purchase, eol, today) is { } eolFraction && eol is { } eolDate)
        {
            var monthsLeft = Math.Abs(((eolDate.Year - today.Year) * 12) + eolDate.Month - today.Month);
            bars.Add(new("Device EOL", $"{monthsLeft} months left · {(int)(eolFraction * 100)}%", eolFraction));
        }
        if (LifecycleFraction(purchase, warranty, today) is { } warrantyFraction)
            bars.Add(new("Warranty", $"{asset.WarrantyExpires?.Formatted ?? ""} · {(int)(warrantyFraction * 100)}%", warrantyFraction));
        return bars;
    }

    /// <summary>"yyyy-MM-dd" at the start of a Snipe-IT date, or null.</summary>
    public static DateTime? ParseDate(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length < 10) return null;
        return DateTime.TryParseExact(raw[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : null;
    }

    /// <summary>Elapsed share of the start→end span on <paramref name="today"/>, clamped 0…1; null when the span is unknown.</summary>
    public static double? LifecycleFraction(DateTime? start, DateTime? end, DateTime today)
    {
        if (start is not { } from || end is not { } to || to <= from) return null;
        var elapsed = (today - from).TotalDays / (to - from).TotalDays;
        return Math.Min(Math.Max(elapsed, 0), 1);
    }

    // MARK: - Cards

    public static List<AssetDetailRow> InventoryRows(SnipeAsset asset)
    {
        var rows = new List<AssetDetailRow>();
        foreach (var (name, field) in FieldsInGroup(asset, Group("inventory")))
        {
            rows.Add(new(name, field.Value, Copyable: true, EditKey: field.Field, AlwaysShow: true));
            // Web order: Location sits between Catalog and Usage.
            if (name == "Catalog") rows.Add(new("Location", asset.RtdLocation?.Name, AlwaysShow: true));
        }
        // Native since the fork's F2 migration, like the lease cluster.
        rows.Add(new("Usage", asset.LeaseUsage, Copyable: true, EditKey: "lease_usage", AlwaysShow: true));
        rows.Add(new("Area", asset.LeaseArea, Copyable: true, EditKey: "lease_area", AlwaysShow: true));
        return rows;
    }

    public static List<AssetDetailRow> AssignmentRows(SnipeAsset asset) => new()
    {
        new("Assigned To", asset.AssignedTo?.Name),
        new("Email", asset.AssignedTo?.Email, Copyable: true),
        new("Employee #", asset.AssignedTo?.EmployeeNumber),
    };

    public static List<AssetDetailRow> HardwareRows(SnipeAsset asset)
    {
        var rows = new List<AssetDetailRow>
        {
            new("Serial", asset.Serial, Copyable: true, Mono: true, EditKey: "serial"),
            new("Model", asset.Model?.Name),
            new("Category", asset.Category?.Name),
            new("Manufacturer", asset.Manufacturer?.Name),
        };
        rows.AddRange(FieldsInGroup(asset, Group("specs"))
            .Select(f => new AssetDetailRow(f.Name, f.Field.Value, Copyable: true, EditKey: f.Field.Field, AlwaysShow: true)));
        return rows;
    }

    public static List<AssetDetailRow> ProcurementRows(SnipeAsset asset)
    {
        var rows = new (string Label, string? Value, string? EditKey)[]
        {
            ("Ownership Type", asset.OwnershipType, "ownership_type"),
            ("Lease Contract ID", asset.LeaseContractId, "lease_contract_id"),
            ("Lease Contract Name", asset.LeaseContractName, "lease_contract_name"),
            ("Lease End Date", asset.LeaseEndDate?.Formatted, "lease_end_date"),
            ("Lease Rent", asset.LeaseRent?.Formatted, "lease_rent"),
            ("Buyout Cost", asset.BuyoutCost?.Formatted, "buyout_cost"),
            ("Invoice Number", asset.InvoiceNumber, "invoice_number"),
            ("PO Number", asset.PoNumber, "po_number"),
            ("Warranty/Soft Cost", asset.WarrantySoftCost?.Formatted, "warranty_soft_cost"),
            ("Decommission Date", asset.DecommissionDate?.Formatted, "decommission_date"),
            ("Book Value", asset.LeaseBookValue?.Formatted ?? asset.BookValue, null),
            ("Purchase Cost", asset.PurchaseCost, "purchase_cost"),
            ("Purchase Date", asset.PurchaseDate?.Formatted, "purchase_date"),
        }.Select(r => new AssetDetailRow(r.Label, r.Value, Copyable: true, EditKey: r.EditKey, AlwaysShow: true)).ToList();
        rows.AddRange(FieldsInGroup(asset, Group("procurement"))
            .Select(f => new AssetDetailRow(f.Name, f.Field.Value, Copyable: true, EditKey: f.Field.Field, AlwaysShow: true)));
        return rows;
    }

    /// <summary>One taxonomy group's rows; empty when the asset has no fields in it.</summary>
    public static List<AssetDetailRow> GroupRows(SnipeAsset asset, string slug)
    {
        var group = Group(slug);
        return FieldsInGroup(asset, group)
            .Select(f => new AssetDetailRow(f.Name, f.Field.Value, Copyable: true, Mono: group.Mono, EditKey: f.Field.Field, AlwaysShow: true))
            .ToList();
    }

    /// <summary>Custom fields no group claims and that hold a value, by name.</summary>
    public static List<AssetDetailRow> OtherRows(SnipeAsset asset) => (asset.CustomFields ?? new())
        .Where(kvp => GroupSlug(kvp.Key, kvp.Value) == "other" && !string.IsNullOrEmpty(kvp.Value.Value) && !HiddenFields.Contains(kvp.Key))
        .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
        .Select(kvp => new AssetDetailRow(kvp.Key, kvp.Value.Value, Copyable: true, EditKey: kvp.Value.Field))
        .ToList();

    public static List<AssetDetailRow> MetadataRows(SnipeAsset asset, DateTime today) => new()
    {
        new("Last Checkout", asset.LastCheckout?.Formatted, AlwaysShow: true),
        new("Expected Checkin", asset.ExpectedCheckin?.Formatted, AlwaysShow: true),
        new("Last Audit", asset.LastAuditDate?.Formatted, AlwaysShow: true),
        new("Next Audit", asset.NextAuditDate?.Formatted, AlwaysShow: true),
        new("Supplier", asset.Supplier?.Name, AlwaysShow: true),
        new("Device EOL", DeviceEolText(asset, today), AlwaysShow: true),
        new("BYOD", asset.Byod == true ? "Yes" : "No"),
        new("Requestable", asset.Requestable == true ? "Yes" : "No"),
        new("Created", asset.CreatedAt?.Formatted),
        new("Updated", asset.UpdatedAt?.Formatted),
    };

    /// <summary>"2029-07-22 — 2 years 10 months from now", like the web's EOL row.</summary>
    public static string? DeviceEolText(SnipeAsset asset, DateTime today)
    {
        var formatted = asset.AssetEolDate?.Formatted;
        if (string.IsNullOrEmpty(formatted)) return null;
        if (ParseDate(asset.AssetEolDate?.Date) is not { } eol) return formatted;

        var (from, to) = eol > today ? (today, eol) : (eol, today);
        var months = ((to.Year - from.Year) * 12) + to.Month - from.Month;
        if (to.Day < from.Day) months--;
        var years = months / 12;
        months %= 12;

        var span = new List<string>();
        if (years > 0) span.Add($"{years} year{(years == 1 ? "" : "s")}");
        if (months > 0) span.Add($"{months} month{(months == 1 ? "" : "s")}");
        if (span.Count == 0) return formatted;
        return $"{formatted} — {string.Join(" ", span)} {(eol > today ? "from now" : "ago")}";
    }

    /// <summary>
    /// The group a custom field belongs to: the server's word when the fork's
    /// API sends field_group, else the mirrored seed taxonomy, else "other".
    /// </summary>
    public static string GroupSlug(string name, SnipeCustomField field) =>
        !string.IsNullOrEmpty(field.FieldGroup) ? field.FieldGroup!
        : FieldGroups.FirstOrDefault(g => g.Fields.Contains(name))?.Slug ?? "other";

    private static List<(string Name, SnipeCustomField Field)> FieldsInGroup(SnipeAsset asset, AssetFieldGroup group)
    {
        if (asset.CustomFields == null) return new();
        return asset.CustomFields
            .Where(kvp => GroupSlug(kvp.Key, kvp.Value) == group.Slug && !HiddenFields.Contains(kvp.Key))
            .OrderBy(kvp =>
            {
                var index = group.Fields.ToList().IndexOf(kvp.Key);
                return index < 0 ? int.MaxValue : index;
            })
            .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => (kvp.Key, kvp.Value))
            .ToList();
    }

    /// <summary>The choices a listbox custom field offers, by db column, from Snipe-IT's field definitions.</summary>
    public static Dictionary<string, List<string>> ListboxOptions(IEnumerable<SnipeFieldDef> definitions)
    {
        var options = new Dictionary<string, List<string>>();
        foreach (var def in definitions)
        {
            if (!string.IsNullOrEmpty(def.DbColumnName)
                && string.Equals(def.Type, "listbox", StringComparison.OrdinalIgnoreCase)
                && def.FieldValuesArray is { Count: > 0 } values)
                options[def.DbColumnName!] = values;
        }
        return options;
    }

    /// <summary>
    /// A listbox field's dropdown items: a blank, the current value when it is
    /// not one of the options (so it is not lost), then the options.
    /// </summary>
    public static List<string> EditChoices(string current, IReadOnlyList<string> options)
    {
        var items = new List<string> { "" };
        if (current.Length > 0 && !options.Contains(current)) items.Add(current);
        items.AddRange(options);
        return items;
    }

    // MARK: - History

    /// <summary>Events whose text contains the filter, ignoring case; all of them when it is blank.</summary>
    public static List<SnipeHistoryEntry> FilterHistory(IReadOnlyList<SnipeHistoryEntry> history, string? filter)
    {
        var text = filter?.Trim().ToLowerInvariant() ?? "";
        return text.Length == 0 ? history.ToList() : history.Where(h => h.SearchText.Contains(text)).ToList();
    }

    /// <summary>"12 events", or "3 of 12 events" while a filter hides some.</summary>
    public static string HistoryCount(int shown, int total) =>
        shown == total ? $"{total} event{(total == 1 ? "" : "s")}" : $"{shown} of {total} events";

    /// <summary>What the History list says when it has nothing to show.</summary>
    public static string HistoryEmpty(int total) =>
        total == 0 ? "No history recorded for this asset." : "No events match the filter.";

    /// <summary>A history event's action, capitalised: "Checkout", "Update".</summary>
    public static string ActionTitle(string action) =>
        action.Length == 0 ? action : char.ToUpperInvariant(action[0]) + action[1..];

    /// <summary>"Jan 5, 2026 3:04 PM" for a history timestamp, or the raw text when it does not parse.</summary>
    public static string FormatHistoryDate(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt.ToString("MMM d, yyyy h:mm tt", CultureInfo.CurrentCulture)
            : raw;
    }

    /// <summary>
    /// An attached file's address: an absolute http(s) URL as given, a path
    /// resolved against the Snipe-IT address, or null for anything else.
    /// </summary>
    public static Uri? ResolveFileUrl(string? url, string? baseUrl)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (!url.StartsWith('/') && Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            return absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp ? absolute : null;
        if (!string.IsNullOrEmpty(baseUrl) && Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var root)
            && Uri.TryCreate(root, url.TrimStart('/'), out var relative))
            return relative;
        return null;
    }

    /// <summary>The asset's page on the Snipe-IT site.</summary>
    public static string WebUrl(string baseUrl, int assetId) => $"{baseUrl.TrimEnd('/')}/hardware/{assetId}";
}
