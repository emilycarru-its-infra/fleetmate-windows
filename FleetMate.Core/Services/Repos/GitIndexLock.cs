using System.Diagnostics;

namespace FleetMate.Core.Services.Repos;

/// <summary>
/// A <c>.git\index.lock</c> left behind by a crashed or interrupted git
/// process is the most common reason staging or committing fails out of
/// nowhere. This finds the lock, says whether anything still holds it, and
/// removes it only when nothing does. (Ported from FleetMate for Mac, which
/// adapted MunkiStudio's IndexLockRecovery, Apache-2.0.)
/// </summary>
public static class GitIndexLock
{
    /// <summary>Whether a git error is an index-lock conflict.</summary>
    public static bool Matches(string message)
    {
        var lower = message.ToLowerInvariant();
        return lower.Contains("index.lock") || lower.Contains("another git process");
    }

    /// <summary>
    /// The lock file of the checkout at <paramref name="checkout"/>, resolving a
    /// linked worktree's <c>.git</c> file to its real git directory.
    /// </summary>
    public static string LockPath(string checkout)
    {
        var dotGit = Path.Combine(checkout, ".git");
        if (File.Exists(dotGit))
        {
            try
            {
                var line = File.ReadAllLines(dotGit).FirstOrDefault(l => l.StartsWith("gitdir:"));
                if (line != null)
                {
                    var gitDir = line["gitdir:".Length..].Trim();
                    if (!Path.IsPathRooted(gitDir)) gitDir = Path.Combine(checkout, gitDir);
                    return Path.GetFullPath(Path.Combine(gitDir, "index.lock"));
                }
            }
            catch (IOException) { }
        }
        return Path.Combine(dotGit, "index.lock");
    }

    /// <summary>
    /// What still holds the lock, or an empty string when nothing does and it
    /// is stale. Git keeps the lock file open while it works, so a lock that
    /// cannot be opened exclusively is still in use; running git processes are
    /// named so the person knows what to wait for.
    /// </summary>
    public static string Holders(string lockPath)
    {
        if (!File.Exists(lockPath)) return "";
        try
        {
            using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            return "";
        }
        catch (IOException)
        {
            var running = RunningGit();
            return running.Length > 0 ? running : "The lock file is open in another process.";
        }
        catch (UnauthorizedAccessException)
        {
            return "The lock file cannot be opened; it may be read-only or owned by another account.";
        }
    }

    private static string RunningGit()
    {
        try
        {
            return string.Join("\n", Process.GetProcessesByName("git").Select(p => $"git (process {p.Id})"));
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    /// <summary>Removes a stale lock. Refuses while anything still holds it.</summary>
    public static void RemoveStale(string lockPath)
    {
        var holders = Holders(lockPath);
        if (holders.Length > 0) throw RepoException.InvalidArgument($"Something still holds {lockPath}:\n{holders}");
        try
        {
            File.Delete(lockPath);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone with its folder: the retry can run.
        }
    }
}
