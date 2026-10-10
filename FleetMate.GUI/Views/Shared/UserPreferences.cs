using Microsoft.Win32;
using FleetMate.Core.Config;
using Serilog;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// The operator's own display choices, kept in HKCU\SOFTWARE\FleetMate beside
/// the theme: which tabs are switched off (<c>HiddenModules</c>), the text size
/// (<c>TextScale</c>), the terminal's offset from it (<c>agentTerminalFontOffset</c>),
/// whether the setup wizard has run (<c>SetupCompleted</c>)
/// whether Tickets shows only the operator's own (<c>tickets.assignedToMe</c>,
/// the macOS app's key), and how Development's Pulls list is sorted and filtered
/// (<c>development.pulls.*</c>, the macOS app's keys).
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

    /// <summary>Points the terminal's text is above or below the app's text size.</summary>
    public static double TerminalFontOffset =>
        double.TryParse(Read("agentTerminalFontOffset"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;

    public static void SetTerminalFontOffset(double offset) =>
        Write("agentTerminalFontOffset", offset == 0 ? null : offset.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static bool SetupCompleted => Read("SetupCompleted") == "1";

    public static void MarkSetupCompleted() => Write("SetupCompleted", "1");

    public static bool TicketsAssignedToMe => Read("tickets.assignedToMe") == "1";

    public static void SetTicketsAssignedToMe(bool on) => Write("tickets.assignedToMe", on ? "1" : null);

    /// <summary>Pulls sections per repository instead of one list by last modified.</summary>
    public static bool PullsGroupedByRepository => Read("development.pulls.grouped") == "1";

    public static void SetPullsGroupedByRepository(bool on) => Write("development.pulls.grouped", on ? "1" : null);

    /// <summary>Pulls least recently modified first instead of most.</summary>
    public static bool PullsOldestFirst => Read("development.pulls.oldestFirst") == "1";

    public static void SetPullsOldestFirst(bool on) => Write("development.pulls.oldestFirst", on ? "1" : null);

    /// <summary>The repository Pulls is filtered to, "owner/repo" or "Project/Repo"; null for all.</summary>
    public static string? PullsRepository => Read("development.pulls.repository") is { Length: > 0 } repo ? repo : null;

    public static void SetPullsRepository(string? repository) => Write("development.pulls.repository", repository);

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
