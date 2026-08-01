using Microsoft.UI.Xaml;
using FleetMate.Core.Services;
using Microsoft.Win32;

namespace FleetMate.WinUI;

public partial class App : Application
{
    public static MainWindow Window { get; private set; } = null!;
    public static DesktopRuntime Runtime { get; } = new();

    public App()
    {
        UnhandledException += (_, e) =>
        {
            var path = Path.Combine(Path.GetTempPath(), "fleetmate-winui-crash.log");
            File.WriteAllText(path, e.Exception?.ToString() ?? e.Message);
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\FleetMate"))
        {
            if (Window.Content is FrameworkElement root)
                root.RequestedTheme = key?.GetValue("UiTheme")?.ToString() switch
                {
                    "Light" => ElementTheme.Light,
                    "Dark" => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };
        }
        Window.Activate();
        EntraTokenSource.ParentWindowProvider = () => WinRT.Interop.WindowNative.GetWindowHandle(Window);
    }
}
