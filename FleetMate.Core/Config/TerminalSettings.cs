namespace FleetMate.Core.Config;

/// <summary>
/// The agent terminal panel's settings. The names match the macOS client:
/// AgentCommand, AgentAutoStart, Repos (the operator's own list) and
/// RepoDefaults (the managed default list from policy).
///
/// AgentCommand and AgentAutoStart come in three layers: the operator's own
/// value (HKCU\SOFTWARE\FleetMate) wins, then the managed value
/// (SOFTWARE\Policies\FleetMate), then the built-in default (shell, on).
/// Unlike FleetMate's other managed settings, policy here supplies a default
/// the operator can change.
/// </summary>
public sealed class TerminalSettings
{
    public const bool DefaultAutoStart = true;

    /// <summary>The operator's own AgentCommand; "" is an explicit choice of the shell. Null when unset.</summary>
    public string? UserAgentCommand { get; set; }
    public string? PolicyAgentCommand { get; set; }
    public bool? UserAgentAutoStart { get; set; }
    public bool? PolicyAgentAutoStart { get; set; }

    /// <summary>What sessions run: "" or "shell", a preset key, or a custom command line.</summary>
    public string AgentCommand => (UserAgentCommand ?? PolicyAgentCommand ?? "").Trim();

    /// <summary>Open a session when FleetMate starts.</summary>
    public bool AgentAutoStart => UserAgentAutoStart ?? PolicyAgentAutoStart ?? DefaultAutoStart;

    /// <summary>What AgentCommand falls back to without the operator's own value.</summary>
    public string AgentCommandFallback => (PolicyAgentCommand ?? "").Trim();
    public bool AgentAutoStartFallback => PolicyAgentAutoStart ?? DefaultAutoStart;

    public bool AgentCommandFromPolicy => UserAgentCommand == null && PolicyAgentCommand != null;
    public bool AgentAutoStartFromPolicy => UserAgentAutoStart == null && PolicyAgentAutoStart != null;

    /// <summary>The operator's repos: local paths or clone URLs.</summary>
    public List<string> Repos { get; set; } = new();

    /// <summary>The managed default repos, from policy. Seeds <see cref="Repos"/> while that is empty.</summary>
    public List<string> RepoDefaults { get; set; } = new();

    /// <summary>The list the panel offers: the operator's own, or the managed defaults until they make one.</summary>
    public IReadOnlyList<string> EffectiveRepos => Repos.Count > 0 ? Repos : RepoDefaults;

    /// <summary>A list value from the registry: REG_MULTI_SZ, or text with one entry per line or separated by semicolons.</summary>
    public static List<string> ParseList(object? value) => value switch
    {
        string[] items => items.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        string text => text.Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(i => i.Trim()).Where(i => i.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        _ => new List<string>(),
    };

    public static bool? ParseBool(object? value) => value?.ToString()?.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "yes" or "on" => true,
        "0" or "false" or "no" or "off" => false,
        _ => null,
    };
}

/// <summary>
/// Settings ▸ Terminal's "Runs" picker. Each choice stores its key in
/// AgentCommand: "" for Shell, the preset's key, or for Custom the command
/// line typed beside it.
/// </summary>
public static class AgentCommandPicker
{
    public const string Custom = "custom";

    public static readonly IReadOnlyList<(string Key, string Label)> Choices = new[]
    {
        ("", "Shell"),
        ("claude", "Claude"),
        ("codex", "Codex"),
        ("claude-remote", "Claude (remote)"),
        ("codex-remote", "Codex (remote)"),
        (Custom, "Custom…"),
    };

    /// <summary>The choices to show: Shell, Custom, and each preset <paramref name="isInstalled"/> accepts.</summary>
    public static IReadOnlyList<(string Key, string Label)> InstalledChoices(Func<string, bool> isInstalled) =>
        Choices.Where(c => c.Key.Length == 0 || c.Key == Custom || isInstalled(c.Key)).ToList();

    /// <summary>
    /// The picker choice and custom text a stored AgentCommand shows as. A
    /// preset that <paramref name="isInstalled"/> rejects is not in the picker,
    /// so it shows as a custom command rather than a blank choice.
    /// </summary>
    public static (string Key, string CustomCommand) FromSetting(string? agentCommand, Func<string, bool>? isInstalled = null)
    {
        var value = agentCommand?.Trim() ?? "";
        if (value.Length == 0 || value.Equals("shell", StringComparison.OrdinalIgnoreCase)) return ("", "");
        foreach (var (key, _) in Choices)
            if (key.Length > 0 && key != Custom && key.Equals(value, StringComparison.OrdinalIgnoreCase))
                return isInstalled == null || isInstalled(key) ? (key, "") : (Custom, value);
        return (Custom, value);
    }

    /// <summary>The AgentCommand a picker choice stores. Custom with nothing typed is the shell.</summary>
    public static string ToSetting(string key, string? customCommand) =>
        key == Custom ? (customCommand?.Trim() ?? "") : key;
}
