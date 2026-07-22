using System.Diagnostics;
using Serilog;

namespace FleetMate.Core.Services;

/// <summary>
/// Mints short-lived Entra access tokens for a target API audience off the
/// operator's own <c>az</c> session — the SSO/OIDC model applied to resource APIs
/// (Snipe-IT, ReportMate, …). The token is the operator's delegated identity,
/// authorized server-side, so no shared secret leaves the machine. Port of the
/// macOS <c>AzTokenSource</c> (secretless / "OIDC-default" direction).
/// The command runner is injectable so the caching logic is unit-testable.
/// </summary>
public sealed class AzTokenSource
{
    /// <summary>Runs an <c>az</c> invocation and returns (exitCode, stdout, stderr).</summary>
    public delegate Task<(int Code, string Out, string Err)> CommandRunner(IReadOnlyList<string> args, CancellationToken ct);

    private readonly CommandRunner _run;
    private readonly Dictionary<string, (string Token, DateTime Expiry)> _cache = new();
    private readonly object _lock = new();

    public AzTokenSource(CommandRunner? runner = null) => _run = runner ?? DefaultRunAzAsync;

    /// <summary>
    /// A delegated access token for <paramref name="resource"/> (an app/client id GUID
    /// or an <c>api://…</c> identifier URI). Cached until shortly before expiry.
    /// Returns null if az is unavailable or not signed in.
    /// </summary>
    public async Task<string?> GetTokenAsync(string resource, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(resource)) return null;

        lock (_lock)
        {
            if (_cache.TryGetValue(resource, out var c) && DateTime.UtcNow < c.Expiry)
                return c.Token;
        }

        var (code, @out, err) = await _run(
            new[] { "account", "get-access-token", "--resource", resource, "--query", "accessToken", "-o", "tsv" }, ct);

        if (code != 0)
        {
            Log.Warning("AzTokenSource: az failed for {Resource}: {Error}", resource, string.IsNullOrEmpty(err) ? @out : err);
            return null;
        }

        var token = @out.Trim();
        if (string.IsNullOrEmpty(token))
        {
            Log.Warning("AzTokenSource: az returned an empty token for {Resource}", resource);
            return null;
        }

        // az tokens live ~60-75 min; refresh a little early.
        lock (_lock) { _cache[resource] = (token, DateTime.UtcNow.AddMinutes(50)); }
        return token;
    }

    /// <summary>Drop all cached tokens (e.g. on sign-out).</summary>
    public void Clear()
    {
        lock (_lock) { _cache.Clear(); }
    }

    private static async Task<(int, string, string)> DefaultRunAzAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = LocateAz(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (-1, "", "could not start az");
            var o = await p.StandardOutput.ReadToEndAsync(ct);
            var e = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return (p.ExitCode, o, e);
        }
        catch (Exception ex)
        {
            return (-1, "", $"could not launch az: {ex.Message}");
        }
    }

    /// <summary>Find the Azure CLI on Windows (az.cmd), falling back to PATH.</summary>
    private static string LocateAz()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
            @"C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd",
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            var azCmd = Path.Combine(dir, "az.cmd");
            if (File.Exists(azCmd)) return azCmd;
            var azExe = Path.Combine(dir, "az.exe");
            if (File.Exists(azExe)) return azExe;
        }
        return "az.cmd";
    }
}
