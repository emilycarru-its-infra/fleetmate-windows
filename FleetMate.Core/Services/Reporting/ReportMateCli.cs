using System.Diagnostics;
using Serilog;

namespace FleetMate.Core.Services.Reporting;

/// <summary>
/// The <c>reportmate</c> admin CLI, when it is installed on this machine.
///
/// The CLI is the reference client for the ReportMate API: it tracks every
/// route the API has and prints the API's JSON unchanged. When it is present
/// FleetMate routes its ReportMate reads through it, so one binary owns the
/// API contract and a route change reaches FleetMate by updating the CLI
/// rather than by shipping a new FleetMate. When it is absent, or cannot be
/// launched, <see cref="ReportMateService"/> falls back to its own HTTP client.
///
/// A non-zero exit from a CLI that <em>did</em> launch is an API answer (a
/// 404, a 403 for a missing scope, a 5xx) and is surfaced, not retried over
/// HTTP: the HTTP path would only reproduce the same answer.
/// </summary>
public sealed class ReportMateCli
{
    /// <summary>Where a fleet install puts the binary, checked before <c>PATH</c>.</summary>
    private static readonly string[] CandidateDirectories =
    {
        @"C:\Program Files\ReportMateCLI",
        @"C:\Program Files\sbin",
        @"C:\Program Files\ReportMate",
    };

    public string Path { get; }

    /// <summary>Runs the process; replaceable so tests can script the CLI.</summary>
    private readonly Func<string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>, CancellationToken, Task<CliOutput>> _launch;

    public ReportMateCli(string path) : this(path, LaunchProcessAsync) { }

    public ReportMateCli(string path,
        Func<string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>, CancellationToken, Task<CliOutput>> launch)
    {
        Path = path;
        _launch = launch;
    }

    /// <summary>
    /// The installed CLI, or null when no <c>reportmate.exe</c> exists.
    ///
    /// <c>REPORTMATE_CLI</c> in the environment pins a specific binary, which
    /// is how a test points the service at a stub and how an operator tries a
    /// development build. Set it to an empty string to disable the CLI path.
    /// </summary>
    public static ReportMateCli? Locate(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var pinned = environment("REPORTMATE_CLI");
        if (pinned != null)
        {
            var trimmed = pinned.Trim();
            return trimmed.Length > 0 && File.Exists(trimmed) ? new ReportMateCli(trimmed) : null;
        }
        foreach (var directory in CandidateDirectories)
        {
            var candidate = System.IO.Path.Combine(directory, "reportmate.exe");
            if (File.Exists(candidate)) return new ReportMateCli(candidate);
        }
        var path = environment("PATH") ?? string.Empty;
        foreach (var directory in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "reportmate.exe", "reportmate" })
            {
                string candidate;
                try { candidate = System.IO.Path.Combine(directory.Trim('"'), name); }
                catch (ArgumentException) { continue; }
                if (File.Exists(candidate)) return new ReportMateCli(candidate);
            }
        }
        return null;
    }

    /// <summary>What a run of the binary produced.</summary>
    public sealed record CliOutput(bool Launched, int ExitCode, string Stdout, string Stderr)
    {
        public bool Succeeded => Launched && ExitCode == 0;
        /// <summary>The one API answer the service treats as "no such thing".</summary>
        public bool IsNotFound => Launched && ExitCode != 0 && Stderr.Contains("-> 404", StringComparison.Ordinal);
    }

    /// <summary>The CLI ran and the API (or the CLI itself) refused the request.</summary>
    public sealed class CliException : Exception
    {
        public int ExitCode { get; }
        public CliException(int exitCode, string stderr) : base($"reportmate exited {exitCode}: {stderr}") => ExitCode = exitCode;
    }

    /// <summary>
    /// Runs <c>reportmate &lt;arguments&gt; --output json</c>.
    ///
    /// <paramref name="credentials"/> carries <c>REPORTMATE_API_URL</c> and one
    /// credential variable; nothing else from FleetMate's environment is
    /// forwarded, so a stray <c>REPORTMATE_*</c> variable in the operator's
    /// shell cannot redirect the call.
    /// </summary>
    public Task<CliOutput> RunAsync(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> credentials,
        CancellationToken cancellationToken = default)
    {
        var full = new List<string>(arguments) { "--output", "json" };
        return _launch(Path, full, credentials, cancellationToken);
    }

    private static async Task<CliOutput> LaunchProcessAsync(string path, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> credentials, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        // Start from an empty ReportMate environment so only the service's own
        // credential reaches the CLI; leave the rest (SystemRoot, TEMP) alone.
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("REPORTMATE_", StringComparison.OrdinalIgnoreCase)).ToList())
            info.Environment.Remove(key);
        foreach (var (key, value) in credentials) info.Environment[key] = value;

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Exception ex)
        {
            return new CliOutput(false, -1, string.Empty, $"could not launch {path}: {ex.Message}");
        }
        if (process == null) return new CliOutput(false, -1, string.Empty, $"could not launch {path}");

        using (process)
        {
            // Drain both streams concurrently: reading them in sequence deadlocks
            // once the child writes more than a pipe buffer to the other one.
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new CliOutput(true, process.ExitCode, await stdout, (await stderr).Trim());
        }
    }
}
