using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Services.Inventory;
using ModernWpf.Controls;
using Serilog;

namespace FleetMate.GUI.Views.Inventory;

/// <summary>
/// The asset detail sidebar — the macOS AssetDetailSidebar, ported. A hero
/// header (category icon, name, copyable tag/serial chips, status, lifecycle
/// bars, photo) over a grid of taxonomy cards: Inventory, Status, Assignment,
/// Hardware, Procurement, Networking, Identity, Notes, Other, Metadata.
/// Rows expose copy on hover, and fields with an API key edit inline and
/// PATCH straight to Snipe-IT.
/// </summary>
public partial class AssetDetailPanel : UserControl
{
    private SnipeAsset? _asset;
    private SnipeService? _service;
    private bool _statusInitializing;

    private static List<SnipeStatusLabelFull> _statusLabels = new();
    /// <summary>Listbox custom fields' options, keyed by db column — so editing
    /// a dropdown field offers its real choices instead of a text field.</summary>
    private static Dictionary<string, List<string>> _listboxOptions = new();
    private static bool _optionsLoaded;

    public event EventHandler? CloseRequested;
    public event EventHandler? ReAllocateRequested;
    /// <summary>Raised after any successful save so the page can refresh its list.</summary>
    public event EventHandler? Changed;

    // ── Field group taxonomy ─────────────────────────────────────────────
    // The six groups the ECU Snipe-IT fork seeds, in its render order. The
    // server's field_group slug on each custom field wins when present; this
    // mirror only decides where a field lands when the API doesn't say.

    private sealed record FieldGroupDef(string Slug, string Title, string Glyph, bool Mono, string[] Fields);

    private static readonly FieldGroupDef[] FieldGroups =
    {
        new("inventory", "Inventory", "", false,
            new[] { "Device Management Service", "Fleet", "Catalog" }),
        new("specs", "Specs", "", false,
            new[] { "Platform", "Colour", "Display", "Display Resolution", "Memory",
                    "Storage", "Chip", "CPU", "GPU", "NPU", "Architecture", "Cellular", "Version" }),
        new("management", "Management", "", false, Array.Empty<string>()),
        new("networking", "Networking", "", false,
            new[] { "Hostname", "Address", "Driver", "Queue(s)", "Virtual Queue",
                    "Toner Contract", "Options" }),
        new("procurement", "Procurement", "", false, new[] { "License Type" }),
        new("identity", "Identity", "", true,
            new[] { "Entra ID", "Intune ID", "Defender ID", "Object ID", "Micro ID",
                    "Identifier", "IMEI", "Enrollment Status" }),
    };

    private static readonly HashSet<string> HiddenFields = new(StringComparer.OrdinalIgnoreCase) { "Username" };

    public AssetDetailPanel()
    {
        InitializeComponent();
    }

    public void Show(SnipeAsset asset)
    {
        _asset = asset;
        _service = (Application.Current as App)?.SnipeService;
        Render();
        _ = EnsureOptionsAsync();
    }

    // ── Rendering ────────────────────────────────────────────────────────

    private void Render()
    {
        if (_asset is not { } asset) return;

        CategoryIcon.Glyph = CategoryGlyph(asset.Category?.Name);
        NameText.Text = asset.DisplayName;
        ModelText.Text = asset.Model?.Name ?? "";
        ModelText.Visibility = string.IsNullOrEmpty(asset.Model?.Name) ? Visibility.Collapsed : Visibility.Visible;

        ReAllocateButton.Visibility =
            asset.AssignedTo != null || asset.StatusLabel?.StatusMeta == "deployable"
                ? Visibility.Visible : Visibility.Collapsed;

        RenderChips(asset);
        RenderWhoWhere(asset);
        RenderLifecycle(asset);
        RenderPhoto(asset);
        RenderCards(asset);
    }

    private void RenderChips(SnipeAsset asset)
    {
        ChipsRow.Children.Clear();
        if (!string.IsNullOrEmpty(asset.AssetTag))
            ChipsRow.Children.Add(IdentityChip(asset.AssetTag, "Copy asset tag"));
        if (!string.IsNullOrEmpty(asset.Serial))
            ChipsRow.Children.Add(IdentityChip(asset.Serial!, "Copy serial number"));
        ChipsRow.Children.Add(StatusBadge(asset.StatusLabel));
    }

    private void RenderWhoWhere(SnipeAsset asset)
    {
        WhoWhereRow.Children.Clear();

        // An asset checked out to a room is not "assigned to a person named
        // D4315" — when the assignee IS a location, the person line would
        // duplicate the pin line verbatim.
        var assignedToLocation = asset.AssignedTo?.Type == "location";
        if (asset.AssignedTo?.Name is { Length: > 0 } who && !assignedToLocation)
            WhoWhereRow.Children.Add(IconLabel("", who));

        var place = asset.LocationName ?? (assignedToLocation ? asset.AssignedTo?.Name : null);
        if (!string.IsNullOrEmpty(place))
            WhoWhereRow.Children.Add(IconLabel("", place!));

        WhoWhereRow.Visibility = WhoWhereRow.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderLifecycle(SnipeAsset asset)
    {
        LifecycleHost.Children.Clear();

        var purchase = ParseDate(asset.PurchaseDate?.Date);
        var eol = ParseDate(asset.AssetEolDate?.Date);
        var warranty = ParseDate(asset.WarrantyExpires?.Date);

        if (LifecycleFraction(purchase, eol) is { } eolFraction && eol is { } eolDate)
        {
            var monthsLeft = Math.Abs(((eolDate.Year - DateTime.Today.Year) * 12) + eolDate.Month - DateTime.Today.Month);
            LifecycleHost.Children.Add(LifecycleBar(
                "Device EOL", $"{monthsLeft} months left · {(int)(eolFraction * 100)}%", eolFraction));
        }
        if (LifecycleFraction(purchase, warranty) is { } wFraction)
        {
            LifecycleHost.Children.Add(LifecycleBar(
                "Warranty", $"{asset.WarrantyExpires?.Formatted ?? ""} · {(int)(wFraction * 100)}%", wFraction));
        }

        LifecycleHost.Visibility = LifecycleHost.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderPhoto(SnipeAsset asset)
    {
        AssetPhoto.Source = null;
        if (string.IsNullOrEmpty(asset.Image)) return;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(asset.Image);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            AssetPhoto.Source = bitmap;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Asset photo failed to load");
        }
    }

    // ── Cards ────────────────────────────────────────────────────────────

    private void RenderCards(SnipeAsset asset)
    {
        CardsHost.Children.Clear();

        // Inventory spans the left; Status and Assignment stack beside it.
        var rightStack = new StackPanel();
        rightStack.Children.Add(StatusCard(asset));
        if (asset.AssignedTo != null)
        {
            var assignment = AssignmentCard(asset);
            assignment.Margin = new Thickness(0, 12, 0, 0);
            rightStack.Children.Add(assignment);
        }
        AddCardRow(InventoryCard(asset), rightStack);

        AddCardRow(HardwareCard(asset), ProcurementCard(asset));

        var management = GroupCard(asset, "management");
        if (management != null) AddFullCard(management);

        // Networking is short, Identity is GUID-wide — share a row 1:2.
        var networking = GroupCard(asset, "networking");
        var identity = GroupCard(asset, "identity");
        if (networking != null || identity != null)
            AddCardRow(networking ?? EmptySpacer(), identity ?? EmptySpacer(), 1, 2);

        if (!string.IsNullOrEmpty(asset.Notes))
        {
            var notes = Card("Notes", "");
            ((StackPanel)notes.Child!).Children.Add(new TextBlock
            {
                Text = asset.Notes,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Secondary()
            });
            AddFullCard(notes);
        }

        var other = UnmappedFields(asset);
        if (other.Count > 0)
        {
            var card = Card("Other", "");
            var host = (StackPanel)card.Child!;
            foreach (var (name, field) in other)
                AddRow(host, name, field.Value, copyable: true, editKey: field.Field);
            AddFullCard(card);
        }

        AddFullCard(MetadataCard(asset));
    }

    private Border InventoryCard(SnipeAsset asset)
    {
        var group = FieldGroups.First(g => g.Slug == "inventory");
        var card = Card(group.Title, group.Glyph);
        var host = (StackPanel)card.Child!;
        foreach (var (name, field) in FieldsInGroup(asset, group))
        {
            AddRow(host, name, field.Value, copyable: true, editKey: field.Field, alwaysShow: true);
            // Web order: Location sits between Catalog and Usage.
            if (name == "Catalog")
                AddRow(host, "Location", asset.RtdLocation?.Name, alwaysShow: true);
        }
        // Native since the fork's F2 migration, like the lease cluster.
        AddRow(host, "Usage", asset.LeaseUsage, copyable: true, editKey: "lease_usage", alwaysShow: true);
        AddRow(host, "Area", asset.LeaseArea, copyable: true, editKey: "lease_area", alwaysShow: true);
        return card;
    }

    private Border StatusCard(SnipeAsset asset)
    {
        var card = Card("Status", "");
        var host = (StackPanel)card.Child!;

        if (_statusLabels.Count > 0)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new TextBlock { Text = "Status", FontSize = 12, Foreground = Secondary(), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            _statusInitializing = true;
            var combo = new ComboBox
            {
                ItemsSource = _statusLabels,
                DisplayMemberPath = "Name",
                FontSize = 12,
                MinWidth = 140,
                HorizontalAlignment = HorizontalAlignment.Left,
                SelectedItem = _statusLabels.FirstOrDefault(s => s.Id == asset.StatusLabel?.Id)
            };
            combo.SelectionChanged += OnStatusChanged;
            _statusInitializing = false;
            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);
            host.Children.Add(row);
        }
        else
        {
            AddRow(host, "Status", asset.StatusLabel?.Name, alwaysShow: true);
        }

        AddRow(host, "Status Type", asset.StatusLabel?.StatusMeta, alwaysShow: true);
        return card;
    }

    private Border AssignmentCard(SnipeAsset asset)
    {
        var card = Card("Assignment", "");
        var host = (StackPanel)card.Child!;
        AddRow(host, "Assigned To", asset.AssignedTo?.Name);
        AddRow(host, "Email", asset.AssignedTo?.Email, copyable: true);
        AddRow(host, "Employee #", asset.AssignedTo?.EmployeeNumber);
        return card;
    }

    private Border HardwareCard(SnipeAsset asset)
    {
        var card = Card("Hardware", "");
        var host = (StackPanel)card.Child!;
        AddRow(host, "Serial", asset.Serial, copyable: true, mono: true, editKey: "serial");
        AddRow(host, "Model", asset.Model?.Name);
        AddRow(host, "Category", asset.Category?.Name);
        AddRow(host, "Manufacturer", asset.Manufacturer?.Name);
        var specs = FieldGroups.First(g => g.Slug == "specs");
        foreach (var (name, field) in FieldsInGroup(asset, specs))
            AddRow(host, name, field.Value, copyable: true, editKey: field.Field, alwaysShow: true);
        return card;
    }

    private Border ProcurementCard(SnipeAsset asset)
    {
        var card = Card("Procurement", "");
        var host = (StackPanel)card.Child!;
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
        };
        foreach (var (label, value, editKey) in rows)
            AddRow(host, label, value, copyable: true, editKey: editKey, alwaysShow: true, labelWidth: 150);
        var group = FieldGroups.First(g => g.Slug == "procurement");
        foreach (var (name, field) in FieldsInGroup(asset, group))
            AddRow(host, name, field.Value, copyable: true, editKey: field.Field, alwaysShow: true, labelWidth: 150);
        return card;
    }

    /// <summary>One taxonomy group as a card; null when it has no fields.</summary>
    private Border? GroupCard(SnipeAsset asset, string slug)
    {
        var group = FieldGroups.First(g => g.Slug == slug);
        var fields = FieldsInGroup(asset, group);
        if (fields.Count == 0) return null;

        var card = Card(group.Title, group.Glyph);
        var host = (StackPanel)card.Child!;
        foreach (var (name, field) in fields)
            AddRow(host, name, field.Value, copyable: true, mono: group.Mono,
                editKey: field.Field, alwaysShow: true);
        return card;
    }

    private Border MetadataCard(SnipeAsset asset)
    {
        var card = Card("Metadata", "");
        var host = (StackPanel)card.Child!;
        AddRow(host, "Last Checkout", asset.LastCheckout?.Formatted, alwaysShow: true);
        AddRow(host, "Expected Checkin", asset.ExpectedCheckin?.Formatted, alwaysShow: true);
        AddRow(host, "Last Audit", asset.LastAuditDate?.Formatted, alwaysShow: true);
        AddRow(host, "Next Audit", asset.NextAuditDate?.Formatted, alwaysShow: true);
        AddRow(host, "Supplier", asset.Supplier?.Name, alwaysShow: true);
        AddRow(host, "Device EOL", DeviceEolText(asset), alwaysShow: true);
        AddRow(host, "BYOD", asset.Byod == true ? "Yes" : "No");
        AddRow(host, "Requestable", asset.Requestable == true ? "Yes" : "No");
        AddRow(host, "Created", asset.CreatedAt?.Formatted);
        AddRow(host, "Updated", asset.UpdatedAt?.Formatted);
        return card;
    }

    /// <summary>"2029-07-22 — 2 years 10 months from now", like the web's EOL row.</summary>
    private static string? DeviceEolText(SnipeAsset asset)
    {
        var formatted = asset.AssetEolDate?.Formatted;
        if (string.IsNullOrEmpty(formatted)) return null;
        if (ParseDate(asset.AssetEolDate?.Date) is not { } eol) return formatted;

        var now = DateTime.Today;
        var (from, to) = eol > now ? (now, eol) : (eol, now);
        var months = ((to.Year - from.Year) * 12) + to.Month - from.Month;
        if (to.Day < from.Day) months--;
        var years = months / 12;
        months %= 12;

        var span = new List<string>();
        if (years > 0) span.Add($"{years} year{(years == 1 ? "" : "s")}");
        if (months > 0) span.Add($"{months} month{(months == 1 ? "" : "s")}");
        if (span.Count == 0) return formatted;
        return $"{formatted} — {string.Join(" ", span)} {(eol > now ? "from now" : "ago")}";
    }

    // ── Custom field grouping ────────────────────────────────────────────

    /// <summary>Group slug for a field: the server's word when the fork's API
    /// sends field_group, else the mirrored seed taxonomy, else "other".</summary>
    private static string GroupSlug(string name, SnipeCustomField field)
    {
        if (!string.IsNullOrEmpty(field.FieldGroup)) return field.FieldGroup!;
        return FieldGroups.FirstOrDefault(g => g.Fields.Contains(name))?.Slug ?? "other";
    }

    private static List<(string Name, SnipeCustomField Field)> FieldsInGroup(SnipeAsset asset, FieldGroupDef group)
    {
        if (asset.CustomFields == null) return new();
        return asset.CustomFields
            .Where(kvp => GroupSlug(kvp.Key, kvp.Value) == group.Slug && !HiddenFields.Contains(kvp.Key))
            .OrderBy(kvp =>
            {
                var index = Array.IndexOf(group.Fields, kvp.Key);
                return index < 0 ? int.MaxValue : index;
            })
            .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => (kvp.Key, kvp.Value))
            .ToList();
    }

    private static List<(string Name, SnipeCustomField Field)> UnmappedFields(SnipeAsset asset)
    {
        if (asset.CustomFields == null) return new();
        return asset.CustomFields
            .Where(kvp => GroupSlug(kvp.Key, kvp.Value) == "other"
                && !string.IsNullOrEmpty(kvp.Value.Value)
                && !HiddenFields.Contains(kvp.Key))
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => (kvp.Key, kvp.Value))
            .ToList();
    }

    // ── Building blocks ──────────────────────────────────────────────────

    private void AddCardRow(FrameworkElement left, FrameworkElement right, double leftStar = 1, double rightStar = 1)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(leftStar, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(rightStar, GridUnitType.Star) });
        left.Margin = new Thickness(0, 0, 6, 0);
        right.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        CardsHost.Children.Add(grid);
    }

    private void AddFullCard(FrameworkElement card)
    {
        card.Margin = new Thickness(0, 0, 0, 12);
        CardsHost.Children.Add(card);
    }

    private static FrameworkElement EmptySpacer() => new Border();

    private Border Card(string title, string glyph)
    {
        var host = new StackPanel();
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Glyph = glyph,
            FontSize = 11,
            Foreground = Secondary(),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Secondary(),
            VerticalAlignment = VerticalAlignment.Center
        });
        host.Children.Add(header);

        return new Border
        {
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)FindResource("CardBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = host
        };
    }

    /// <summary>
    /// One field row: label, value ("—" when empty), copy and edit revealed on
    /// hover. Rows with an editKey PATCH that API field inline; listbox fields
    /// edit as the dropdown they are on the web.
    /// </summary>
    private void AddRow(StackPanel host, string label, string? value, bool copyable = false,
        bool mono = false, string? editKey = null, bool alwaysShow = false, double labelWidth = 110)
    {
        if (!alwaysShow && string.IsNullOrEmpty(value)) return;

        var row = new Grid { Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = Secondary(),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(labelBlock, 0);
        row.Children.Add(labelBlock);

        var display = string.IsNullOrEmpty(value) ? "—" : value!;
        var valueBlock = new TextBlock
        {
            Text = display,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = string.IsNullOrEmpty(value)
                ? Secondary()
                : (Brush)FindResource("SystemControlForegroundBaseHighBrush")
        };
        if (mono) valueBlock.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        Grid.SetColumn(valueBlock, 1);
        row.Children.Add(valueBlock);

        var copyButton = HoverIconButton("", "Copy");
        copyButton.Click += (_, _) =>
        {
            var text = valueBlock.Text;
            if (text != "—") try { Clipboard.SetText(text); } catch { }
        };
        Grid.SetColumn(copyButton, 2);
        if (copyable) row.Children.Add(copyButton);

        var editButton = HoverIconButton("", "Edit");
        Grid.SetColumn(editButton, 3);
        if (editKey != null && _service != null)
        {
            editButton.Click += (_, _) => BeginInlineEdit(row, valueBlock, editKey, mono);
            row.Children.Add(editButton);
        }

        row.MouseEnter += (_, _) => { copyButton.Opacity = 1; editButton.Opacity = 1; };
        row.MouseLeave += (_, _) => { copyButton.Opacity = 0; editButton.Opacity = 0; };

        host.Children.Add(row);
    }

    private static Button HoverIconButton(string glyph, string tooltip) => new()
    {
        Content = new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Glyph = glyph,
            FontSize = 11
        },
        Padding = new Thickness(4, 2, 4, 2),
        Margin = new Thickness(4, 0, 0, 0),
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        ToolTip = tooltip,
        Opacity = 0,
        VerticalAlignment = VerticalAlignment.Center
    };

    private void BeginInlineEdit(Grid row, TextBlock valueBlock, string editKey, bool mono)
    {
        if (_asset == null || _service == null) return;

        var current = valueBlock.Text == "—" ? "" : valueBlock.Text;

        var editor = new StackPanel { Orientation = Orientation.Horizontal };
        FrameworkElement input;
        Func<string> readValue;

        // Snipe's field type decides the editor: a listbox edits as the
        // dropdown it is on the web, not as free text.
        if (_listboxOptions.TryGetValue(editKey, out var options) && options.Count > 0)
        {
            var items = new List<string> { "" };
            if (current.Length > 0 && !options.Contains(current)) items.Add(current);
            items.AddRange(options);
            var combo = new ComboBox { ItemsSource = items, SelectedItem = current, FontSize = 12, MinWidth = 140 };
            input = combo;
            readValue = () => combo.SelectedItem as string ?? "";
        }
        else
        {
            var box = new TextBox { Text = current, FontSize = 12, MinWidth = 150 };
            if (mono) box.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            input = box;
            readValue = () => box.Text;
        }

        var save = new Button
        {
            Content = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = "", FontSize = 11 },
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = "Save"
        };
        var cancel = new Button
        {
            Content = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = "", FontSize = 11 },
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = "Cancel"
        };
        editor.Children.Add(input);
        editor.Children.Add(save);
        editor.Children.Add(cancel);
        Grid.SetColumn(editor, 1);

        valueBlock.Visibility = Visibility.Collapsed;
        row.Children.Add(editor);

        void EndEdit()
        {
            row.Children.Remove(editor);
            valueBlock.Visibility = Visibility.Visible;
        }

        cancel.Click += (_, _) => EndEdit();
        if (input is TextBox textInput)
        {
            textInput.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                if (e.Key == Key.Escape) EndEdit();
            };
            textInput.Focus();
            textInput.SelectAll();
        }

        save.Click += async (_, _) =>
        {
            var newValue = readValue();
            save.IsEnabled = false;
            cancel.IsEnabled = false;
            try
            {
                var response = await _service.PatchAssetFieldAsync(_asset.Id, editKey, newValue);
                if (response == null || !response.IsSuccess)
                {
                    MessageBox.Show($"Update failed: {response?.Messages ?? "no response"}",
                        "Snipe-IT", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                valueBlock.Text = newValue.Length == 0 ? "—" : newValue;
                valueBlock.Foreground = newValue.Length == 0
                    ? Secondary()
                    : (Brush)FindResource("SystemControlForegroundBaseHighBrush");
                EndEdit();
                Changed?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                save.IsEnabled = true;
                cancel.IsEnabled = true;
            }
        };
    }

    private FrameworkElement IdentityChip(string value, string tooltip)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 12,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Glyph = "",
            FontSize = 10,
            Foreground = Secondary(),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

        var chip = new Button
        {
            Content = content,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 8, 0),
            Background = (Brush)FindResource("SubtleFillBrush"),
            BorderThickness = new Thickness(0),
            ToolTip = tooltip
        };
        chip.Click += (_, _) => { try { Clipboard.SetText(value); } catch { } };
        return chip;
    }

    private FrameworkElement StatusBadge(SnipeStatusLabel? status)
    {
        var color = status?.StatusMeta switch
        {
            "deployed" => "#27ae60",
            "deployable" => "#3182CE",
            "pending" => "#d69e2e",
            "archived" => "#718096",
            _ => "#718096"
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = status?.Name ?? "Unknown",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }

    private FrameworkElement IconLabel(string glyph, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
        panel.Children.Add(new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Glyph = glyph,
            FontSize = 11,
            Foreground = Secondary(),
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = Secondary(),
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }

    /// <summary>Title left, milestone right, and a bar showing how much of the
    /// span has elapsed — amber while running, red once overdue.</summary>
    private FrameworkElement LifecycleBar(string title, string trailing, double fraction)
    {
        var host = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };

        var header = new DockPanel();
        var trail = new TextBlock { Text = trailing, FontSize = 10, Foreground = Secondary() };
        DockPanel.SetDock(trail, Dock.Right);
        header.Children.Add(trail);
        header.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeights.Medium });
        host.Children.Add(header);

        var track = new Grid { Height = 6, Margin = new Thickness(0, 3, 0, 0) };
        var filled = Math.Max(fraction, 0.02);
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(filled, GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - filled, 0.0001), GridUnitType.Star) });
        var back = new Border
        {
            Background = (Brush)FindResource("SubtleFillBrush"),
            CornerRadius = new CornerRadius(3)
        };
        Grid.SetColumnSpan(back, 2);
        track.Children.Add(back);
        var fill = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fraction >= 1 ? "#e53e3e" : "#dd6b20")),
            CornerRadius = new CornerRadius(3)
        };
        Grid.SetColumn(fill, 0);
        track.Children.Add(fill);
        host.Children.Add(track);

        return host;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private Brush Secondary() => (Brush)FindResource("SystemControlForegroundBaseMediumBrush");

    private static DateTime? ParseDate(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw!.Length < 10) return null;
        return DateTime.TryParseExact(raw[..10], "yyyy-MM-dd", null,
            System.Globalization.DateTimeStyles.None, out var date) ? date : null;
    }

    /// <summary>Elapsed share of the purchase→milestone span, clamped 0…1.</summary>
    private static double? LifecycleFraction(DateTime? start, DateTime? end)
    {
        if (start is not { } from || end is not { } to || to <= from) return null;
        var elapsed = (DateTime.Today - from).TotalDays / (to - from).TotalDays;
        return Math.Min(Math.Max(elapsed, 0), 1);
    }

    private static string CategoryGlyph(string? category)
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

    private async Task EnsureOptionsAsync()
    {
        if (_service == null) return;
        if (_statusLabels.Count == 0)
        {
            _statusLabels = await _service.GetStatusLabelsAsync();
            if (_asset != null) Render();
        }
        if (!_optionsLoaded)
        {
            _optionsLoaded = true;
            var defs = await _service.GetFieldDefinitionsAsync();
            var options = new Dictionary<string, List<string>>();
            foreach (var def in defs)
            {
                if (!string.IsNullOrEmpty(def.DbColumnName)
                    && string.Equals(def.Type, "listbox", StringComparison.OrdinalIgnoreCase)
                    && def.FieldValuesArray is { Count: > 0 } values)
                {
                    options[def.DbColumnName!] = values;
                }
            }
            _listboxOptions = options;
        }
    }

    // ── Events ───────────────────────────────────────────────────────────

    private async void OnStatusChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_statusInitializing || _asset == null || _service == null) return;
        if (sender is not ComboBox combo || combo.SelectedItem is not SnipeStatusLabelFull selected) return;
        if (selected.Id == _asset.StatusLabel?.Id) return;

        var response = await _service.PatchAssetFieldAsync(_asset.Id, "status_id", selected.Id.ToString());
        if (response == null || !response.IsSuccess)
        {
            MessageBox.Show($"Status update failed: {response?.Messages ?? "no response"}",
                "Snipe-IT", MessageBoxButton.OK, MessageBoxImage.Warning);
            _statusInitializing = true;
            combo.SelectedItem = _statusLabels.FirstOrDefault(s => s.Id == _asset.StatusLabel?.Id);
            _statusInitializing = false;
            return;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnReAllocateClicked(object sender, RoutedEventArgs e) =>
        ReAllocateRequested?.Invoke(this, EventArgs.Empty);

    private void OnCloseClicked(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenInSnipeClicked(object sender, RoutedEventArgs e)
    {
        if (_asset == null || _service == null) return;
        try
        {
            Process.Start(new ProcessStartInfo($"{_service.BaseUrl}/hardware/{_asset.Id}") { UseShellExecute = true });
        }
        catch { }
    }
}
