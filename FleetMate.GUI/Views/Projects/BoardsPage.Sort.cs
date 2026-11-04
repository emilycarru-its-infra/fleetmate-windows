using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FleetMate.Core.Models.Projects;
using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Projects;

/// <summary>
/// Sort Queries By, the macOS toolbar Sort menu and View › Sort Queries By:
/// the four orders, then the two directions worded for the chosen order.
/// Picking a new order starts it in its natural direction. The choice is
/// remembered in the user's preferences.
/// </summary>
public partial class BoardsPage
{
    private void OnSortQueriesClick(object sender, RoutedEventArgs e)
    {
        BuildSortQueriesMenu();
        SortQueriesMenu.PlacementTarget = SortQueriesButton;
        SortQueriesMenu.Placement = PlacementMode.Bottom;
        SortQueriesMenu.IsOpen = true;
    }

    private void BuildSortQueriesMenu()
    {
        var current = UserPreferences.QueriesOrder;
        var ascending = UserPreferences.QueriesOrderAscending;
        SortQueriesMenu.Items.Clear();

        foreach (var order in QueryListOrders.All)
        {
            var item = new MenuItem { Header = order.Title(), IsCheckable = true, IsChecked = order == current };
            item.Click += (_, _) =>
                QueriesList.SetOrder(order, order == current ? ascending : order.DefaultAscending());
            SortQueriesMenu.Items.Add(item);
        }

        SortQueriesMenu.Items.Add(new Separator());

        foreach (var direction in new[] { current.DefaultAscending(), !current.DefaultAscending() })
        {
            var item = new MenuItem
            {
                Header = current.DirectionTitle(direction),
                IsCheckable = true,
                IsChecked = direction == ascending
            };
            item.Click += (_, _) => QueriesList.SetOrder(current, direction);
            SortQueriesMenu.Items.Add(item);
        }
    }
}
