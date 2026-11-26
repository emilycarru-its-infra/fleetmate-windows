using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FleetMate.Core.Inventory;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Shared;
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

    public AssetDetailPanel()
    {
        InitializeComponent();
    }

    public void Show(SnipeAsset asset)
    {
        _asset = asset;
        _service = (Application.Current as App)?.SnipeService;
        AgentButton.Context = AgentContexts.Asset(asset, (Application.Current as App)?.Config.SnipeUrl);
        Render();
        _historyAssetId = null; // a fresh Show (new asset, or a save) refetches history
        ShowSegment();
        _ = EnsureOptionsAsync();
    }

    // ── Rendering ────────────────────────────────────────────────────────

    private void Render()
    {
        if (_asset is not { } asset) return;

        CategoryIcon.Glyph = AssetDetailLayout.CategoryGlyph(asset.Category?.Name);
        NameText.Text = asset.DisplayName;
        ModelText.Text = asset.Model?.Name ?? "";
        ModelText.Visibility = string.IsNullOrEmpty(asset.Model?.Name) ? Visibility.Collapsed : Visibility.Visible;

        ReAllocateButton.Visibility = AssetDetailLayout.CanReAllocate(asset) ? Visibility.Visible : Visibility.Collapsed;

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
        var (person, place) = AssetDetailLayout.WhoWhere(asset);
        if (person != null) WhoWhereRow.Children.Add(IconLabel(AssetDetailLayout.PersonGlyph, person));
        if (place != null) WhoWhereRow.Children.Add(IconLabel(AssetDetailLayout.PlaceGlyph, place));
        WhoWhereRow.Visibility = WhoWhereRow.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderLifecycle(SnipeAsset asset)
    {
        LifecycleHost.Children.Clear();
        foreach (var bar in AssetDetailLayout.Lifecycle(asset, DateTime.Today))
            LifecycleHost.Children.Add(LifecycleBar(bar.Title, bar.Trailing, bar.Fraction));
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
            var assignment = RowsCard("Assignment", AssetDetailLayout.AssignmentGlyph, AssetDetailLayout.AssignmentRows(asset));
            assignment.Margin = new Thickness(0, 12, 0, 0);
            rightStack.Children.Add(assignment);
        }
        var inventory = AssetDetailLayout.Group("inventory");
        AddCardRow(RowsCard(inventory.Title, inventory.Glyph, AssetDetailLayout.InventoryRows(asset)), rightStack);

        AddCardRow(RowsCard("Hardware", AssetDetailLayout.HardwareGlyph, AssetDetailLayout.HardwareRows(asset)),
            RowsCard("Procurement", AssetDetailLayout.Group("procurement").Glyph, AssetDetailLayout.ProcurementRows(asset), 150));

        var management = GroupCard(asset, "management");
        if (management != null) AddFullCard(management);

        // Networking is short, Identity is GUID-wide: they share a row 1:2.
        var networking = GroupCard(asset, "networking");
        var identity = GroupCard(asset, "identity");
        if (networking != null || identity != null)
            AddCardRow(networking ?? EmptySpacer(), identity ?? EmptySpacer(), 1, 2);

        if (!string.IsNullOrEmpty(asset.Notes))
        {
            var notes = Card("Notes", AssetDetailLayout.NotesGlyph);
            ((StackPanel)notes.Child!).Children.Add(new TextBlock
            {
                Text = asset.Notes,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Secondary()
            });
            AddFullCard(notes);
        }

        var other = AssetDetailLayout.OtherRows(asset);
        if (other.Count > 0) AddFullCard(RowsCard("Other", AssetDetailLayout.OtherGlyph, other));

        AddFullCard(RowsCard("Metadata", AssetDetailLayout.MetadataGlyph, AssetDetailLayout.MetadataRows(asset, DateTime.Today)));

        if (HandbookCard(asset) is { } handbook) AddFullCard(handbook);
    }

    private Border StatusCard(SnipeAsset asset)
    {
        var card = Card("Status", AssetDetailLayout.StatusGlyph);
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
            AddRow(host, new AssetDetailRow("Status", asset.StatusLabel?.Name, AlwaysShow: true));
        }

        AddRow(host, new AssetDetailRow("Status Type", asset.StatusLabel?.StatusMeta, AlwaysShow: true));
        return card;
    }

    private Border RowsCard(string title, string glyph, IEnumerable<AssetDetailRow> rows, double labelWidth = 110)
    {
        var card = Card(title, glyph);
        var host = (StackPanel)card.Child!;
        foreach (var row in rows) AddRow(host, row, labelWidth);
        return card;
    }

    /// <summary>One taxonomy group as a card; null when it has no fields.</summary>
    private Border? GroupCard(SnipeAsset asset, string slug)
    {
        var rows = AssetDetailLayout.GroupRows(asset, slug);
        if (rows.Count == 0) return null;
        var group = AssetDetailLayout.Group(slug);
        return RowsCard(group.Title, group.Glyph, rows);
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
    private void AddRow(StackPanel host, AssetDetailRow field, double labelWidth = 110)
    {
        if (!field.Shows) return;
        var (label, value, copyable, mono, editKey) = (field.Label, field.Value, field.Copyable, field.Mono, field.EditKey);

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
            var items = AssetDetailLayout.EditChoices(current, options);
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
        var color = AssetDetailLayout.StatusTone(status) switch
        {
            AssetStatusTone.Deployed => "#27ae60",
            AssetStatusTone.Deployable => "#3182CE",
            AssetStatusTone.Pending => "#d69e2e",
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

    private async Task EnsureOptionsAsync()
    {
        if (_service == null) return;
        if (_statusLabels.Count == 0)
        {
            try
            {
                _statusLabels = await _service.GetStatusLabelsAsync();
                if (_asset != null) Render();
            }
            catch (SnipeException)
            {
                // Logged by the service; the status picker simply has no options.
            }
        }
        if (!_optionsLoaded)
        {
            _optionsLoaded = true;
            _listboxOptions = AssetDetailLayout.ListboxOptions(await _service.GetFieldDefinitionsAsync());
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
            Process.Start(new ProcessStartInfo(AssetDetailLayout.WebUrl(_service.BaseUrl, _asset.Id)) { UseShellExecute = true });
        }
        catch { }
    }
}
