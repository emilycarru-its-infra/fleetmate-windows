using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FleetMate.GUI.Views.Development;

/// <summary>
/// Group name → the header's text for the converter parameter: "toggle" for
/// the menu item, "tip" for the tooltip, "status" for the accessibility status.
/// </summary>
public sealed class PullGroupTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value as string ?? "";
        var collapsed = PullGroupCollapse.Collapsed.Contains(key);
        return parameter as string switch
        {
            "toggle" => PullGroupCollapse.ToggleLabel(collapsed),
            "tip" => PullGroupCollapse.Tip(key, collapsed),
            _ => PullGroupCollapse.Status(collapsed),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Group name → Collapsed when that group is collapsed, for the group's rows.</summary>
public sealed class PullGroupRowsVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key && PullGroupCollapse.Collapsed.Contains(key) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Group name → the disclosure chevron: right when collapsed, down when expanded.</summary>
public sealed class PullGroupChevronConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key && PullGroupCollapse.Collapsed.Contains(key) ? "" : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
