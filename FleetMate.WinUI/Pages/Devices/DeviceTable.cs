using FleetMate.Core.Devices;
using FleetMate.Core.Models.Devices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Serilog;

namespace FleetMate.WinUI.Pages.Devices;

/// <summary>
/// The Devices list as a table: a header of sortable columns over a
/// virtualized, multi-select ListView whose rows draw only the columns
/// shown. The same columns for every row, a dash where its sources have no
/// value (<see cref="DeviceListColumns"/>). Right-click (or Shift+F10 on) a
/// header to show or hide the optional columns or move one; the layout is
/// kept in the file FleetMate.GUI keeps it in, so both apps share it.
/// </summary>
public sealed partial class DeviceTable : UserControl
{
    private const double CellPadding = 12;

    private readonly Grid _header = new() { Padding = new Thickness(CellPadding, 0, CellPadding, 0), MinHeight = 32 };
    private readonly ScrollViewer _scroller;
    private readonly Grid _body = new();
    private DeviceColumnLayout _layout = new();
    private List<DeviceColumn> _columns = new();
    private DevicesViewModel? _model;

    public DeviceTable()
    {
        List = new ListView
        {
            SelectionMode = ListViewSelectionMode.Extended,
            IsMultiSelectCheckBoxEnabled = false,
        };
        AutomationProperties.SetName(List, "Devices");
        AutomationProperties.SetAutomationId(List, "DevicesList");
        List.ContainerContentChanging += OnContainerContentChanging;
        List.ItemContainerStyle = RowStyle();
        List.ContextFlyout = RowMenu();

        _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var rule = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            Child = _header,
        };
        _body.Children.Add(rule);
        Grid.SetRow(List, 1);
        _body.Children.Add(List);

        // Wide tables scroll sideways as one, header and rows together.
        _scroller = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _body,
        };
        Content = _scroller;
        _layout = LoadLayout();
        _columns = DeviceListColumns.Visible(_layout);
        BuildHeader();
    }

    /// <summary>The row list; the page reads and sets its selection.</summary>
    public ListView List { get; }

    /// <summary>The visible columns, in order.</summary>
    public IReadOnlyList<DeviceColumn> Columns => _columns;

    public void Attach(DevicesViewModel model)
    {
        _model = model;
        List.ItemsSource = model.Rows;
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DevicesViewModel.SortColumn) or nameof(DevicesViewModel.SortDescending)) BuildHeader();
        };
    }

    /// <summary>Draw every realized row again, after a value read on selection (Activation Lock) changed.</summary>
    public void Refresh()
    {
        for (var i = 0; i < List.Items.Count; i++)
            if (List.ContainerFromIndex(i) is ListViewItem { ContentTemplateRoot: DeviceRowCells cells } && List.Items[i] is DeviceListRow row)
                cells.Bind(row, _columns);
    }

    /// <summary>Every row is one <see cref="DeviceRowCells"/>, filled as its container is realized; set from the page's XAML.</summary>
    public DataTemplate? RowTemplate
    {
        get => List.ItemTemplate;
        set => List.ItemTemplate = value;
    }

    private double TableWidth => _columns.Sum(c => c.Width) + 2 * CellPadding;

    // MARK: - Header

    private void BuildHeader()
    {
        _header.Children.Clear();
        _header.ColumnDefinitions.Clear();
        _body.Width = TableWidth;
        for (var i = 0; i < _columns.Count; i++)
        {
            var column = _columns[i];
            _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(column.Width) });
            var sorted = _model?.SortColumn == column.Header;
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            label.Children.Add(new TextBlock
            {
                Text = column.Header,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            });
            if (sorted)
                label.Children.Add(new FontIcon
                {
                    Glyph = _model!.SortDescending ? "" : "",
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });

            var button = new Button
            {
                Content = label,
                Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 4, 4, 4),
                Margin = new Thickness(0, 0, 4, 0),
                IsEnabled = true,
            };
            var name = sorted
                ? $"{column.Header}, sorted {(_model!.SortDescending ? "descending" : "ascending")}"
                : column.Header;
            AutomationProperties.SetName(button, name);
            AutomationProperties.SetAutomationId(button, $"DevicesColumn{new string(column.Header.Where(char.IsLetterOrDigit).ToArray())}");
            ToolTipService.SetToolTip(button, column.Sortable
                ? $"Sort by {column.Header}. Right-click to show, hide or move columns"
                : "Right-click to show, hide or move columns");
            var header = column.Header;
            if (column.Sortable) button.Click += (_, _) => _model?.SortBy(header);
            button.ContextFlyout = ColumnMenu(header);
            Grid.SetColumn(button, i);
            _header.Children.Add(button);
        }
        Refresh();
    }

    /// <summary>Show or hide the optional columns the connected services fill, and move this one.</summary>
    private MenuFlyout ColumnMenu(string header)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var rows = _model?.AllRows ?? Array.Empty<DeviceListRow>();
            foreach (var optional in DeviceListColumns.Optional)
            {
                var shown = _columns.Any(c => c.Header == optional);
                if (!shown && !DeviceListColumns.CanFill(optional, rows.ToList())) continue;
                var item = new ToggleMenuFlyoutItem { Text = optional, IsChecked = shown };
                var target = optional;
                item.Click += (_, _) => SetShown(target, item.IsChecked);
                menu.Items.Add(item);
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            var left = new MenuFlyoutItem { Text = $"Move {header} Left", Icon = new FontIcon { Glyph = "" } };
            left.Click += (_, _) => Move(header, -1);
            var right = new MenuFlyoutItem { Text = $"Move {header} Right", Icon = new FontIcon { Glyph = "" } };
            right.Click += (_, _) => Move(header, +1);
            var order = _columns.Select(c => c.Header).ToList();
            left.IsEnabled = order.IndexOf(header) > 0;
            right.IsEnabled = order.IndexOf(header) < order.Count - 1;
            menu.Items.Add(left);
            menu.Items.Add(right);
            var reset = new MenuFlyoutItem { Text = "Reset Columns" };
            reset.Click += (_, _) => Apply(new DeviceColumnLayout());
            menu.Items.Add(reset);
        };
        menu.Items.Add(new MenuFlyoutItem { Text = "Columns" });
        return menu;
    }

    private void SetShown(string header, bool shown)
    {
        var showing = _columns.Where(c => c.Optional).Select(c => c.Header).ToHashSet();
        if (shown) showing.Add(header);
        else showing.Remove(header);
        Apply(DeviceListColumns.Layout(showing, CurrentOrder()));
    }

    private void Move(string header, int step)
    {
        // Move within the shown columns, keeping hidden ones where they were.
        var shown = _columns.Select(c => c.Header).ToList();
        var moved = DeviceListColumns.Move(shown, header, step);
        var full = CurrentOrder();
        var slots = full.Select((h, i) => (h, i)).Where(x => shown.Contains(x.h)).Select(x => x.i).ToList();
        for (var k = 0; k < slots.Count; k++) full[slots[k]] = moved[k];
        Apply(DeviceListColumns.Layout(_columns.Where(c => c.Optional).Select(c => c.Header), full));
    }

    private List<string> CurrentOrder() => _layout.Arrange(DeviceListColumns.DefaultOrder.ToList());

    private void Apply(DeviceColumnLayout layout)
    {
        _layout = layout;
        _columns = DeviceListColumns.Visible(layout);
        SaveLayout(layout);
        BuildHeader();
    }

    // MARK: - Rows

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not DeviceListRow row) return;
        if (args.ItemContainer.ContentTemplateRoot is DeviceRowCells cells) cells.Bind(row, _columns);
        AutomationProperties.SetName(args.ItemContainer, RowName(row));
        args.Handled = true;
    }

    /// <summary>What a screen reader says for a row: name, serial, platform and compliance.</summary>
    private static string RowName(DeviceListRow row) =>
        string.Join(", ", new[] { DeviceListText.Label(row), row.SerialText, row.PlatformText, row.ComplianceText }
            .Where(s => s != DeviceListRow.Missing).Distinct());

    private static Style RowStyle()
    {
        var style = new Style(typeof(ListViewItem)) { BasedOn = (Style)Application.Current.Resources["DefaultListViewItemStyle"] };
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(CellPadding, 0, CellPadding, 0)));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 36.0));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        return style;
    }

    /// <summary>Copy a row's name or serial; the Terminal's Send to Agent joins once that tab is ported.</summary>
    private MenuFlyout RowMenu()
    {
        var menu = new MenuFlyout();
        void Add(string text, Func<DeviceListRow, string?> value)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) =>
            {
                var rows = List.SelectedItems.OfType<DeviceListRow>().ToList();
                var lines = rows.Select(value).Where(v => !string.IsNullOrEmpty(v) && v != DeviceListRow.Missing);
                Development.DevelopmentUi.Copy(string.Join(Environment.NewLine, lines));
            };
            menu.Items.Add(item);
        }
        Add("Copy Name", r => r.NameText);
        Add("Copy Serial", r => r.SerialNumber);
        Add("Copy Intune ID", r => r.Intune?.Id);
        return menu;
    }

    // MARK: - Layout file

    /// <summary>The file FleetMate.GUI keeps the same layout in, so both apps show the same columns.</summary>
    private static string LayoutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FleetMate", "devices-columns.json");

    private static DeviceColumnLayout LoadLayout()
    {
        // A demo or QA capture shows the defaults and never reads the person's own layout.
        if (App.Services?.DemoMode != false) return new DeviceColumnLayout();
        try
        {
            return File.Exists(LayoutPath) ? DeviceColumnLayout.Parse(File.ReadAllText(LayoutPath)) : new DeviceColumnLayout();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Devices: could not restore column layout");
            return new DeviceColumnLayout();
        }
    }

    private static void SaveLayout(DeviceColumnLayout layout)
    {
        if (App.Services?.DemoMode != false) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LayoutPath)!);
            File.WriteAllText(LayoutPath, layout.Serialize());
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Devices: could not save column layout");
        }
    }
}

/// <summary>
/// One row's cells, drawn for the columns shown: text cells, the
/// Compliance dot and the Activation Lock state, which turns to the
/// caution colour when locked.
/// </summary>
public sealed partial class DeviceRowCells : Grid
{
    public void Bind(DeviceListRow row, IReadOnlyList<DeviceColumn> columns)
    {
        Children.Clear();
        ColumnDefinitions.Clear();
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(column.Width) });
            var text = new TextBlock
            {
                Text = column.Text(row),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                Margin = new Thickness(0, 0, 8, 0),
            };
            if (text.Text == DeviceListRow.Missing)
                text.Foreground = DeviceUi.Theme("TextFillColorTertiaryBrush");
            if (column.Header == "Activation Lock" && row.ActivationLockIsLocked)
                text.Foreground = DeviceUi.ToneBrush(DeviceTone.Attention);
            // A trimmed value shows in full on hover.
            text.IsTextTrimmedChanged += (s, _) => ToolTipService.SetToolTip(s, s.IsTextTrimmed ? s.Text : null);

            FrameworkElement cell = text;
            if (column.Header == "Compliance")
            {
                var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                    Fill = DeviceUi.ToneBrush(DeviceDetailSections.RowTone(row.ComplianceTone)),
                };
                var holder = new Grid
                {
                    Margin = new Thickness(0, 0, 8, 0),
                    ColumnDefinitions = { new() { Width = GridLength.Auto }, new() { Width = new GridLength(1, GridUnitType.Star) } },
                };
                text.Margin = new Thickness(0);
                SetColumn(text, 1);
                holder.Children.Add(dot);
                holder.Children.Add(text);
                cell = holder;
            }
            SetColumn(cell, i);
            Children.Add(cell);
        }
    }
}
