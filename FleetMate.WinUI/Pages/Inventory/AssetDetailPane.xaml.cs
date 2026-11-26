using FleetMate.Core.Inventory;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Shared;
using FleetMate.WinUI.Pages.Development;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Serilog;
using Windows.System;

namespace FleetMate.WinUI.Pages.Inventory;

/// <summary>
/// The selected asset: the macOS AssetDetailSidebar, as FleetMate.GUI's
/// AssetDetailPanel draws it. A hero header (category icon, name, copyable
/// tag and serial chips, status, who and where, lifecycle bars, photo) over
/// the taxonomy cards, Details | History, and Re-Allocate. Rows copy their
/// value, and rows with a Snipe-IT field edit inline and PATCH it. What each
/// card holds is FleetMate.Core's <see cref="AssetDetailLayout"/>.
/// </summary>
public sealed partial class AssetDetailPane : UserControl
{
    /// <summary>Card pairs stack one above the other below this pane width.</summary>
    private const double TwoColumnWidth = 560;

    private SnipeAsset? _asset;
    private bool _twoColumns = true;
    private bool _statusInitializing;
    private IReadOnlyList<SnipeStatusLabelFull> _statusLabels = Array.Empty<SnipeStatusLabelFull>();
    private IReadOnlyDictionary<string, List<string>> _listboxOptions = new Dictionary<string, List<string>>();
    private bool _optionsLoaded;

    public event EventHandler? CloseRequested;
    public event EventHandler? ReAllocateRequested;

    /// <summary>Raised after a successful save, so the page reloads the list.</summary>
    public event EventHandler? Changed;

    public AssetDetailPane()
    {
        InitializeComponent();
    }

    private static Shell.ShellServices Services => App.Services;

    /// <summary>Edits reach Snipe-IT; demo mode has none, so its rows only copy.</summary>
    private static bool CanEdit => Services.Snipe != null;

    public void Show(SnipeAsset asset)
    {
        var other = _asset?.Id != asset.Id;
        _asset = asset;
        ErrorBar.IsOpen = false;
        Render();
        if (other) _historyAssetId = null;
        ShowSegment();
        _ = EnsureOptionsAsync();
    }

    // MARK: - Header

    private void Render()
    {
        if (_asset is not { } asset) return;
        CategoryIcon.Glyph = AssetDetailLayout.CategoryGlyph(asset.Category?.Name);
        NameText.Text = asset.DisplayName;
        ModelText.Text = asset.Model?.Name ?? "";
        ModelText.Visibility = DevelopmentUi.Show(!string.IsNullOrEmpty(asset.Model?.Name));
        ReAllocateButton.Visibility = DevelopmentUi.Show(AssetDetailLayout.CanReAllocate(asset) && CanEdit);
        OpenButton.IsEnabled = Services.SnipeWebUrl != null;

        RenderChips(asset);
        RenderWhoWhere(asset);
        RenderLifecycle(asset);
        RenderPhoto(asset);
        RenderCards(asset);
    }

    private void RenderChips(SnipeAsset asset)
    {
        ChipsRow.Children.Clear();
        if (!string.IsNullOrEmpty(asset.AssetTag)) ChipsRow.Children.Add(IdentityChip(asset.AssetTag, "asset tag"));
        if (!string.IsNullOrEmpty(asset.Serial)) ChipsRow.Children.Add(IdentityChip(asset.Serial!, "serial number"));
        ChipsRow.Children.Add(StatusBadge(asset.StatusLabel));
    }

    private void RenderWhoWhere(SnipeAsset asset)
    {
        WhoWhereRow.Children.Clear();
        var (person, place) = AssetDetailLayout.WhoWhere(asset);
        if (person != null) WhoWhereRow.Children.Add(IconLabel(AssetDetailLayout.PersonGlyph, person, "Assigned to"));
        if (place != null) WhoWhereRow.Children.Add(IconLabel(AssetDetailLayout.PlaceGlyph, place, "Location"));
        WhoWhereRow.Visibility = DevelopmentUi.Show(WhoWhereRow.Children.Count > 0);
    }

    private void RenderLifecycle(SnipeAsset asset)
    {
        LifecycleHost.Children.Clear();
        foreach (var bar in AssetDetailLayout.Lifecycle(asset, DateTime.Today)) LifecycleHost.Children.Add(LifecycleBar(bar));
        LifecycleHost.Visibility = DevelopmentUi.Show(LifecycleHost.Children.Count > 0);
    }

    private void RenderPhoto(SnipeAsset asset)
    {
        AssetPhoto.Source = null;
        AssetPhoto.Visibility = Visibility.Collapsed;
        if (!Uri.TryCreate(asset.Image, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return;
        try
        {
            AssetPhoto.Source = new BitmapImage(uri);
            AssetPhoto.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Asset photo failed to load");
        }
    }

    // MARK: - Cards

    private void RenderCards(SnipeAsset asset)
    {
        CardsHost.Children.Clear();

        // Inventory beside Status and Assignment stacked.
        var right = new StackPanel { Spacing = 12, Children = { StatusCard(asset) } };
        if (asset.AssignedTo != null)
            right.Children.Add(RowsCard("Assignment", AssetDetailLayout.AssignmentGlyph, AssetDetailLayout.AssignmentRows(asset)));
        var inventory = AssetDetailLayout.Group("inventory");
        AddPair(RowsCard(inventory.Title, inventory.Glyph, AssetDetailLayout.InventoryRows(asset)), right);

        AddPair(RowsCard("Hardware", AssetDetailLayout.HardwareGlyph, AssetDetailLayout.HardwareRows(asset)),
            RowsCard("Procurement", AssetDetailLayout.Group("procurement").Glyph, AssetDetailLayout.ProcurementRows(asset), 148));

        if (GroupCard(asset, "management") is { } management) CardsHost.Children.Add(management);

        // Networking is short, Identity GUID-wide: they share a row 1:2.
        var networking = GroupCard(asset, "networking");
        var identity = GroupCard(asset, "identity");
        if (networking != null && identity != null) AddPair(networking, identity, 1, 2);
        else if ((networking ?? identity) is { } single) CardsHost.Children.Add(single);

        if (!string.IsNullOrEmpty(asset.Notes))
        {
            var (notes, host) = Card("Notes", AssetDetailLayout.NotesGlyph);
            host.Children.Add(new TextBlock
            {
                Text = asset.Notes, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                Style = InventoryUi.Style("BodyTextBlockStyle"),
            });
            CardsHost.Children.Add(notes);
        }

        var other = AssetDetailLayout.OtherRows(asset);
        if (other.Count > 0) CardsHost.Children.Add(RowsCard("Other", AssetDetailLayout.OtherGlyph, other));

        CardsHost.Children.Add(RowsCard("Metadata", AssetDetailLayout.MetadataGlyph, AssetDetailLayout.MetadataRows(asset, DateTime.Today)));

        if (HandbookCard(asset) is { } handbook) CardsHost.Children.Add(handbook);
    }

    /// <summary>Two cards side by side in a wide pane, one above the other in a narrow one.</summary>
    private void AddPair(FrameworkElement left, FrameworkElement right, double leftStar = 1, double rightStar = 1)
    {
        if (!_twoColumns)
        {
            CardsHost.Children.Add(left);
            CardsHost.Children.Add(right);
            return;
        }
        var grid = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(leftStar, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(rightStar, GridUnitType.Star) },
            },
        };
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        CardsHost.Children.Add(grid);
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var two = e.NewSize.Width >= TwoColumnWidth;
        if (two == _twoColumns) return;
        _twoColumns = two;
        if (_asset != null) RenderCards(_asset);
    }

    private FrameworkElement StatusCard(SnipeAsset asset)
    {
        var (card, host) = Card("Status", AssetDetailLayout.StatusGlyph);
        if (_statusLabels.Count > 0)
        {
            var combo = new ComboBox
            {
                ItemsSource = _statusLabels.Select(s => s.Name).ToList(),
                MinWidth = 160,
                IsEnabled = CanEdit,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            AutomationProperties.SetName(combo, "Status");
            AutomationProperties.SetAutomationId(combo, "InventoryStatusPicker");
            _statusInitializing = true;
            combo.SelectedIndex = _statusLabels.ToList().FindIndex(s => s.Id == asset.StatusLabel?.Id);
            _statusInitializing = false;
            combo.SelectionChanged += async (_, _) => await OnStatusChangedAsync(combo);
            host.Children.Add(LabelledRow("Status", combo, 110));
        }
        else
        {
            AddRow(host, new AssetDetailRow("Status", asset.StatusLabel?.Name, AlwaysShow: true), 110);
        }
        AddRow(host, new AssetDetailRow("Status Type", asset.StatusLabel?.StatusMeta, AlwaysShow: true), 110);
        return card;
    }

    private FrameworkElement RowsCard(string title, string glyph, IEnumerable<AssetDetailRow> rows, double labelWidth = 116)
    {
        var (card, host) = Card(title, glyph);
        foreach (var row in rows) AddRow(host, row, labelWidth);
        return card;
    }

    /// <summary>One taxonomy group as a card; null when it has no fields.</summary>
    private FrameworkElement? GroupCard(SnipeAsset asset, string slug)
    {
        var rows = AssetDetailLayout.GroupRows(asset, slug);
        if (rows.Count == 0) return null;
        var group = AssetDetailLayout.Group(slug);
        return RowsCard(group.Title, group.Glyph, rows);
    }

    private static (Border Card, StackPanel Host) Card(string title, string glyph)
    {
        var heading = new TextBlock { Text = title, Style = InventoryUi.Style("CardHeaderTextStyle"), Margin = new Thickness(0) };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level3);
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 4),
            Children =
            {
                new FontIcon { Glyph = glyph, FontSize = 12, Foreground = InventoryUi.Secondary, VerticalAlignment = VerticalAlignment.Center },
                heading,
            },
        };
        var host = new StackPanel { Spacing = 2, Children = { header } };
        var card = new Border { Style = InventoryUi.Style("CardStyle"), Padding = new Thickness(12, 10, 12, 10), Child = host };
        AutomationProperties.SetName(card, title);
        return (card, host);
    }

    private static Grid LabelledRow(string label, FrameworkElement value, double labelWidth)
    {
        var grid = new Grid
        {
            ColumnSpacing = 8,
            MinHeight = 32,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(labelWidth) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        var text = new TextBlock
        {
            Text = label, Style = InventoryUi.Style("BodyTextBlockStyle"), Foreground = InventoryUi.Secondary,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(value, 1);
        grid.Children.Add(text);
        grid.Children.Add(value);
        return grid;
    }

    /// <summary>
    /// One field: label, value ("—" when empty), and Copy and Edit, shown
    /// while the pointer or keyboard focus is on the row. A listbox field
    /// edits as the dropdown it is on the web.
    /// </summary>
    private void AddRow(StackPanel host, AssetDetailRow field, double labelWidth)
    {
        if (!field.Shows) return;
        var empty = string.IsNullOrEmpty(field.Value);
        var value = new TextBlock
        {
            Text = empty ? "—" : field.Value,
            Style = InventoryUi.Style("BodyTextBlockStyle"),
            Foreground = empty ? InventoryUi.Secondary : InventoryUi.Primary,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = !empty,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (field.Mono) value.FontFamily = InventoryUi.Mono;
        AutomationProperties.SetName(value, $"{field.Label}: {(empty ? "empty" : field.Value)}");
        var row = LabelledRow(field.Label, value, labelWidth);
        row.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        var actions = new List<Button>();
        if (field.Copyable && !empty)
        {
            var copy = RowButton(AssetDetailLayout.CopyGlyph, $"Copy {field.Label}", $"InventoryCopy{Id(field.Label)}");
            copy.Click += (_, _) => DevelopmentUi.Copy(value.Text);
            Grid.SetColumn(copy, 2);
            row.Children.Add(copy);
            actions.Add(copy);
        }
        if (field.EditKey is { } editKey && CanEdit)
        {
            var edit = RowButton(AssetDetailLayout.EditGlyph, $"Edit {field.Label}", $"InventoryEdit{Id(field.Label)}");
            edit.Click += (_, _) => BeginEdit(row, value, field, editKey);
            Grid.SetColumn(edit, 3);
            row.Children.Add(edit);
            actions.Add(edit);
        }
        if (actions.Count > 0)
        {
            void Reveal(bool on)
            {
                foreach (var a in actions) a.Opacity = on ? 1 : 0;
            }
            row.PointerEntered += (_, _) => Reveal(true);
            row.PointerExited += (_, _) => Reveal(row.FocusState != FocusState.Unfocused || actions.Any(a => a.FocusState != FocusState.Unfocused));
            foreach (var a in actions)
            {
                a.GotFocus += (_, _) => Reveal(true);
                a.LostFocus += (_, _) => Reveal(false);
            }
        }
        host.Children.Add(row);
    }

    /// <summary>A label as an AutomationId part: "Employee #" becomes "Employee".</summary>
    private static string Id(string label) => new(label.Where(char.IsLetterOrDigit).ToArray());

    private static Button RowButton(string glyph, string name, string automationId)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            Opacity = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Style = InventoryUi.Style("SubtleButtonStyle"),
        };
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetAutomationId(button, automationId);
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    private void BeginEdit(Grid row, TextBlock valueBlock, AssetDetailRow field, string editKey)
    {
        if (_asset is not { } asset || Services.Snipe is not { } snipe) return;
        var current = string.IsNullOrEmpty(field.Value) ? "" : valueBlock.Text;

        Control input;
        Func<string> read;
        if (_listboxOptions.TryGetValue(editKey, out var options) && options.Count > 0)
        {
            var combo = new ComboBox { ItemsSource = AssetDetailLayout.EditChoices(current, options), SelectedItem = current, MinWidth = 160 };
            input = combo;
            read = () => combo.SelectedItem as string ?? "";
        }
        else
        {
            var box = new TextBox { Text = current, MinWidth = 160 };
            if (field.Mono) box.FontFamily = InventoryUi.Mono;
            input = box;
            read = () => box.Text;
        }
        AutomationProperties.SetName(input, field.Label);

        var save = EditButton(AssetDetailLayout.SaveGlyph, $"Save {field.Label}");
        var cancel = EditButton(AssetDetailLayout.CancelGlyph, $"Cancel editing {field.Label}");
        var editor = new Grid
        {
            ColumnSpacing = 4,
            ColumnDefinitions = { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto }, new() { Width = GridLength.Auto } },
            Children = { input, save, cancel },
        };
        Grid.SetColumn(save, 1);
        Grid.SetColumn(cancel, 2);
        Grid.SetColumn(editor, 1);
        Grid.SetColumnSpan(editor, 3);
        var hidden = row.Children.Where(c => Grid.GetColumn((FrameworkElement)c) >= 1).ToList();
        foreach (var c in hidden) c.Visibility = Visibility.Collapsed;
        row.Children.Add(editor);

        void End()
        {
            row.Children.Remove(editor);
            foreach (var c in hidden) c.Visibility = Visibility.Visible;
        }

        async Task SaveAsync()
        {
            var newValue = read();
            save.IsEnabled = cancel.IsEnabled = false;
            try
            {
                var response = await snipe.PatchAssetFieldAsync(asset.Id, editKey, newValue);
                if (response is not { IsSuccess: true })
                {
                    ShowError($"Update failed: {response?.Messages ?? "no response"}");
                    return;
                }
                valueBlock.Text = newValue.Length == 0 ? "—" : newValue;
                valueBlock.Foreground = newValue.Length == 0 ? InventoryUi.Secondary : InventoryUi.Primary;
                End();
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[snipe] Field update failed");
                ShowError($"Update failed: {ex.Message}");
            }
            finally
            {
                save.IsEnabled = cancel.IsEnabled = true;
            }
        }

        cancel.Click += (_, _) => End();
        save.Click += async (_, _) => await SaveAsync();
        input.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                End();
            }
            else if (e.Key == VirtualKey.Enter && input is TextBox)
            {
                e.Handled = true;
                if (save.IsEnabled) _ = SaveAsync();
            }
        };
        if (input is TextBox text)
        {
            text.Loaded += (_, _) =>
            {
                text.Focus(FocusState.Programmatic);
                text.SelectAll();
            };
        }
        else input.Loaded += (_, _) => input.Focus(FocusState.Programmatic);
    }

    private static Button EditButton(string glyph, string name)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 12 }, Width = 32, Height = 32, Padding = new Thickness(0) };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    private void ShowError(string message)
    {
        ErrorBar.Title = "Snipe-IT";
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    // MARK: - Header pieces

    private static Button IdentityChip(string value, string what)
    {
        var chip = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = value, FontFamily = InventoryUi.Mono, VerticalAlignment = VerticalAlignment.Center },
                    new FontIcon { Glyph = AssetDetailLayout.CopyGlyph, FontSize = 12, Foreground = InventoryUi.Secondary },
                },
            },
            Padding = new Thickness(8, 4, 8, 4),
        };
        AutomationProperties.SetName(chip, $"Copy {what} {value}");
        AutomationProperties.SetAutomationId(chip, $"InventoryCopy{Id(what)}Chip");
        ToolTipService.SetToolTip(chip, $"Copy {what}");
        chip.Click += (_, _) => DevelopmentUi.Copy(value);
        return chip;
    }

    private static FrameworkElement StatusBadge(SnipeStatusLabel? status)
    {
        var name = status?.Name is { Length: > 0 } n ? n : "Unknown";
        var badge = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Ellipse { Width = 8, Height = 8, Fill = InventoryUi.StatusBrush(status), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = name, Style = InventoryUi.Style("BodyTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center },
            },
        };
        AutomationProperties.SetName(badge, $"Status: {name}");
        return badge;
    }

    private static FrameworkElement IconLabel(string glyph, string text, string what)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new FontIcon { Glyph = glyph, FontSize = 12, Foreground = InventoryUi.Secondary, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock
                {
                    Text = text, Style = InventoryUi.Style("BodyTextBlockStyle"), Foreground = InventoryUi.Secondary,
                    VerticalAlignment = VerticalAlignment.Center, IsTextSelectionEnabled = true,
                },
            },
        };
        AutomationProperties.SetName(panel, $"{what}: {text}");
        return panel;
    }

    /// <summary>Title, the milestone, and how much of the span has passed: caution while it runs, critical once it is over.</summary>
    private static FrameworkElement LifecycleBar(AssetLifecycleBar bar)
    {
        var header = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } },
        };
        var trailing = new TextBlock { Text = bar.Trailing, Style = InventoryUi.Style("CaptionTextBlockStyle"), Foreground = InventoryUi.Secondary };
        Grid.SetColumn(trailing, 1);
        header.Children.Add(new TextBlock { Text = bar.Title, Style = InventoryUi.Style("CaptionTextBlockStyle"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        header.Children.Add(trailing);

        // A track and its fill, 6px, as in WPF: the share of the span that has passed.
        var filled = Math.Max(bar.Fraction, 0.02);
        var fill = new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = DevelopmentUi.Theme(bar.Overdue ? "SystemFillColorCriticalBrush" : "SystemFillColorCautionBrush"),
        };
        var track = new Grid
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = DevelopmentUi.Theme("BarTrackBrush"),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(filled, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(Math.Max(1 - filled, 0.0001), GridUnitType.Star) },
            },
            Children = { fill },
        };
        AutomationProperties.SetName(track, $"{bar.Title}: {bar.Trailing}");
        return new StackPanel { Spacing = 4, Children = { header, track } };
    }

    // MARK: - Options and status

    private async Task EnsureOptionsAsync()
    {
        if (_optionsLoaded) return;
        _optionsLoaded = true;
        try
        {
            _statusLabels = await Services.GetStatusLabelsAsync();
            _listboxOptions = await Services.GetListboxOptionsAsync();
            if (_asset != null && _statusLabels.Count > 0) RenderCards(_asset);
        }
        catch (Exception ex)
        {
            // The service logs Snipe-IT's failures; the picker shows the status as text.
            Log.Debug(ex, "[snipe] Status and field options unavailable");
        }
    }

    private async Task OnStatusChangedAsync(ComboBox combo)
    {
        if (_statusInitializing || _asset is not { } asset || Services.Snipe is not { } snipe) return;
        if (combo.SelectedIndex < 0 || _statusLabels[combo.SelectedIndex] is not { } selected || selected.Id == asset.StatusLabel?.Id) return;
        try
        {
            var response = await snipe.PatchAssetFieldAsync(asset.Id, "status_id", selected.Id.ToString());
            if (response is { IsSuccess: true })
            {
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }
            ShowError($"Status update failed: {response?.Messages ?? "no response"}");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[snipe] Status update failed");
            ShowError($"Status update failed: {ex.Message}");
        }
        _statusInitializing = true;
        combo.SelectedIndex = _statusLabels.ToList().FindIndex(s => s.Id == asset.StatusLabel?.Id);
        _statusInitializing = false;
    }

    // MARK: - Actions

    private void ReAllocateButton_Click(object sender, RoutedEventArgs e) => ReAllocateRequested?.Invoke(this, EventArgs.Empty);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void CloseAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_asset != null && Services.SnipeWebUrl is { } web && !Services.DemoMode)
            DevelopmentUi.OpenUrl(AssetDetailLayout.WebUrl(web, _asset.Id));
    }

    /// <summary>
    /// The asset as an agent reads it, on the clipboard. Sending it to an
    /// agent's terminal waits for the Terminal tab; until then this copies.
    /// </summary>
    private void AgentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_asset == null) return;
        DevelopmentUi.Copy(AgentContextRenderer.Render(AgentContexts.Asset(_asset, Services.SnipeWebUrl)));
    }
}
