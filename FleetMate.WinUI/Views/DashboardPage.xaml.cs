using Microsoft.UI.Xaml.Controls;

namespace FleetMate.WinUI.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
        LoadServiceStatus();
    }

    private void LoadServiceStatus()
    {
        var app = App.Current;
        var items = new List<object>
        {
            new { Name = "Microsoft Graph (Devices / Identity)", Status = app.GraphService != null ? "Configured" : "Not configured" },
            new { Name = "Snipe-IT (Inventory)", Status = app.SnipeService != null ? "Configured" : "Not configured" },
            new { Name = "TeamDynamix (Tickets)", Status = app.TdxService != null ? "Configured" : "Not configured" },
            new { Name = "Azure DevOps (Projects)", Status = app.DevOpsService != null ? "Configured" : "Not configured" },
            new { Name = "ReportMate", Status = app.ReportMateService != null ? "Configured" : "Not configured" },
        };
        ServicesList.ItemsSource = items;
    }
}
