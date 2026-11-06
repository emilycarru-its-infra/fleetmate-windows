using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;

namespace FleetMate.Core.Services;

/// <summary>
/// The command line's last way to a token, the same one the Mac CLI uses: the operator's own
/// <c>az</c> session (<c>az account get-access-token --scope</c>). When that session needs the
/// operator again (expired MFA, a new scope, no login at all) and the command runs at a real
/// console, it runs <c>az login</c> for that scope in the console the operator is looking at,
/// then asks again once. Only the command line registers it (see
/// <see cref="EntraTokenSource.CommandLineFallback"/>); the desktop app never opens a sign-in.
/// </summary>
public sealed class AzCliTokenSource
{
    /// <summary>Exit code, standard output and standard error of one az run.</summary>
    public sealed record AzResult(int ExitCode, string StdOut, string StdErr);

    /// <summary>Runs az with arguments; <c>interactive</c> attaches it to this console.</summary>
    public delegate Task<AzResult> AzRunner(IReadOnlyList<string> arguments, bool interactive, CancellationToken ct);

    // Entra's "sign in again" answers: multi-factor required or expired, sign-in frequency,
    // a refresh token that is no longer accepted, and consent not yet given.
    private static readonly Regex SignInAgain = new(
        @"AADSTS(5007[689]|70043|50173|700082|65001)\b|interaction_required|Interactive authentication is needed|az login",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly AzRunner _run;
    private readonly Func<bool> _isInteractive;

    public AzCliTokenSource(AzRunner? runner = null, Func<bool>? isInteractive = null)
    {
        _run = runner ?? RunAzAsync;
        _isInteractive = isInteractive ?? ConsoleIsInteractive;
    }

    /// <summary>A token for <paramref name="scope"/> from az, signing in once at a console if az asks.</summary>
    public async Task<(string Token, DateTimeOffset ExpiresOn)> AcquireAsync(
        string scope, string tenant, CancellationToken ct = default)
    {
        var tenantArgs = string.IsNullOrWhiteSpace(tenant) || tenant == "organizations"
            ? Array.Empty<string>()
            : new[] { "--tenant", tenant };
        var get = new List<string> { "account", "get-access-token", "--scope", scope, "--output", "json" };
        get.AddRange(tenantArgs);

        var first = await _run(get, false, ct);
        if (first.ExitCode == 0) return Parse(first.StdOut);

        if (!NeedsSignIn(first.StdErr + first.StdOut))
            throw new InvalidOperationException($"az could not get a token: {FirstLine(first.StdErr)}");
        if (!_isInteractive())
            throw new InvalidOperationException(
                $"az needs you to sign in again; run az login --scope {scope}" +
                (tenantArgs.Length > 0 ? $" --tenant {tenant}" : ""));

        Log.Information("[entra] az needs a sign-in for {Scope}; running az login at the console", scope);
        Console.Error.WriteLine($"Signing in for {scope}. Finish the sign-in Windows or your browser shows.");
        var login = new List<string> { "login", "--scope", scope, "--output", "none" };
        login.AddRange(tenantArgs);
        var signedIn = await _run(login, true, ct);
        if (signedIn.ExitCode != 0)
            throw new InvalidOperationException("az login did not complete");

        var second = await _run(get, false, ct);
        if (second.ExitCode == 0) return Parse(second.StdOut);
        throw new InvalidOperationException($"az still could not get a token: {FirstLine(second.StdErr)}");
    }

    /// <summary>Whether az's error is one an interactive sign-in fixes.</summary>
    public static bool NeedsSignIn(string? error) => SignInAgain.IsMatch(error ?? string.Empty);

    internal static (string Token, DateTimeOffset ExpiresOn) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var token = root.TryGetProperty("accessToken", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("az returned no access token");

        var expires = DateTimeOffset.UtcNow.AddMinutes(50);
        if (root.TryGetProperty("expires_on", out var unix) && unix.ValueKind == JsonValueKind.Number)
            expires = DateTimeOffset.FromUnixTimeSeconds(unix.GetInt64());
        else if (root.TryGetProperty("expiresOn", out var local) && local.ValueKind == JsonValueKind.String
                 && DateTime.TryParse(local.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            expires = new DateTimeOffset(parsed);
        return (token, expires);
    }

    private static string FirstLine(string? text) =>
        (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "no detail";

    /// <summary>A person at a console: input and output not redirected, and not opted out.</summary>
    public static bool ConsoleIsInteractive() =>
        Environment.UserInteractive
        && !Console.IsInputRedirected
        && !Console.IsOutputRedirected
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLEETMATE_NONINTERACTIVE"))
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));

    private static async Task<AzResult> RunAzAsync(IReadOnlyList<string> arguments, bool interactive, CancellationToken ct)
    {
        var az = FindAz() ?? throw new InvalidOperationException("az is not installed or not on PATH");
        var psi = new ProcessStartInfo
        {
            FileName = az,
            UseShellExecute = false,
            // A sign-in runs in this console so the operator sees and answers it; a token
            // request is captured.
            RedirectStandardOutput = !interactive,
            RedirectStandardError = !interactive,
            CreateNoWindow = !interactive,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("az did not start");
        var stdout = interactive ? Task.FromResult(string.Empty) : process.StandardOutput.ReadToEndAsync(ct);
        var stderr = interactive ? Task.FromResult(string.Empty) : process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return new AzResult(process.ExitCode, await stdout, await stderr);
    }

    private static string? FindAz()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
        };
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            foreach (var name in new[] { "az.cmd", "az.exe", "az" })
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }
}
