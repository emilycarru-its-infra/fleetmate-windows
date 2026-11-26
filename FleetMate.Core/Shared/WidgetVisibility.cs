using FleetMate.Core.Config;
using Microsoft.Win32;
using Serilog;

namespace FleetMate.Core.Shared;

/// <summary>
/// Whether each tab's widgets are shown — the Graphs button's state, shared
/// by FleetMate.GUI and FleetMate.WinUI. Kept per tab in the edition's HKCU
/// key under <c>widgets.collapsed.&lt;Tab&gt;</c>. Development opens on the
/// work itself — changes, diffs, commits — so its graphs start hidden; every
/// other tab starts with them shown. Development and Projects changed
/// default, so their keys carry <c>.v2</c>: a choice stored before the change
/// does not override the new default.
/// </summary>
public static class WidgetVisibility
{
    private static string RegistryPath => AppEdition.Current.UserRegistryPath;

    /// <summary>Raised with the tab whose widgets were shown or hidden.</summary>
    public static event Action<string>? Changed;

    public static string PersistenceKey(string tab) =>
        tab is "Development" or "Projects" ? $"widgets.collapsed.{tab}.v2" : $"widgets.collapsed.{tab}";

    /// <summary>Whether <paramref name="tab"/>'s graphs show before anyone has chosen.</summary>
    public static bool ShownByDefault(string tab) => tab != "Development";

    public static bool IsShown(string tab)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            return key?.GetValue(PersistenceKey(tab)) is int value ? value == 0 : ShownByDefault(tab);
        }
        catch
        {
            return ShownByDefault(tab);
        }
    }

    public static void Toggle(string tab) => SetShown(tab, !IsShown(tab));

    public static void SetShown(string tab, bool shown)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue(PersistenceKey(tab), shown ? 0 : 1, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[widgets] Could not save the shown state for {Tab}", tab);
        }
        Changed?.Invoke(tab);
    }
}
