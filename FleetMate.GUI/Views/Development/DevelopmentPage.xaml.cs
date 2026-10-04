using System.Windows;
using System.Windows.Controls;
using FleetMate.GUI.Views.Shared.Widgets;

namespace FleetMate.GUI.Views.Development;

/// <summary>
/// The Development tab: a page shell around <see cref="DevelopmentView"/>,
/// which owns the pull requests, the inbox and the activity sidebar.
/// </summary>
public partial class DevelopmentPage : Page, IWidgetFilterHost
{
    /// <summary>A Pull Requests by Repository bar opens Pulls filtered to that repository.</summary>
    public void ApplyWidgetFilter(string category, string value)
    {
        if (category == WidgetCatalog.Category.Repository) View.ShowRepository(value);
    }

    public DevelopmentPage()
    {
        InitializeComponent();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await View.RefreshAsync();
}
