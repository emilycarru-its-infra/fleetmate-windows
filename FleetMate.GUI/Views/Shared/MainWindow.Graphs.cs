using System.Windows;
using System.Windows.Media;
using FleetMate.GUI.Views.Shared.Widgets;

namespace FleetMate.GUI.Views.Shared;

/// <summary>The toolbar Graphs button: shows or hides the open tab's widget strip.</summary>
public partial class MainWindow
{
    private bool _graphsHooked;

    private void OnGraphsClicked(object sender, RoutedEventArgs e) => ToggleGraphs();

    private void ToggleGraphs()
    {
        var tab = CurrentTab;
        if (!WidgetCatalog.HasWidgets(tab)) return;
        WidgetVisibility.Toggle(tab);
    }

    private void UpdateGraphsButton()
    {
        if (!_graphsHooked)
        {
            _graphsHooked = true;
            WidgetVisibility.Changed += _ => Dispatcher.Invoke(UpdateGraphsButton);
        }

        var tab = CurrentTab;
        if (!WidgetCatalog.HasWidgets(tab))
        {
            GraphsButton.Visibility = Visibility.Collapsed;
            return;
        }

        var shown = WidgetVisibility.IsShown(tab);
        GraphsButton.Visibility = Visibility.Visible;
        GraphsButton.ToolTip = shown ? "Hide Graphs (Ctrl+Alt+G)" : "Show Graphs (Ctrl+Alt+G)";
        GraphsIcon.Foreground = (Brush)FindResource(shown
            ? "SystemControlForegroundAccentBrush"
            : "SystemControlForegroundBaseMediumBrush");
    }
}
