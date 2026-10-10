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

        // Case and punctuation drift is tolerated, as on the Mac: a status
        // type ("deployed") finds the status of that name ("Deployed").
        var items = combo.Items.Cast<object>().Where(i => i != null).ToList();
        var wanted = WidgetCatalog.MatchFilterValues(value, items.Select(i => i.ToString() ?? ""))[0];
        var match = items.FirstOrDefault(i => i.ToString() == wanted);
        if (match != null) combo.SelectedItem = match;
    }
}
