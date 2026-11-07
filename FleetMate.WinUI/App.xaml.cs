using Microsoft.UI.Xaml;

namespace FleetMate.WinUI;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        // `--theme light|dark` forces a theme, for screenshots and checks.
        // Application.RequestedTheme can only be set before the first window,
        // and setting it here also themes the caption buttons.
        var args = Environment.GetCommandLineArgs();
        var i = Array.FindIndex(args, a => a.Equals("--theme", StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < args.Length)
        {
            if (args[i + 1].Equals("dark", StringComparison.OrdinalIgnoreCase)) RequestedTheme = ApplicationTheme.Dark;
            else if (args[i + 1].Equals("light", StringComparison.OrdinalIgnoreCase)) RequestedTheme = ApplicationTheme.Light;
        }

        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
