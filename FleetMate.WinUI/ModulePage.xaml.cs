using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FleetMate.WinUI;

public sealed partial class ModulePage : Page
{
    private readonly string _module;

    public ModulePage(string module)
    {
        _module = module;
        InitializeComponent();
        PageTitle.Text = module;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        Busy.IsActive = true;
        ErrorBar.IsOpen = false;
        try
        {
            var snapshot = await App.Runtime.LoadModuleAsync(_module);
            PageSummary.Text = snapshot.Summary;
            Rows.ItemsSource = snapshot.Rows;
        }
        catch (Exception ex)
        {
            Rows.ItemsSource = null;
            PageSummary.Text = "Service data is unavailable.";
            ErrorBar.Message = ex.Message.Split('\r', '\n')[0];
            ErrorBar.IsOpen = true;
        }
        finally
        {
            Busy.IsActive = false;
        }
    }
}
