using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Models;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Services.Tickets;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Reporting;
using Serilog;

namespace FleetMate.GUI;

/// <summary>
/// Centralized manager that tracks authentication state for every configured system.
/// Implements INotifyPropertyChanged for WPF data binding.
/// </summary>
public class AuthManager : INotifyPropertyChanged
{
    private readonly FleetMateConfig _config;
    private Dictionary<AuthSystemId, AuthSystemStatus> _systems = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public Dictionary<AuthSystemId, AuthSystemStatus> Systems
    {
        get => _systems;
        private set { _systems = value; OnPropertyChanged(); }
    }

    public AuthManager(FleetMateConfig config)
    {
        _config = config;
        BootstrapFromConfig();
    }

    // MARK: - Bootstrap

    public void BootstrapFromConfig()
    {
        var systems = new Dictionary<AuthSystemId, AuthSystemStatus>();

        // Devices — Graph / Intune
        if (_config.Graph != null && !string.IsNullOrEmpty(_config.Graph.TenantId))
        {
            systems[AuthSystemId.Intune] = new AuthSystemStatus { SystemId = AuthSystemId.Intune, State = AuthTokenState.Configured() };
            systems[AuthSystemId.Graph] = new AuthSystemStatus { SystemId = AuthSystemId.Graph, State = AuthTokenState.Configured() };
        }

        // Assets — Snipe-IT. A URL is enough: auth is the operator's Entra
        // session, so gating this on an API key hid a working SSO Snipe from the
        // panel entirely.
        if (!string.IsNullOrEmpty(_config.SnipeUrl))
        {
            systems[AuthSystemId.Snipe] = new AuthSystemStatus { SystemId = AuthSystemId.Snipe, State = AuthTokenState.Configured() };
        }

        // Tickets — TDX
        if (_config.Tdx != null && !string.IsNullOrEmpty(_config.Tdx.BaseUrl))
        {
            systems[AuthSystemId.Tdx] = new AuthSystemStatus { SystemId = AuthSystemId.Tdx, State = AuthTokenState.Configured() };
        }

        // Projects — DevOps and GitHub are always listed, even before they are
        // configured. Gating them on their own config made the panel unusable:
        // the Settings ▸ Projects toggle sends you here to enter the DevOps
        // organization, but with no row to edit there was nothing to fill in and
        // the toggle silently did nothing. An unconfigured row shows as
        // NotConfigured with its edit affordance, which is the way in.
        var devOpsConfigured = _config.AzureDevOps != null && !string.IsNullOrEmpty(_config.AzureDevOps.Organization);
        systems[AuthSystemId.DevOps] = new AuthSystemStatus
        {
            SystemId = AuthSystemId.DevOps,
            State = devOpsConfigured ? AuthTokenState.Configured() : AuthTokenState.NotConfigured()
        };

        // GitHub authenticates through the `gh` CLI rather than anything stored
        // here, so `Enabled` says nothing about whether it works — ProbeGitHubAsync
        // resolves the real state. Listing it unconditionally is what lets the
        // panel report a GitHub session that is already live.
        systems[AuthSystemId.GitHub] = new AuthSystemStatus { SystemId = AuthSystemId.GitHub, State = AuthTokenState.Configured() };

        // Projects — Gitea
        if (_config.Tasks?.Providers?.Gitea is { Enabled: true })
        {
            systems[AuthSystemId.Gitea] = new AuthSystemStatus { SystemId = AuthSystemId.Gitea, State = AuthTokenState.Configured() };
        }

        // Identity — Entra (same Graph credentials but for groups)
        if (_config.Graph != null && !string.IsNullOrEmpty(_config.Graph.TenantId))
        {
            systems[AuthSystemId.Entra] = new AuthSystemStatus { SystemId = AuthSystemId.Entra, State = AuthTokenState.Configured() };
        }

        Systems = systems;
        OnPropertyChanged(nameof(ConfiguredSystems));
        OnPropertyChanged(nameof(HasServicePrincipalWarning));
    }

    // MARK: - State Updates

    public void Update(AuthSystemId id, AuthTokenState state)
    {
        if (!_systems.ContainsKey(id)) return;
        _systems[id].State = state;
        _systems[id].LastChecked = DateTime.Now;
        if (state.Kind == AuthStateKind.Valid)
            _systems[id].User = state.User;

        OnPropertyChanged(nameof(Systems));
        OnPropertyChanged(nameof(HasServicePrincipalWarning));
    }

    /// <summary>
    /// Report that an <em>optional</em> auth path failed — a silent SSO attempt,
    /// say — without contradicting a system that is already authenticated by some
    /// other means.
    ///
    /// These systems have more than one way in: Snipe-IT and ReportMate ride an
    /// Entra bearer, and TeamDynamix a service-account JWT. The cookie/silent-SSO
    /// attempt runs anyway and, on failure, used to overwrite a perfectly good
    /// Valid with "Silent SSO failed" — the panel claiming TDX was broken while
    /// the row underneath said "signed in as Service Account" in green.
    /// </summary>
    public void ReportOptionalAuthFailure(AuthSystemId id, string message)
    {
        if (!_systems.TryGetValue(id, out var status)) return;

        if (status.State.IsHealthy)
        {
            Log.Information("[auth] {System}: optional SSO path failed ({Message}) — " +
                            "keeping the working auth state", id, message);
            return;
        }

        Update(id, AuthTokenState.Failed(message));
    }

    public void SignOut()
    {
        foreach (var id in _systems.Keys.ToList())
            Update(id, AuthTokenState.Configured());

        // Drop brokered tokens too, or "sign out" leaves the next call silently
        // succeeding on a cached credential.
        EntraTokenSource.Shared?.Invalidate();
    }

    // MARK: - Queries

    public IReadOnlyList<AuthSystemStatus> ConfiguredSystems =>
        Enum.GetValues<AuthSystemId>()
            .Where(id => _systems.ContainsKey(id))
            .Select(id => _systems[id])
            .ToList();

    public IReadOnlyList<AuthSystemStatus> SystemsForCategory(AuthCategory category) =>
        ConfiguredSystems.Where(s => s.SystemId.Category() == category).ToList();

    public AuthTokenState CategoryHealth(AuthCategory category)
    {
        var items = SystemsForCategory(category);
        if (items.Count == 0) return AuthTokenState.NotConfigured();
        if (items.All(s => s.State.IsHealthy)) return AuthTokenState.Valid();
        if (items.Any(s => s.State.Kind == AuthStateKind.Failed)) return AuthTokenState.Failed("");
        if (items.Any(s => s.State.Kind == AuthStateKind.ServicePrincipal)) return AuthTokenState.SP("");
        return AuthTokenState.Configured();
    }

    public bool HasServicePrincipalWarning =>
        _systems.Values.Any(s => s.State.Kind == AuthStateKind.ServicePrincipal);

    // MARK: - Probe All

    public async Task ProbeAllAsync(
        GraphService? graphService,
        TdxService? tdxService,
        SnipeService? snipeService,
        AzureDevOpsService? devOpsService)
    {
        var tasks = new List<Task>();

        // Each system runs the same probe a Re-check runs, so the two can never
        // disagree about what "healthy" means for that card.

        if (_systems.ContainsKey(AuthSystemId.Graph) && graphService != null)
            tasks.Add(ProbeGraphAsync(graphService));

        if (_systems.ContainsKey(AuthSystemId.Entra) && graphService != null)
            tasks.Add(ProbeEntraAsync(graphService));

        if (_systems.ContainsKey(AuthSystemId.Snipe) && snipeService != null)
            tasks.Add(ProbeSnipeAsync(snipeService));

        if (_systems.ContainsKey(AuthSystemId.Tdx) && tdxService != null)
            tasks.Add(ProbeTdxAsync(tdxService));

        // DevOps (az CLI on Windows)
        if (_systems.ContainsKey(AuthSystemId.DevOps))
        {
            Update(AuthSystemId.DevOps, AuthTokenState.Authenticating());
            tasks.Add(ProbeDevOpsAsync(devOpsService));
        }

        // GitHub (gh CLI)
        if (_systems.ContainsKey(AuthSystemId.GitHub))
        {
            Update(AuthSystemId.GitHub, AuthTokenState.Authenticating());
            tasks.Add(ProbeGitHubAsync());
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Re-probe one system.
    ///
    /// Sign-ins finish outside the app — in a browser, or a terminal running
    /// `gh auth login` — so a card has to be re-checkable on its own. Running
    /// the whole ProbeAll to refresh one row spins every other card and costs a
    /// round trip per system for no reason.
    /// </summary>
    public async Task RecheckAsync(
        AuthSystemId id,
        GraphService? graphService,
        TdxService? tdxService,
        SnipeService? snipeService,
        AzureDevOpsService? devOpsService)
    {
        if (!_systems.ContainsKey(id)) return;

        switch (id)
        {
            case AuthSystemId.Graph:
            case AuthSystemId.Intune:
                if (graphService != null) await ProbeGraphAsync(graphService);
                break;

            case AuthSystemId.Entra:
                if (graphService != null) await ProbeEntraAsync(graphService);
                break;

            case AuthSystemId.Snipe:
                if (snipeService != null) await ProbeSnipeAsync(snipeService);
                break;

            case AuthSystemId.Tdx:
                if (tdxService != null) await ProbeTdxAsync(tdxService);
                break;

            case AuthSystemId.DevOps:
                Update(AuthSystemId.DevOps, AuthTokenState.Authenticating());
                await ProbeDevOpsAsync(devOpsService);
                break;

            case AuthSystemId.GitHub:
                Update(AuthSystemId.GitHub, AuthTokenState.Authenticating());
                await ProbeGitHubAsync();
                break;
        }
    }

    private async Task ProbeGraphAsync(GraphService graphService)
    {
        Update(AuthSystemId.Graph, AuthTokenState.Authenticating());
        Update(AuthSystemId.Intune, AuthTokenState.Authenticating());

        try
        {
            await graphService.GetManagedDevicesAsync(limit: 1);
            Update(AuthSystemId.Graph, AuthTokenState.Valid("Entra SSO"));
            Update(AuthSystemId.Intune, AuthTokenState.Valid("Entra SSO"));
        }
        catch (Exception ex)
        {
            Update(AuthSystemId.Graph, AuthTokenState.Failed(ex.Message));
            Update(AuthSystemId.Intune, AuthTokenState.Failed(ex.Message));
        }
    }

    private async Task ProbeEntraAsync(GraphService graphService)
    {
        Update(AuthSystemId.Entra, AuthTokenState.Authenticating());

        try
        {
            await graphService.SearchGroupsAsync("test", 1);
            Update(AuthSystemId.Entra, AuthTokenState.Valid("Entra SSO"));
        }
        catch (Exception ex)
        {
            Update(AuthSystemId.Entra, AuthTokenState.Failed(ex.Message));
        }
    }

    private async Task ProbeSnipeAsync(SnipeService snipeService)
    {
        Update(AuthSystemId.Snipe, AuthTokenState.Authenticating());

        try
        {
            await snipeService.GetAssetsAsync();
            Update(AuthSystemId.Snipe, AuthTokenState.Valid(
                snipeService.UsesOidc ? "SSO bearer (Entra)" : _config.SnipeUrl ?? "Snipe-IT"));
        }
        catch (Exception ex)
        {
            Update(AuthSystemId.Snipe, AuthTokenState.Failed(ex.Message));
        }
    }

    private async Task ProbeTdxAsync(TdxService tdxService)
    {
        Update(AuthSystemId.Tdx, AuthTokenState.Authenticating());

        try
        {
            await tdxService.SearchTicketsAsync(
                new FleetMate.Core.Models.Tickets.TicketSearchRequest { MaxResults = 1 }, 1);
            Update(AuthSystemId.Tdx, AuthTokenState.Valid(tdxService.AuthenticatedUserName ?? "SSO"));
        }
        catch (Exception ex)
        {
            Update(AuthSystemId.Tdx, AuthTokenState.Failed(ex.Message));
        }
    }

    // MARK: - Individual Probes

    private async Task ProbeDevOpsAsync(AzureDevOpsService? devOpsService)
    {
        try
        {
            if (devOpsService == null)
            {
                Update(AuthSystemId.DevOps, AuthTokenState.Configured());
                return;
            }
            
            // VerifyAuthAsync acquires through the Windows broker when the
            // service has no cached token. Merely checking HasValidToken first
            // reported "configured" forever and never gave WAM a chance.
            try
            {
                await devOpsService.VerifyAuthAsync();
                Update(AuthSystemId.DevOps, AuthTokenState.Valid(
                    devOpsService.SsoUserName ?? "Windows account"));
                return;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[auth] Azure DevOps broker probe failed");
            }
            
            // Fall back to az CLI check for service principal detection
            try
            {
                var (name, type) = await RunAzAccountShowAsync();
                if (type == "servicePrincipal")
                {
                    Update(AuthSystemId.DevOps, AuthTokenState.SP(name));
                    return;
                }
                Update(AuthSystemId.DevOps, AuthTokenState.Configured());
            }
            catch
            {
                // az CLI not available or not logged in
                Update(AuthSystemId.DevOps, AuthTokenState.Configured());
            }
        }
        catch (Exception ex)
        {
            Update(AuthSystemId.DevOps, AuthTokenState.Failed(ex.Message));
        }
    }

    private async Task ProbeGitHubAsync()
    {
        try
        {
            // Both streams, and no exit-code check.
            //
            // `gh auth status` writes its report to stderr in some versions and
            // exits non-zero when logged out. Reading only stdout and treating a
            // non-zero exit as "gh is missing" meant a perfectly good session
            // showed as not signed in — the card was reporting on our own
            // plumbing rather than on GitHub.
            var (output, _) = await RunLenientAsync(ResolveGh(), "auth status --active");

            if (ParseGitHubAccount(output) is { } user)
            {
                Update(AuthSystemId.GitHub, AuthTokenState.Valid(user));
            }
            else
            {
                Update(AuthSystemId.GitHub, AuthTokenState.Configured());
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[auth] gh probe failed");
            Update(AuthSystemId.GitHub, AuthTokenState.Configured());
        }
    }

    /// <summary>
    /// Pull the account name out of <c>gh auth status</c>, or null when the
    /// output does not describe a live session.
    /// </summary>
    internal static string? ParseGitHubAccount(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        // Check the negation first. "You are not logged into any GitHub hosts"
        // contains "logged in" as a substring, so a bare positive match reads
        // gh's signed-out message as a live session.
        if (output.Contains("not logged in", StringComparison.OrdinalIgnoreCase)) return null;
        if (!output.Contains("Logged in", StringComparison.OrdinalIgnoreCase)) return null;

        // "✓ Logged in to github.com account ada (keyring)"
        var marker = output.IndexOf("account ", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return "GitHub";

        var rest = output[(marker + "account ".Length)..];
        var name = rest.Split(new[] { ' ', '\r', '\n', '(' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim();

        return string.IsNullOrWhiteSpace(name) ? "GitHub" : name;
    }

    /// <summary>
    /// Find gh by absolute path.
    ///
    /// A GUI process does not always inherit the shell's PATH, so relying on it
    /// alone is how the probe came to report a missing gh that was installed.
    /// </summary>
    private static string ResolveGh()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GitHub CLI", "gh.exe"),
            @"C:\Program Files\GitHub CLI\gh.exe",
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;

            try
            {
                var exe = Path.Combine(dir, "gh.exe");
                if (File.Exists(exe)) return exe;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not worth failing over.
            }
        }

        return "gh";
    }

    // MARK: - Shell Helpers

    private async Task<(string Name, string Type)> RunAzAccountShowAsync()
    {
        var output = await ShellOutputAsync("az", "account show -o json");
        using var doc = JsonDocument.Parse(output);
        var user = doc.RootElement.GetProperty("user");
        return (user.GetProperty("name").GetString() ?? "", user.GetProperty("type").GetString() ?? "");
    }

    /// <summary>
    /// Run a command and return both streams plus the exit code, without
    /// throwing.
    ///
    /// Tools that report status through stderr and signal state through the exit
    /// code — gh is one — cannot be read by a helper that discards stderr and
    /// throws on non-zero. Both streams are read concurrently: reading one to
    /// completion before the other deadlocks as soon as the second fills its
    /// pipe buffer.
    /// </summary>
    private static async Task<(string Output, int ExitCode)> RunLenientAsync(
        string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync();

        var combined = string.Join("\n",
            new[] { await stdoutTask, await stderrTask }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return (combined, process.ExitCode);
    }

    private static async Task<string> ShellOutputAsync(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}");

        return stdout;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
