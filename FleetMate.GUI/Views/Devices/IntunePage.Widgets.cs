using System.Windows.Controls;
using FleetMate.GUI.Views.Shared.Widgets;

namespace FleetMate.GUI.Views.Devices;

/// <summary>Devices widget clicks set the page's Platform filter and Non-Compliant toggle.</summary>
public partial class IntunePage : IWidgetFilterHost
{
    public void ApplyWidgetFilter(string category, string value)
    {
        switch (category)
        {
            case WidgetCatalog.Category.Platform:
                var match = PlatformFilterComboBox.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => string.Equals(i.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase));
                if (match != null) PlatformFilterComboBox.SelectedItem = match;
                break;

            case WidgetCatalog.Category.Compliance:
                NonCompliantOnlyCheckBox.IsChecked = value == "Non-Compliant";
                break;
        }
    }
}
