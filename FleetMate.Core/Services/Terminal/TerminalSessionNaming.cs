using System.Text;
using System.Text.RegularExpressions;

namespace FleetMate.Core.Services.Terminal;

/// <summary>
/// A terminal title (OSC 0 or OSC 2) read as the name of the task in the
/// session. Claude Code and Codex title the terminal with their session's name
/// behind a status glyph: a Braille spinner frame while working, a star or dot
/// while idle. A shell's own title (<c>user@host: ~/src</c>, a path, the
/// program's bare name) names no task and is ignored, so the row keeps its label.
/// </summary>
public sealed record TerminalTitleTask(string? Task, bool? Working)
{
    public const int MaxLength = 120;

    /// <summary>Titles that are only the program's or the shell's own name.</summary>
    private static readonly HashSet<string> ProgramNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "claude", "claude code", "codex", "openai codex", "zsh", "-zsh", "bash", "-bash", "fish", "sh", "login",
        "terminal", "shell", "cmd", "cmd.exe", "command prompt", "powershell", "powershell.exe", "pwsh", "pwsh.exe",
        "windows powershell", "administrator: windows powershell", "administrator: command prompt", "wsl", "wsl.exe",
    };

    /// <summary>Marks agents draw ahead of the title when idle.</summary>
    private static readonly HashSet<string> IdleMarks = new(StringComparer.Ordinal)
        { "✳", "✻", "✽", "✶", "✢", "✦", "✧", "·", "•", "●", "○", "*" };

    private static readonly Regex Prompt = new(@"^[^\s@]+@[^\s:]+(:|\s|$)", RegexOptions.CultureInvariant);
    private static readonly Regex DrivePath = new(@"^[A-Za-z]:([\\/]|$)", RegexOptions.CultureInvariant);

    public static TerminalTitleTask Parse(string? raw)
    {
        var builder = new StringBuilder();
        foreach (var c in raw ?? "") if (!char.IsControl(c)) builder.Append(c);
        var text = builder.ToString().Trim();
        bool? working = null;
        while (text.Length > 0)
        {
            var first = char.ConvertFromUtf32(char.ConvertToUtf32(text, 0));
            var code = char.ConvertToUtf32(first, 0);
            if (code is >= 0x2800 and <= 0x28FF) working = true;
            else if (IdleMarks.Contains(first)) working ??= false;
            else break;
            text = text[first.Length..].TrimStart();
        }
        if (text.Length > MaxLength) text = text[..MaxLength];
        text = text.Trim();
        return new TerminalTitleTask(NamesTask(text) ? text : null, working);
    }

    private static bool NamesTask(string text)
    {
        if (text.Length == 0) return false;
        if (ProgramNames.Contains(text)) return false;
        if (text.StartsWith('/') || text.StartsWith('~') || text.StartsWith(@"\\", StringComparison.Ordinal) || DrivePath.IsMatch(text)) return false;
        // What a shell's prompt sets: user@host: path, user@host path.
        if (Prompt.IsMatch(text)) return false;
        return true;
    }
}

/// <summary>The automatic and manual names of a session, and which one shows.</summary>
public static class TerminalSessionName
{
    /// <summary>
    /// A name the person typed wins; then what the agent wrote to its session
    /// file, the task in the terminal title, the row's label (the repository's
    /// name), and last what it runs.
    /// </summary>
    public static string Resolve(string? custom, string? agent, string? task, string? label, string fallback)
    {
        foreach (var candidate in new[] { custom, agent, task, label })
            if (candidate?.Trim() is { Length: > 0 } value) return value;
        return fallback;
    }
}

/// <summary>
/// A folder as a short path for a session row: its components below the
/// nearest repository root (the registry's scan roots and clone root), joined
/// with " · ", so <c>~\src\Cloud\Vantage</c> under the root <c>~\src</c> reads
/// "Cloud · Vantage". Hidden components such as <c>.worktrees</c> are left out.
/// </summary>
public static class TerminalBreadcrumb
{
    public const string Separator = " · ";

    public static string Make(string path, IEnumerable<string> roots, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var full = Trimmed(TerminalWorkspaceStore.Expand(path, home));
        var expandedRoots = roots.Select(r => Trimmed(TerminalWorkspaceStore.Expand(r, home)))
            .Where(r => r.Length > 0 && !IsDriveRoot(r))
            .OrderByDescending(r => r.Length)
            .ToList();
        var trimmedHome = Trimmed(home);
        List<string> components;
        if (expandedRoots.FirstOrDefault(r => Same(full, r) || full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)) is { } root)
            components = Same(full, root) ? new() { LastComponent(root) } : Split(full[(root.Length + 1)..]);
        else if (trimmedHome.Length > 0 && Same(full, trimmedHome))
            return "~";
        else if (trimmedHome.Length > 0 && full.StartsWith(trimmedHome + "\\", StringComparison.OrdinalIgnoreCase))
            components = new List<string> { "~" }.Concat(Split(full[(trimmedHome.Length + 1)..])).ToList();
        else
            return full;
        var shown = components.Where(c => !c.StartsWith('.')).ToList();
        return shown.Count == 0 ? full : string.Join(Separator, shown);
    }

    private static string Trimmed(string path)
    {
        var p = path.Replace('/', '\\');
        return p.Length > 1 && p.EndsWith('\\') && !IsDriveRoot(p) ? p.TrimEnd('\\') : p;
    }

    private static bool IsDriveRoot(string path) => path.TrimEnd('\\').Length <= 2 && path.Length >= 2 && path[1] == ':';
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string LastComponent(string path) => path.Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path;
    private static List<string> Split(string path) => path.Split('\\', StringSplitOptions.RemoveEmptyEntries).ToList();
}

/// <summary>How an agent session picks its last conversation back up after a relaunch.</summary>
public static class AgentResume
{
    /// <summary>
    /// <paramref name="command"/> changed to resume the latest conversation in
    /// the folder it starts in, or null when it does not start Claude Code or
    /// Codex (a shell, a remote wrapper that keeps its own session, a subcommand).
    /// </summary>
    public static string? Command(string? command)
    {
        var words = (command ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return null;
        var program = Path.GetFileNameWithoutExtension(words[0].Replace('/', '\\').Split('\\').Last()).ToLowerInvariant();
        var rest = words.Skip(1).ToHashSet(StringComparer.Ordinal);
        switch (program)
        {
            case "claude":
                if (words.Length > 1 && AgentBrief.ClaudeNonSessionSubcommands.Contains(words[1])) return null;
                if (rest.Overlaps(new[] { "--continue", "-c", "--resume", "-r", "-p", "--print" })) return null;
                return command!.Trim() + " --continue";
            case "codex":
                if (words.Length > 1 && !words[1].StartsWith('-')) return null;
                return command!.Trim() + " resume --last";
            default:
                return null;
        }
    }
}
