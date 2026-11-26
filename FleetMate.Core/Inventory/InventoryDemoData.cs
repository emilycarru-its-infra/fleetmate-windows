using System.Globalization;
using FleetMate.Core.Knowledge;
using FleetMate.Core.Models.Inventory;

namespace FleetMate.Core.Inventory;

/// <summary>
/// Fictional Inventory data for demo mode and screenshots: made-up assets,
/// models, people, rooms, serials and DEMO- asset tags, with addresses on
/// example.com. Nothing here names a real organization, person or device, so
/// captures made from it can be published. Dates are relative to
/// <paramref name="now"/> so ages and lifecycle bars read the same whenever it runs.
/// </summary>
public static class InventoryDemoData
{
    private sealed record Seed(string Tag, string Name, string Serial, string Model, string Manufacturer, string Category,
        string Status, string Meta, string? Person, string? Location, string Platform, int AgeMonths, int LifeMonths);

    private static readonly Seed[] Seeds =
    {
        new("DEMO-0001", "Studio Laptop 01", "DMX10001A", "Contoso Book Pro 14 (2025)", "Contoso", "Laptops", "Deployed", "deployed", "Avery Quinn", "Studio North", "Macintosh", 14, 48),
        new("DEMO-0002", "Studio Laptop 02", "DMX10002B", "Contoso Book Pro 14 (2025)", "Contoso", "Laptops", "Deployed", "deployed", "Jordan Ellis", "Studio North", "Macintosh", 13, 48),
        new("DEMO-0003", "Lab Desktop 11", "DMX20011C", "Fabrikam Tower 7", "Fabrikam", "Desktops", "Deployed", "deployed", "Riley Chen", "Media Lab", "Windows", 30, 60),
        new("DEMO-0004", "Lab Desktop 12", "DMX20012D", "Fabrikam Tower 7", "Fabrikam", "Desktops", "Ready to Deploy", "deployable", null, "Media Lab", "Windows", 30, 60),
        new("DEMO-0005", "Loaner Laptop 05", "DMX10005E", "Fabrikam Slim 13", "Fabrikam", "Laptops", "Ready to Deploy", "deployable", null, "Service Desk", "Windows", 22, 48),
        new("DEMO-0006", "Front Desk Display", "DMX30006F", "Northwind View 27", "Northwind", "Displays", "Deployed", "deployed", "Morgan Lee", "Front Office", "", 40, 72),
        new("DEMO-0007", "Edit Suite Display", "DMX30007G", "Northwind View 32", "Northwind", "Displays", "In Repair", "pending", null, "Service Desk", "", 50, 72),
        new("DEMO-0008", "Field Tablet 08", "DMX40008H", "Contoso Slate 11", "Contoso", "Tablets", "Deployed", "deployed", "Casey Patel", "Field Kit Room", "iOS/iPadOS", 8, 36),
        new("DEMO-0009", "Field Tablet 09", "DMX40009J", "Contoso Slate 11", "Contoso", "Tablets", "Ordered", "pending", null, null, "iOS/iPadOS", 0, 36),
        new("DEMO-0010", "Copy Room Printer", "DMX50010K", "Litware Print 400", "Litware", "Printers", "Deployed", "deployed", null, "Copy Room", "", 60, 84),
        new("DEMO-0011", "Gallery Camera", "DMX60011L", "Adatum Lens X", "Adatum", "Cameras", "Deployed", "deployed", "Drew Kim", "Gallery", "", 26, 60),
        new("DEMO-0012", "Old Studio Laptop", "DMX10012M", "Contoso Book 13 (2019)", "Contoso", "Laptops", "Retired", "archived", null, "Storage", "Macintosh", 74, 48),
        new("DEMO-0013", "Studio Speaker Pair", "DMX70013N", "Adatum Sound S2", "Adatum", "Audio", "Deployed", "deployed", null, "Studio South", "", 18, 60),
        new("DEMO-0014", "Lab Switch 2", "DMX80014P", "Litware Switch 24", "Litware", "Network", "Deployed", "deployed", null, "Server Room", "", 36, 84),
        new("DEMO-0015", "Studio Laptop 15", "DMX10015Q", "Contoso Book Pro 16 (2025)", "Contoso", "Laptops", "Deployed", "deployed", "Sam Rivera", "Studio South", "Macintosh", 5, 48),
        new("DEMO-0016", "Lab Desktop 16", "DMX20016R", "Fabrikam Tower 7", "Fabrikam", "Desktops", "Deployed", "deployed", null, "Media Lab", "Windows", 30, 60),
    };

    private static readonly string[] Statuses = { "Deployed", "Ready to Deploy", "In Repair", "Ordered", "Retired" };

    public static List<SnipeAsset> Assets(DateTime now) =>
        Seeds.Select((s, i) => Asset(s, i + 1, now)).ToList();

    /// <summary>The status labels the Status picker offers.</summary>
    public static List<SnipeStatusLabelFull> StatusLabels() => Statuses.Select((name, i) => new SnipeStatusLabelFull
    {
        Id = i + 1,
        Name = name,
        Type = Seeds.First(s => s.Status == name).Meta is "deployed" or "deployable" ? "deployable" : "pending",
    }).ToList();

    private static SnipeAsset Asset(Seed s, int id, DateTime now)
    {
        var purchase = now.Date.AddMonths(-s.AgeMonths);
        var eol = purchase.AddMonths(s.LifeMonths);
        var warranty = purchase.AddMonths(36);
        var updated = now.AddDays(-(id * 3 % 40)).AddHours(-id);
        var person = s.Person;
        var fields = new Dictionary<string, SnipeCustomField>
        {
            ["Device Management Service"] = Field("_snipeit_device_management_service_1", s.Platform switch
            {
                "Macintosh" or "iOS/iPadOS" => "Demo MDM", "Windows" => "Demo Endpoint Manager", _ => null,
            }, "inventory"),
            ["Fleet"] = Field("_snipeit_fleet_2", s.Category is "Laptops" or "Tablets" ? "Studio Fleet" : "Shared Fleet", "inventory"),
            ["Catalog"] = Field("_snipeit_catalog_3", s.Category is "Laptops" or "Desktops" ? "Standard" : null, "inventory"),
        };
        if (s.Platform.Length > 0) fields["Platform"] = Field("_snipeit_platform_4", s.Platform, "specs");
        if (s.Category is "Laptops" or "Desktops" or "Tablets")
        {
            fields["Memory"] = Field("_snipeit_memory_5", s.Category == "Tablets" ? "8 GB" : "16 GB", "specs");
            fields["Storage"] = Field("_snipeit_storage_6", "512 GB", "specs");
            fields["Hostname"] = Field("_snipeit_hostname_7", $"{s.Tag.ToLowerInvariant()}.example.com", "networking");
            fields["Entra ID"] = Field("_snipeit_entra_id_8", DemoGuid(id, 1), "identity");
            fields["Intune ID"] = Field("_snipeit_intune_id_9", DemoGuid(id, 2), "identity");
        }
        if (s.Category == "Printers")
        {
            fields["Hostname"] = Field("_snipeit_hostname_7", "printer-copy.example.com", "networking");
            fields["Toner Contract"] = Field("_snipeit_toner_contract_10", "DEMO-TC-12", "networking");
        }
        fields["Asset Colour"] = Field("_snipeit_asset_colour_11", id % 2 == 0 ? "Silver" : "Graphite", null);

        return new SnipeAsset
        {
            Id = id,
            Name = s.Name,
            AssetTag = s.Tag,
            Serial = s.Serial,
            Model = new SnipeRef { Id = 100 + id, Name = s.Model },
            Manufacturer = new SnipeRef { Id = s.Manufacturer.Length, Name = s.Manufacturer },
            Category = new SnipeRef { Id = s.Category.Length, Name = s.Category },
            StatusLabel = new SnipeStatusLabel
            {
                Id = Array.IndexOf(Statuses, s.Status) + 1, Name = s.Status, StatusMeta = s.Meta,
                StatusType = s.Meta is "deployed" or "deployable" ? "deployable" : s.Meta,
            },
            AssignedTo = person == null ? null : new SnipeAssignee
            {
                Id = 500 + id, Name = person, Type = "user",
                Email = person.ToLowerInvariant().Replace(' ', '.') + "@example.com",
                EmployeeNumber = $"E{1000 + id}",
            },
            Location = s.Location == null ? null : new SnipeRef { Id = s.Location.Length, Name = s.Location },
            RtdLocation = s.Location == null ? null : new SnipeRef { Id = s.Location.Length, Name = s.Location },
            Supplier = new SnipeRef { Id = 7, Name = "Demo Supplies Ltd." },
            PurchaseDate = Date(purchase),
            AssetEolDate = Date(eol),
            WarrantyExpires = Date(warranty),
            PurchaseCost = s.Category switch { "Laptops" => "1,899.00", "Desktops" => "1,450.00", "Displays" => "629.00", _ => "349.00" },
            OwnershipType = id % 3 == 0 ? "Lease" : "Purchase",
            LeaseContractId = id % 3 == 0 ? $"DEMO-L-{id:000}" : null,
            InvoiceNumber = $"INV-{2000 + id}",
            PoNumber = $"PO-{7000 + id}",
            LeaseUsage = s.Category is "Laptops" or "Tablets" ? "Staff" : "Lab",
            LeaseArea = s.Location?.Split(' ')[0],
            Notes = id == 7 ? "Backlight flickers after an hour; sent for repair." : null,
            Byod = false,
            Requestable = s.Meta == "deployable",
            CreatedAt = DateTimeOf(purchase.AddDays(2)),
            UpdatedAt = DateTimeOf(updated),
            LastCheckout = person == null ? null : DateTimeOf(updated.AddDays(-12)),
            LastAuditDate = DateTimeOf(now.AddMonths(-3).AddDays(-id)),
            NextAuditDate = DateTimeOf(now.AddMonths(9).AddDays(-id)),
            CustomFields = fields,
        };
    }

    /// <summary>A few events for any asset: created, checked out, updated, audited.</summary>
    public static List<SnipeHistoryEntry> History(SnipeAsset asset, DateTime now)
    {
        var who = "Demo Admin";
        var entries = new List<SnipeHistoryEntry>
        {
            new()
            {
                Id = asset.Id * 10 + 1, Action = "update", By = who, Date = Stamp(now.AddDays(-2)),
                Changes = new[]
                {
                    new SnipeFieldChange("Location", "Service Desk", asset.LocationName ?? "Storage"),
                    new SnipeFieldChange("Memory", null, asset.CustomFieldByName("Memory") ?? "16 GB"),
                },
            },
            new()
            {
                Id = asset.Id * 10 + 2, Action = "audit", By = who, Date = Stamp(now.AddMonths(-3)),
                Note = "Seen in place; label legible.",
            },
            new()
            {
                Id = asset.Id * 10 + 3, Action = "uploaded", By = who, Date = Stamp(now.AddMonths(-5)),
                File = new SnipeHistoryFile("purchase-receipt.pdf", "https://inventory.example.com/uploads/demo/purchase-receipt.pdf"),
            },
        };
        if (asset.AssignedTo is { } person)
            entries.Insert(1, new SnipeHistoryEntry
            {
                Id = asset.Id * 10 + 4, Action = "checkout", By = who, Target = person.Name, Date = Stamp(now.AddDays(-14)),
                Note = "Issued for the term.",
            });
        entries.Add(new SnipeHistoryEntry
        {
            Id = asset.Id * 10 + 5, Action = "create new", By = who, Date = Stamp(now.AddMonths(-14)),
        });
        return entries;
    }

    /// <summary>A handful of Handbook pages the demo assets' Handbook card finds.</summary>
    public static HandbookIndex Handbook() => new(new[]
    {
        Page("devices/laptops/contoso-book-pro.md", "Contoso Book Pro: setup and care", new[] { "devices", "laptops" },
            "# Contoso Book Pro\n\nHow a **Contoso Book Pro** is set up for a studio, and how to look after it.\n\n- Charge overnight before first use.\n- Keep the lid closed when moving it.\n"),
        Page("devices/laptops/loaners.md", "Lending laptops", new[] { "devices", "laptops" },
            "# Lending laptops\n\nLaptops are lent from the service desk for up to two weeks.\n"),
        Page("devices/displays/display-calibration.md", "Calibrating displays", new[] { "devices", "displays" },
            "# Calibrating displays\n\nCalibrate each display every term with the shared colorimeter.\n"),
        Page("devices/fleets/studio-fleet.md", "The Studio Fleet", new[] { "devices", "fleets" },
            "# Studio Fleet\n\nThe laptops and tablets lent to studios. Each is enrolled in **Demo MDM**.\n"),
        Page("devices/management/demo-mdm.md", "Enrolling in Demo MDM", new[] { "devices", "management" },
            "# Demo MDM\n\nEnroll a device from its setup assistant; it joins the right fleet by serial.\n"),
    });

    private static HandbookPage Page(string path, string title, string[] sections, string body) =>
        new(path, title, sections, path.Replace(".md", "/"), Array.Empty<string>(), body, null, null);

    private static SnipeCustomField Field(string column, string? value, string? group) =>
        new() { Field = column, Value = value, FieldGroup = group };

    private static string DemoGuid(int id, int kind) => $"00000000-0000-4000-8000-{kind:00}{id:0000000000}";

    private static SnipeDate Date(DateTime date) => new()
    {
        Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Formatted = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    private static SnipeDateTime DateTimeOf(DateTime when) => new()
    {
        DateTime = Stamp(when),
        Formatted = when.ToString("yyyy-MM-dd h:mm tt", CultureInfo.InvariantCulture),
    };

    private static string Stamp(DateTime when) => when.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
