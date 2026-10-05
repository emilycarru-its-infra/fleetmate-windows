using System.Collections;
using System.Text;

namespace FleetMate.Core.Services.Terminal;

/// <summary>
/// The environment every terminal session starts with: the app's own, plus
/// FLEETMATE_CONTEXT pointing at the live context file, and the fleetmate
/// CLI's folder first on PATH so an agent can call it.
/// </summary>
public static class TerminalEnvironment
{
    public const string ContextVariable = "FLEETMATE_CONTEXT";

    public static Dictionary<string, string> Build(IDictionary baseEnvironment, string contextPath, string? cliDirectory)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in baseEnvironment)
            if (entry.Key is string key && entry.Value is string value) env[key] = value;

        env[ContextVariable] = contextPath;
        if (!string.IsNullOrWhiteSpace(cliDirectory))
        {
            var path = env.TryGetValue("PATH", out var existing) ? existing : "";
            var already = path.Split(Path.PathSeparator)
                .Any(p => string.Equals(p.TrimEnd('\\'), cliDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (!already) env["PATH"] = cliDirectory + Path.PathSeparator + path;
        }
        return env;
    }

    /// <summary>
    /// The folder holding the fleetmate CLI: the installed one when present,
    /// else a CLI built beside this app, else null.
    /// </summary>
    public static string? FindCliDirectory(string appDirectory)
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FleetMate");
        foreach (var dir in new[] { appDirectory, installed })
            if (File.Exists(Path.Combine(dir, "fleetmate.exe"))) return dir;
        return null;
    }

    /// <summary>CreateProcess's Unicode environment block: sorted "name=value" entries, each NUL-ended, then a final NUL.</summary>
    public static string ToEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(key).Append('=').Append(value).Append('\0');
        return sb.Append('\0').ToString();
    }
}
