using System.Windows;
using System.Windows.Controls;

namespace FleetMate.GUI.Views.Development;

/// <summary>
/// The Development tab: a page shell around <see cref="DevelopmentView"/>,
/// which owns the pull requests, the inbox and the activity sidebar.
/// </summary>
public partial class DevelopmentPage : Page
{
    public DevelopmentPage()
    {
        InitializeComponent();
    }

    private void OnActivityToggled(object sender, RoutedEventArgs e) =>
        View.ShowActivity(ActivityToggle.IsChecked == true);
}
