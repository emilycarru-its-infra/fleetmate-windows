using Microsoft.Win32;
using FleetMate.Core.Config;
using Serilog;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// The operator's own display choices, kept in HKCU\SOFTWARE\FleetMate beside
/// the theme: which tabs are switched off (<c>HiddenModules</c>), the text size
/// (<c>TextScale</c>) and whether the setup wizard has run (<c>SetupCompleted</c>).
/// None of these is managed by policy.
/// </summary>
public static class UserPreferences
{
    private static string RegistryPath => FleetMate.Core.Config.AppEdition.Current.UserRegistryPath;

    /// <summary>Raised after any preference here changes.</summary>
    public static event Action? Changed;

    public static IReadOnlySet<string> HiddenModules => AppModules.ParseHidden(Read("HiddenModules"));

    public static void SetModuleShown(string tag, bool shown)
    {
        var next = AppModules.WithModule(HiddenModules, tag, shown);
        Write("HiddenModules", next.Count == 0 ? null : AppModules.FormatHidden(next));
    }

    public static void SetHiddenModules(IEnumerable<string> hidden)
    {
        var parsed = AppModules.ParseHidden(AppModules.FormatHidden(hidden));
        Write("HiddenModules", parsed.Count == 0 ? null : AppModules.FormatHidden(parsed));
    }

    public static double TextScale => AppTextScale.Parse(Read("TextScale"));

    /// <summary>Whether a text size other than the default has been saved.</summary>
    public static bool HasTextScale => Read("TextScale") != null;

    public static void SetTextScale(double scale) =>
        Write("TextScale", AppTextScale.IsDefault(scale) ? null : AppTextScale.Format(scale));

    public static bool SetupCompleted => Read("SetupCompleted") == "1";

    public static void MarkSetupCompleted() => Write("SetupCompleted", "1");

    private static string? Read(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            return key?.GetValue(name)?.ToString();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[prefs] Could not read {Name}", name);
            return null;
        }
    }

    /// <summary>Write a value, or remove it when <paramref name="value"/> is null so the default applies.</summary>
    private static void Write(string name, string? value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            if (value == null) key.DeleteValue(name, throwOnMissingValue: false);
            else key.SetValue(name, value);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[prefs] Could not save {Name}", name);
        }
        Changed?.Invoke();
    }
}
