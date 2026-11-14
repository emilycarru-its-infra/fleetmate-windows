namespace FleetMate.Core.Services.Repos;

/// <summary>
/// One line for a repository row when git status fails. Git's own message can
/// run to a dozen lines (dubious ownership prints the owners and a config
/// command), which buries the sidebar; the row says what is wrong in a few
/// words and the full text stays available to read and copy.
/// </summary>
public static class RepoStatusError
{
    private const int MaxLength = 80;

    public static string Summarize(string? error)
    {
        var text = error?.Trim() ?? "";
        if (text.Length == 0) return "Git status failed";
        if (Has(text, "dubious ownership")) return "Owned by another account. Git won't read it.";
        if (Has(text, "not a git repository")) return "Not a git repository";
        if (Has(text, "index.lock")) return "Locked by another git process";
        if (Has(text, "permission denied") || Has(text, "access is denied")) return "Permission denied";

        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? text;
        foreach (var prefix in new[] { "fatal:", "error:", "warning:" })
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) line = line[prefix.Length..].Trim();
        if (line.Length > 0) line = char.ToUpperInvariant(line[0]) + line[1..];
        return line.Length > MaxLength ? line[..(MaxLength - 1)].TrimEnd() + "…" : line;
    }

    private static bool Has(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
