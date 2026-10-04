namespace FleetMate.Core.Config;

/// <summary>
/// The agent terminal panel's settings. The names match the macOS client:
/// AgentCommand, AgentAutoStart, Repos (the operator's own list) and
/// RepoDefaults (the managed default list from policy).
/// </summary>
public sealed class TerminalSettings
{
    /// <summary>"shell", a preset (claude, codex, claude-remote, codex-remote) or a custom binary and arguments.</summary>
    public string AgentCommand { get; set; } = "shell";

    /// <summary>Open an AgentCommand session when the app starts and whenever a new terminal tab opens.</summary>
    public bool AgentAutoStart { get; set; }

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
}
