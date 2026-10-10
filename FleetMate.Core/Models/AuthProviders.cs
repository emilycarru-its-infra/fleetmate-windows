using System.Diagnostics;
using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Services;

namespace FleetMate.Core.Models;

/// <summary>
/// Where a connected system's credential comes from. Settings ›
/// Authentication shows one card per provider, listing the systems that
/// depend on it, so a single expired sign-in reads as one problem rather than
/// several. Declaration order is display order.
/// </summary>
public enum CredentialProvider
{
    /// <summary>The operator's Windows account, used silently through the broker. No password is stored.</summary>
    SingleSignOn,
    /// <summary>The operator's own az sign-in: the trust anchor for every elevation session.</summary>
    AzureCli,
    /// <summary>The gh CLI's sign-in.</summary>
    GitHubCli,
    /// <summary>Keys and tokens saved in FleetMate.</summary>
    StoredCredential,
}

public static class CredentialProviderExtensions
{
    public static string Title(this CredentialProvider provider) => provider switch
    {
        CredentialProvider.SingleSignOn => "Single sign-on",
        CredentialProvider.AzureCli => "Azure CLI sign-in",
        CredentialProvider.GitHubCli => "GitHub CLI sign-in",
        _ => "Stored credentials",
    };

    public static string Summary(this CredentialProvider provider) => provider switch
    {
        CredentialProvider.SingleSignOn => "Your Windows account, used silently. No password is stored and actions show as you.",
        CredentialProvider.AzureCli => "Your command-line cloud sign-in. Systems below run through elevation sessions that start from it.",
        CredentialProvider.GitHubCli => "Your command-line code-hosting sign-in, managed by the gh CLI.",
        _ => "Keys saved for FleetMate. Actions run as that credential, not as you.",
    };

    /// <summary>A Segoe Fluent Icons code point.</summary>
    public static string Glyph(this CredentialProvider provider) => provider switch
    {
        CredentialProvider.SingleSignOn => "\uE77B",
        CredentialProvider.AzureCli => "\uE756",
        CredentialProvider.GitHubCli => "\uE943",
        _ => "\uE8D7",
    };
}

/// <summary>
/// How each system authenticates, for grouping and per-row detail. Pure
/// functions of the configuration and the Graph transport, so the grouping
/// is unit-testable.
/// </summary>
public static class AuthProviderGrouping
{
    /// <summary>
    /// Whether Graph calls run in elevation sessions: the default, unless
    /// FLEETMATE_GRAPH_TRANSPORT=direct sends them with the operator's own
    /// brokered token.
    /// </summary>
    public static bool GraphUsesElevation(string? transport = null) =>
        !string.Equals(transport ?? Environment.GetEnvironmentVariable("FLEETMATE_GRAPH_TRANSPORT"),
            "direct", StringComparison.OrdinalIgnoreCase);

    /// <summary>The provider a system's credential comes from.</summary>
    public static CredentialProvider Provider(AuthSystemId system, FleetMateConfig config, bool graphUsesElevation) => system switch
    {
        // Elevation sessions start from az; the direct transport is the broker.
        AuthSystemId.Intune or AuthSystemId.Graph or AuthSystemId.Entra =>
            graphUsesElevation ? CredentialProvider.AzureCli : CredentialProvider.SingleSignOn,
        AuthSystemId.DevOps or AuthSystemId.Tdx => CredentialProvider.SingleSignOn,
        // Without an audience Snipe-IT falls back to the legacy API key.
        AuthSystemId.Snipe => config.SnipeUsesOidc ? CredentialProvider.SingleSignOn : CredentialProvider.StoredCredential,
        AuthSystemId.GitHub => CredentialProvider.GitHubCli,
        _ => CredentialProvider.StoredCredential,
    };

    /// <summary>The elevation domain a system's calls run in, or null when it uses the operator's own token.</summary>
    public static GraphDomain? ElevationDomain(AuthSystemId system, bool graphUsesElevation)
    {
        if (!graphUsesElevation) return null;
        return system switch
        {
            AuthSystemId.Intune or AuthSystemId.Graph => GraphDomain.Devices,
            AuthSystemId.Entra => GraphDomain.Identity,
            _ => null,
        };
    }

    /// <summary>
    /// Group systems by provider, in provider order, each group in the order
    /// given. Providers with no systems are left out.
    /// </summary>
    public static IReadOnlyList<(CredentialProvider Provider, IReadOnlyList<AuthSystemId> Systems)> Group(
        IEnumerable<AuthSystemId> systems, FleetMateConfig config, bool graphUsesElevation)
    {
        var buckets = new Dictionary<CredentialProvider, List<AuthSystemId>>();
        foreach (var system in systems)
        {
            var provider = Provider(system, config, graphUsesElevation);
            if (!buckets.TryGetValue(provider, out var list)) buckets[provider] = list = new List<AuthSystemId>();
            list.Add(system);
        }
        return Enum.GetValues<CredentialProvider>()
            .Where(buckets.ContainsKey)
            .Select(p => (p, (IReadOnlyList<AuthSystemId>)buckets[p]))
            .ToList();
    }

    /// <summary>A short description of the method, for the system's row.</summary>
    public static string MethodDescription(AuthSystemId system, FleetMateConfig config, bool graphUsesElevation) => system switch
    {
        AuthSystemId.Intune or AuthSystemId.Graph or AuthSystemId.Entra =>
            graphUsesElevation ? "Elevation session" : "Your token (direct)",
        AuthSystemId.DevOps => "Your token",
        AuthSystemId.Snipe => config.SnipeUsesOidc ? "Brokered bearer" : "API key",
        AuthSystemId.Tdx => "Silent SSO",
        AuthSystemId.GitHub => "gh token",
        _ => "API token",
    };
}

/// <summary>Who a CLI is signed in as, read from <c>az account show</c> or <c>gh auth status</c>.</summary>
public sealed record CliAccount(string User, string Type = "user", string? TenantId = null, string? Subscription = null)
{
    public bool IsServicePrincipal => Type == "servicePrincipal";
}

/// <summary>
/// The shared az and gh account checks behind every card that depends on
/// either CLI, and the setup wizard's Development step.
/// </summary>
public static class CliAccountProbe
{
    /// <summary>Parse <c>az account show -o json</c>. Null when the output is not an account.</summary>
    public static CliAccount? ParseAzAccount(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object
                || !user.TryGetProperty("name", out var name) || name.GetString() is not { Length: > 0 } userName)
                return null;
            return new CliAccount(
                userName,
                user.TryGetProperty("type", out var type) ? type.GetString() ?? "user" : "user",
                root.TryGetProperty("tenantId", out var tenant) ? tenant.GetString() : null,
                root.TryGetProperty("name", out var sub) ? sub.GetString() : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Parse <c>gh auth status</c> (both streams). Null when not signed in.</summary>
    public static CliAccount? ParseGhStatus(string? output) =>
        AuthManager.ParseGitHubAccount(output ?? "") is { } user ? new CliAccount(user) : null;

    /// <summary>Who az is signed in as, or null.</summary>
    public static async Task<CliAccount?> AzAccountAsync()
    {
        // az is a .cmd on Windows, which Process.Start only finds through cmd.
        var (output, code) = await RunAsync("cmd.exe", "/d /c az account show -o json", stdoutOnly: true);
        return code == 0 ? ParseAzAccount(output) : null;
    }

    /// <summary>
    /// Who gh is signed in as, or null. gh reports on stderr and exits
    /// non-zero when logged out, so both streams are read.
    /// </summary>
    public static async Task<CliAccount?> GhAccountAsync()
    {
        var (output, _) = await RunAsync(AuthManager.ResolveGh(), "auth status --active", stdoutOnly: false);
        return ParseGhStatus(output);
    }

    private static async Task<(string Output, int Code)> RunAsync(string fileName, string arguments, bool stdoutOnly)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process == null) return ("", -1);
            // Both streams at once: draining one first deadlocks when the other fills.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdout, stderr);
            await process.WaitForExitAsync();
            return (stdoutOnly ? stdout.Result : stdout.Result + "\n" + stderr.Result, process.ExitCode);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[auth] {File} {Args} could not run", fileName, arguments);
            return ("", -1);
        }
    }
}

/// <summary>
/// The one status model every authentication row and group uses, so the same
/// condition always reads and colours the same way. Never red: nothing here
/// is an emergency, and orange appears only when the person has to act.
/// </summary>
public sealed record AuthDisplayStatus(AuthDisplayKind Kind, string? Text = null)
{
    public static readonly AuthDisplayStatus Valid = new(AuthDisplayKind.Valid);
    public static readonly AuthDisplayStatus NeedsSignIn = new(AuthDisplayKind.NeedsSignIn);
    public static readonly AuthDisplayStatus NotConfigured = new(AuthDisplayKind.NotConfigured);
    public static AuthDisplayStatus Checking(string? text = null) => new(AuthDisplayKind.Checking, text);
    public static AuthDisplayStatus Failed(string message) => new(AuthDisplayKind.Failed, message);

    /// <summary>
    /// Map a probe state. Configured is never final: before its first check
    /// it reads as checking, after one it means a sign-in is missing.
    /// </summary>
    public static AuthDisplayStatus From(AuthTokenState state, DateTime? lastChecked) => state.Kind switch
    {
        AuthStateKind.NotConfigured => NotConfigured,
        AuthStateKind.Configured => lastChecked == null ? Checking() : NeedsSignIn,
        AuthStateKind.Authenticating => Checking(),
        AuthStateKind.Valid => Valid,
        AuthStateKind.Expired => NeedsSignIn,
        AuthStateKind.ServicePrincipal => Failed(
            $"Signed in as the service principal {state.ServicePrincipalName}, so actions will not show as you."),
        _ => Failed(string.IsNullOrWhiteSpace(state.Message) ? "The check failed." : state.Message!),
    };

    public string Label => Kind switch
    {
        AuthDisplayKind.Checking => Text ?? "Checking…",
        AuthDisplayKind.Valid => "Valid",
        AuthDisplayKind.NeedsSignIn => "Needs sign-in",
        AuthDisplayKind.NotConfigured => "Not configured",
        _ => "Failed",
    };

    public AuthDisplayTone Tone => Kind switch
    {
        AuthDisplayKind.Checking => AuthDisplayTone.Neutral,
        AuthDisplayKind.Valid => AuthDisplayTone.Positive,
        AuthDisplayKind.NotConfigured => AuthDisplayTone.Inactive,
        _ => AuthDisplayTone.Attention,
    };

    public bool IsChecking => Kind == AuthDisplayKind.Checking;

    /// <summary>
    /// The pill for a group: valid only when every member is; "n of m need
    /// attention" when any must be acted on; otherwise still checking.
    /// </summary>
    public static (string Label, AuthDisplayTone Tone) Summary(IEnumerable<AuthDisplayStatus> members)
    {
        var active = members.Where(m => m.Kind != AuthDisplayKind.NotConfigured).ToList();
        if (active.Count == 0) return ("Not configured", AuthDisplayTone.Inactive);
        var attention = active.Count(m => m.Tone == AuthDisplayTone.Attention);
        if (attention > 0)
            return attention == 1 && active.Count == 1
                ? (active[0].Label, AuthDisplayTone.Attention)
                : ($"{attention} of {active.Count} need attention", AuthDisplayTone.Attention);
        if (active.Any(m => m.IsChecking)) return ("Checking…", AuthDisplayTone.Neutral);
        return active.Count == 1 ? ("Valid", AuthDisplayTone.Positive) : ("All valid", AuthDisplayTone.Positive);
    }
}

public enum AuthDisplayKind { Checking, Valid, NeedsSignIn, NotConfigured, Failed }

public enum AuthDisplayTone { Positive, Neutral, Attention, Inactive }
