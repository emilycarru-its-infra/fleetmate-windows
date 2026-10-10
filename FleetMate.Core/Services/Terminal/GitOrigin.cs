namespace FleetMate.Core.Services.Terminal;

/// <summary>
/// A checkout's origin URL, read from its git config without running git.
/// Returned with any credentials removed.
/// </summary>
public static class GitOrigin
{
    public static string? Read(string checkout)
    {
        try
        {
            var config = ConfigPath(checkout);
            return config == null ? null : Parse(File.ReadAllLines(config));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>The url of [remote "origin"] in a git config's lines, credentials removed.</summary>
    public static string? Parse(IEnumerable<string> lines)
    {
        var inOrigin = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inOrigin = line.Replace(" ", "").Equals("[remote\"origin\"]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inOrigin) continue;
            var eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals("url", StringComparison.OrdinalIgnoreCase))
                return AgentWhereabouts.RedactRemote(line[(eq + 1)..].Trim());
        }
        return null;
    }

    /// <summary>The config file for a checkout: .git\config, or the main repository's for a worktree.</summary>
    private static string? ConfigPath(string checkout)
    {
        var dotGit = Path.Combine(checkout, ".git");
        if (Directory.Exists(dotGit)) return File.Exists(Path.Combine(dotGit, "config")) ? Path.Combine(dotGit, "config") : null;
        if (!File.Exists(dotGit)) return null;
        var pointer = File.ReadAllText(dotGit).Trim();
        if (!pointer.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) return null;
        var gitDir = Path.GetFullPath(Path.Combine(checkout, pointer[7..].Trim()));
        var common = Path.Combine(gitDir, "commondir");
        if (File.Exists(common)) gitDir = Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(common).Trim()));
        var config = Path.Combine(gitDir, "config");
        return File.Exists(config) ? config : null;
    }
}
