using System.Windows.Controls;
using FleetMate.GUI.Views.Shared.Widgets;

namespace FleetMate.GUI.Views.Inventory;

/// <summary>Inventory widget clicks set the page's own Category and Status filters.</summary>
public partial class AssetsPage : IWidgetFilterHost
{
    public void ApplyWidgetFilter(string category, string value)
    {
        var combo = category switch
        {
            WidgetCatalog.Category.AssetCategory => CategoryFilterComboBox,
            WidgetCatalog.Category.Status => StatusFilterComboBox,
            _ => null,
        };
        if (combo == null) return;

        var match = combo.Items.Cast<object>().FirstOrDefault(i => i?.ToString() == value);
        if (match != null) combo.SelectedItem = match;
    }
}
