using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FleetMate.Core.Models.Inventory;

namespace FleetMate.GUI.Views.Inventory;

/// <summary>
/// The Columns menu (macOS parity): show or hide the list's columns from the
/// toolbar's Columns button or a right-click on any column header. The last
/// visible column stays, and the choice is kept between launches.
/// </summary>
public partial class AssetsPage
{
    private InventoryColumns _columns = new();
    /// <summary>Every column in display order, so hidden ones go back where they were.</summary>
    private List<GridViewColumn> _allColumns = new();

    private GridView ListGrid => (GridView)AssetListView.View;

    private void InitColumns()
    {
        _allColumns = ListGrid.Columns.ToList();
        _columns = InventoryColumns.Load();
        ApplyColumns();
        AssetListView.PreviewMouseRightButtonUp += OnListRightClick;
    }

    private void ApplyColumns()
    {
        ListGrid.Columns.Clear();
        foreach (var column in _allColumns.Where(c => _columns.IsVisible(c.Header as string ?? "")))
            ListGrid.Columns.Add(column);
    }

    private ContextMenu BuildColumnsMenu()
    {
        var menu = new ContextMenu();
        foreach (var header in InventoryColumns.All)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = _columns.IsVisible(header), StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                if (!_columns.Set(header, item.IsChecked)) item.IsChecked = true; // the last column stays
                ApplyColumns();
                try { _columns.Save(); }
                catch (Exception ex) { Serilog.Log.Debug(ex, "Inventory: could not save the column layout"); }
            };
            menu.Items.Add(item);
        }
        return menu;
    }

    private void OnColumnsClicked(object sender, RoutedEventArgs e)
    {
        var menu = BuildColumnsMenu();
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnListRightClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source is not GridViewColumnHeader)
            source = VisualTreeHelper.GetParent(source);
        if (source == null) return;

        var menu = BuildColumnsMenu();
        menu.PlacementTarget = (UIElement)source;
        menu.IsOpen = true;
        e.Handled = true;
    }
}
