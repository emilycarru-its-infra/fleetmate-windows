using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FleetMate.Core.Services;

/// <summary>
/// Sign in to the command-line tools FleetMate rides on, from Settings. `az
/// login` is the trust anchor for Azure DevOps and every elevation session;
/// `gh auth login` backs the GitHub provider. FleetMate never shows a sign-in
/// window of its own: az opens the browser itself, and gh runs in a console
/// because it prompts on the terminal. The names match the macOS client.
/// </summary>
public static partial class CliSignIn
{
    public sealed record Outcome(bool Succeeded, string Message);

    public const string GhLoginCommand = "gh auth login --web";

    /// <summary>
    /// Arguments for `az login`, scoped to <paramref name="tenantId"/> when it
    /// looks like a tenant id or domain. Anything else is dropped rather than
    /// passed to the shell.
    /// </summary>
    public static string AzLoginArguments(string? tenantId)
    {
        var tenant = tenantId?.Trim();
        return !string.IsNullOrEmpty(tenant) && TenantPattern().IsMatch(tenant)
            ? $"login --tenant {tenant}"
            : "login";
    }

    public static string AzLoginCommandDescription(string? tenantId) => "az " + AzLoginArguments(tenantId);

    /// <summary>
    /// Run `az login`. It opens the browser itself and returns when the
    /// sign-in finishes or is cancelled; no console is shown.
    /// </summary>
    public static async Task<Outcome> AzLoginAsync(string? tenantId, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            // az is a .cmd on Windows, which Process.Start only finds through cmd.
            FileName = "cmd.exe",
            Arguments = "/d /c az " + AzLoginArguments(tenantId) + " -o none",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            using var process = Process.Start(psi);
            if (process == null) return new Outcome(false, "Couldn't start az. Is the Azure CLI installed?");
            var stderr = process.StandardError.ReadToEndAsync(ct);
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(stderr, stdout);
            if (process.ExitCode == 0) return new Outcome(true, "Signed in with az.");
            var detail = FirstLine(stderr.Result);
            return new Outcome(false, string.IsNullOrEmpty(detail) ? $"az login failed (exit {process.ExitCode})" : detail);
        }
        catch (OperationCanceledException)
        {
            return new Outcome(false, "az login was cancelled.");
        }
        catch (Exception ex)
        {
            return new Outcome(false, $"Couldn't run az login: {ex.Message}");
        }
    }

    /// <summary>
    /// Start `gh auth login` in a console window. gh prompts on the terminal
    /// even with every flag given, so it cannot run hidden; the caller watches
    /// for the session to appear instead of waiting on the process.
    /// </summary>
    public static Outcome GhLoginInConsole()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/k " + GhLoginCommand,
                UseShellExecute = true,
            });
            return new Outcome(true, "Finish signing in to GitHub in the console window.");
        }
        catch (Exception ex)
        {
            return new Outcome(false, $"Couldn't open a console. Run `{GhLoginCommand}` yourself. ({ex.Message})");
        }
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => !l.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase)) ?? "";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]{0,252}$")]
    private static partial Regex TenantPattern();
}
