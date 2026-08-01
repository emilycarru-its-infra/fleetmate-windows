using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace FleetMate.WinUI;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN) so we can log startup crashes
/// before the WinUI runtime is fully up. Mirrors the Cimian Managed Software Center
/// bootstrap: init COM wrappers, start the WinUI message pump, and install a
/// DispatcherQueue SynchronizationContext so async/await marshals to the UI thread.
/// </summary>
public static class Program
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".fleetmate", "winui_crash.log");

    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            ComWrappersSupport.InitializeComWrappers();

            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
        }
        catch (Exception ex)
        {
            Log($"CRASH: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
            Environment.Exit(1);
        }
    }

    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { /* logging is best-effort */ }
    }
}
