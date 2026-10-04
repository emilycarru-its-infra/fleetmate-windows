using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FleetMate.Core.Knowledge;

/// <summary>
/// FleetMate's own copy of a repository's main branch, kept apart from any
/// clone the person works in. Their checkout may be on a branch, behind, or
/// missing; this one is always the remote's branch and nothing else: a
/// shallow, sparse clone that every sync resets to the remote exactly
/// (macOS parity).
/// </summary>
public sealed partial class RepoMirror
{
    public string Name { get; }
    public string RemoteUrl { get; }
    public string Branch { get; }
    /// <summary>Only these folders are checked out, when given.</summary>
    public IReadOnlyList<string> Paths { get; }
    public string LocalPath { get; }
    /// <summary>The only host the sign-in token may be sent to.</summary>
    public string? TokenHost { get; }

    public RepoMirror(string name, string remoteUrl, IReadOnlyList<string>? paths = null, string branch = "main",
        string? tokenHost = null, string? root = null)
    {
        Name = name;
        RemoteUrl = remoteUrl;
        Branch = branch;
        Paths = paths ?? Array.Empty<string>();
        TokenHost = tokenHost?.ToLowerInvariant();
        var baseDir = root ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FleetMate", "mirrors");
        LocalPath = System.IO.Path.Combine(baseDir, name);
    }

    /// <summary>Whether a usable copy exists, synced or not.</summary>
    public bool IsCloned => Directory.Exists(System.IO.Path.Combine(LocalPath, ".git"));

    /// <summary>
    /// The HTTPS host prefix the token is scoped to, or null when the remote
    /// is not HTTPS on the token's host: a misconfigured remote, a redirect
    /// or a submodule elsewhere never sees the token.
    /// </summary>
    public string? TokenScope =>
        TokenHost != null && Uri.TryCreate(RemoteUrl, UriKind.Absolute, out var url)
        && url.Scheme == Uri.UriSchemeHttps && url.Host.Equals(TokenHost, StringComparison.OrdinalIgnoreCase)
            ? $"https://{TokenHost}/"
            : null;

    /// <summary>
    /// The environment for one git call. The token rides in git's environment
    /// configuration for this call only: not on the command line, where it
    /// would show in the process list, and never in the repository's config.
    /// Prompts are off, since a desktop app has no terminal to answer on.
    /// </summary>
    public Dictionary<string, string> GitEnvironment(string? bearerToken)
    {
        var env = new Dictionary<string, string>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never",
        };
        if (!string.IsNullOrEmpty(bearerToken) && TokenScope is { } scope)
        {
            env["GIT_CONFIG_COUNT"] = "2";
            env["GIT_CONFIG_KEY_0"] = $"http.{scope}.extraHeader";
            env["GIT_CONFIG_VALUE_0"] = $"Authorization: Bearer {bearerToken}";
            // The token is the sign-in; keep credential helpers out of it.
            env["GIT_CONFIG_KEY_1"] = "credential.helper";
            env["GIT_CONFIG_VALUE_1"] = "";
        }
        return env;
    }

    /// <summary>Bring the copy to the remote's latest branch; returns the commit now checked out.</summary>
    public async Task<string> SyncAsync(string? bearerToken, CancellationToken ct = default)
    {
        var env = GitEnvironment(bearerToken);
        if (!IsCloned)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LocalPath)!);
            if (Directory.Exists(LocalPath)) Directory.Delete(LocalPath, recursive: true);
            var args = new List<string> { "clone", "--depth", "1", "--single-branch", "--branch", Branch };
            if (Paths.Count > 0) args.AddRange(new[] { "--filter=blob:none", "--sparse" });
            args.AddRange(new[] { RemoteUrl, LocalPath });
            await GitAsync(args, null, env, ct);
            if (Paths.Count > 0)
                await GitAsync(new[] { "sparse-checkout", "set" }.Concat(Paths).ToList(), LocalPath, env, ct);
        }
        else
        {
            await GitAsync(new List<string> { "fetch", "--depth", "1", "origin", Branch }, LocalPath, env, ct);
            await GitAsync(new List<string> { "reset", "--hard", "FETCH_HEAD" }, LocalPath, env, ct);
            await GitAsync(new List<string> { "clean", "-fdx" }, LocalPath, env, ct);
        }
        return await GitAsync(new List<string> { "rev-parse", "--short", "HEAD" }, LocalPath, env, ct);
    }

    /// <summary>Remove a bearer token from text before it is shown or logged.</summary>
    public static string Scrub(string text) => BearerPattern().Replace(text, "Bearer ***");

    private static async Task<string> GitAsync(IReadOnlyList<string> args, string? dir,
        Dictionary<string, string> env, CancellationToken ct)
    {
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (dir != null) { start.ArgumentList.Add("-C"); start.ArgumentList.Add(dir); }
        foreach (var a in args) start.ArgumentList.Add(a);
        foreach (var (k, v) in env) start.Environment[k] = v;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("git could not be started");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            // Never echo the arguments: with a token they can carry it.
            var command = args.FirstOrDefault(a => !a.StartsWith('-') && !a.Contains('=')) ?? "git";
            throw new InvalidOperationException($"git {command} failed: {Scrub((await stderr).Trim())}");
        }
        return (await stdout).Trim();
    }

    [GeneratedRegex(@"Bearer [^\s""']+")]
    private static partial Regex BearerPattern();
}
