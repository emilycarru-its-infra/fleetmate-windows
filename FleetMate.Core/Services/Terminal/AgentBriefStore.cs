using System.Diagnostics;
using System.Text;

namespace FleetMate.Core.Services.Terminal;

/// <summary>One session's brief: the file to hand Claude Code, and the text to hand Codex.</summary>
public sealed record AgentSessionBrief(string BriefPath, string CodexInstructions);

/// <summary>
/// Keeps the brief on disk in FleetMate's owner-only agent folder,
/// regenerated only when the installed CLI changes, and writes each session's
/// own copy with where it opened at the top.
/// </summary>
public sealed class AgentBriefStore
{
    /// <summary>Runs a program and returns whether it exited 0 and what it printed.</summary>
    public delegate (bool Ok, string Output) Runner(string fileName, IReadOnlyList<string> arguments);

    /// <summary>Bump when the brief's layout changes, so existing caches regenerate.</summary>
    internal const int FormatVersion = 1;

    private readonly object _gate = new();

    public AgentBriefStore(string directory) => Directory = directory;

    /// <summary>%LOCALAPPDATA%\FleetMate\Agent: the brief, the session briefs and the context file.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FleetMate", "Agent");

    public string Directory { get; }
    public string BriefPath => Path.Combine(Directory, "agent-brief.md");
    private string StampPath => Path.Combine(Directory, "agent-brief.stamp");
    /// <summary>Folder holding each open session's own brief.</summary>
    public string SessionDirectory => Path.Combine(Directory, "sessions");

    /// <summary>Identifies one build of the CLI without running it.</summary>
    internal static string Stamp(string? cliPath)
    {
        if (cliPath == null || !File.Exists(cliPath)) return "none";
        var info = new FileInfo(cliPath);
        return $"{FormatVersion}|{cliPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    /// <summary>
    /// Make sure the brief matches the installed CLI, regenerating it if not.
    /// Blocking: a stat when current, two short CLI runs when stale.
    /// </summary>
    public string Refresh(string? cliPath, Runner? run = null)
    {
        lock (_gate)
        {
            var stamp = Stamp(cliPath);
            try
            {
                if (File.Exists(BriefPath) && File.Exists(StampPath) && File.ReadAllText(StampPath) == stamp)
                    return BriefPath;
            }
            catch (IOException) { }

            AgentBrief.HelpDump? dump = null;
            string? version = null;
            if (cliPath != null)
            {
                run ??= RunProcess;
                var help = run(cliPath, new[] { "--experimental-dump-help" });
                if (help.Ok)
                {
                    try { dump = AgentBrief.Decode(help.Output); }
                    catch (System.Text.Json.JsonException) { }
                }
                var v = run(cliPath, new[] { "--version" });
                if (v.Ok) version = v.Output.Trim();
            }
            var markdown = AgentBrief.Markdown(dump, cliPath, version);
            try
            {
                PrivateFile.EnsureDirectory(Directory);
                PrivateFile.Write(BriefPath, markdown);
                // Only remember a full reference, so a CLI that failed once is retried.
                if (dump != null) PrivateFile.Write(StampPath, stamp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return BriefPath;
        }
    }

    /// <summary>
    /// Write the brief for one session: the shared brief with
    /// <paramref name="whereabouts"/> at its top, owner-only. Falls back to
    /// the shared brief if writing fails.
    /// </summary>
    public AgentSessionBrief WriteSessionBrief(string id, AgentWhereabouts whereabouts, DateTimeOffset? openedAt = null)
    {
        string text;
        try { text = File.ReadAllText(BriefPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { text = AgentBrief.Markdown(null, null, null); }
        text = AgentBrief.Inserting(whereabouts.Markdown(openedAt ?? DateTimeOffset.UtcNow), text);
        var path = Path.Combine(SessionDirectory, id + ".md");
        try
        {
            PrivateFile.EnsureDirectory(Directory);
            PrivateFile.EnsureDirectory(SessionDirectory);
            PrivateFile.Write(path, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            path = BriefPath;
        }
        return new AgentSessionBrief(path, AgentBrief.CodexInstructions(text, path));
    }

    /// <summary>Remove one session's brief.</summary>
    public void RemoveSessionBrief(string id)
    {
        try { File.Delete(Path.Combine(SessionDirectory, id + ".md")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Remove every session brief, such as those a previous run left behind.</summary>
    public void ClearSessions()
    {
        try { if (System.IO.Directory.Exists(SessionDirectory)) System.IO.Directory.Delete(SessionDirectory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Run a program with no window and no input, for at most 30 seconds.</summary>
    public static (bool Ok, string Output) RunProcess(string fileName, IReadOnlyList<string> arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                CreateNoWindow = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in arguments) psi.ArgumentList.Add(a);
            using var process = Process.Start(psi)!;
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (false, "");
            }
            return (process.ExitCode == 0, stdout.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return (false, "");
        }
    }
}
