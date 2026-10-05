using System.Text;

namespace FleetMate.Core.Services.Terminal;

/// <summary>A program to start in a terminal session: its file and arguments.</summary>
public sealed record TerminalCommand(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>
    /// The single command line CreateProcess takes. A .cmd or .bat file (an
    /// npm-installed CLI is one) runs through cmd.exe, since CreateProcess
    /// starts only executables.
    /// </summary>
    public string CommandLine
    {
        get
        {
            var ext = Path.GetExtension(FileName);
            if (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
                return "cmd.exe /d /c " + CommandLineBuilder.Build(FileName, Arguments);
            return CommandLineBuilder.Build(FileName, Arguments);
        }
    }
}

/// <summary>
/// What the AgentCommand setting means. "shell" is the default; claude,
/// codex and their -remote variants are presets; anything else is a custom
/// binary followed by its arguments, quoted as on a command line.
/// </summary>
public static class AgentCommands
{
    public const string Shell = "shell";

    /// <summary>
    /// The presets, as file and arguments. The -remote variants pass --remote
    /// to the same CLI; this table is the one place to change if those CLIs
    /// spell it differently.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Presets =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = new[] { "claude" },
            ["codex"] = new[] { "codex" },
            ["claude-remote"] = new[] { "claude", "--remote" },
            ["codex-remote"] = new[] { "codex", "--remote" },
        };

    /// <summary>The choices Settings offers; a custom command can be typed instead.</summary>
    public static IReadOnlyList<string> Choices { get; } = new[] { Shell }.Concat(Presets.Keys).ToList();

    /// <summary>
    /// Resolve a setting value. <paramref name="findOnPath"/> returns the full
    /// path of an executable on PATH, or null.
    /// </summary>
    public static TerminalCommand Resolve(string? agentCommand, Func<string, string?> findOnPath)
    {
        var value = agentCommand?.Trim();
        if (string.IsNullOrEmpty(value) || value.Equals(Shell, StringComparison.OrdinalIgnoreCase))
            return DefaultShell(findOnPath);

        if (Presets.TryGetValue(value, out var preset))
            return new TerminalCommand(findOnPath(preset[0]) ?? preset[0], preset.Skip(1).ToList());

        var parts = CommandLineBuilder.Split(value);
        if (parts.Count == 0) return DefaultShell(findOnPath);
        return new TerminalCommand(findOnPath(parts[0]) ?? parts[0], parts.Skip(1).ToList());
    }

    /// <summary>PowerShell 7 when it is installed, Windows PowerShell otherwise.</summary>
    public static TerminalCommand DefaultShell(Func<string, string?> findOnPath) =>
        new(findOnPath("pwsh.exe") ?? findOnPath("powershell.exe") ?? "powershell.exe", new[] { "-NoLogo" });

    /// <summary>Find an executable on PATH, trying PATHEXT extensions when none is given.</summary>
    public static string? FindOnPath(string name)
    {
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
        var extensions = Path.HasExtension(name)
            ? new[] { "" }
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';');
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir.Trim(), name + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}

/// <summary>Builds and splits Windows command lines by the CommandLineToArgvW rules.</summary>
public static class CommandLineBuilder
{
    public static string Build(string fileName, IEnumerable<string> arguments) =>
        string.Join(" ", new[] { fileName }.Concat(arguments).Select(Quote));

    /// <summary>Quote one argument so the C runtime reads it back unchanged.</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;

        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            // Backslashes before a quote are doubled, and the quote escaped.
            sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        // Trailing backslashes are doubled so they don't escape the closing quote.
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>Split a typed command into its parts, honouring double quotes.</summary>
    public static List<string> Split(string commandLine)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var c in commandLine)
        {
            if (c == '"') { inQuotes = !inQuotes; hasToken = true; continue; }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasToken) { parts.Add(current.ToString()); current.Clear(); hasToken = false; }
                continue;
            }
            current.Append(c);
            hasToken = true;
        }
        if (hasToken) parts.Add(current.ToString());
        return parts;
    }
}
