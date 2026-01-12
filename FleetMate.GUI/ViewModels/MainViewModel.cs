using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FleetMate.GUI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "FleetMate";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    public MainViewModel()
    {
        StatusMessage = "Ready";
    }

    [RelayCommand]
    private void Refresh()
    {
        IsLoading = true;
        StatusMessage = "Refreshing...";

        // Placeholder for actual refresh logic
        Task.Run(async () =>
        {
            await Task.Delay(500);
            StatusMessage = "Ready";
            IsLoading = false;
        });
    }
}
