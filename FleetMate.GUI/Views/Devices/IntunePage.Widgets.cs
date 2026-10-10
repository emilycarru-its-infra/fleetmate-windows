using FleetMate.Core.Models.Devices;
using FleetMate.GUI.Views.Shared.Widgets;

namespace FleetMate.GUI.Views.Devices;

/// <summary>
/// Devices widget clicks set the list's own filters: a Platform tile picks
/// that platform, a Compliance wedge or the Non-Compliant figure that state.
/// </summary>
public partial class IntunePage : IWidgetFilterHost
{
    public void ApplyWidgetFilter(string category, string value)
    {
        DeviceFacet? facet = category switch
        {
            WidgetCatalog.Category.Platform => DeviceFacet.Platform,
            WidgetCatalog.Category.Compliance => DeviceFacet.Compliance,
            _ => null,
        };
        if (facet is not { } f) return;

        // The widgets name platforms as the Mac does ("Macintosh", "iOS/iPadOS");
        // the filter holds Intune's names, so each is matched through that label.
        var available = DeviceFacets.Counts(_allRows, f).Select(c => c.Value);
        SetFacetFilter(f, WidgetCatalog.MatchFilterValues(value, available,
            f == DeviceFacet.Platform ? WidgetCatalog.PlatformLabel : null));
    }
}
