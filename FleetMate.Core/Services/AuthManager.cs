using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Models;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Services.Tickets;
using FleetMate.Core.Services.Projects;
using Serilog;

namespace FleetMate.Core.Services;

/// <summary>
/// Centralized manager that tracks authentication state for every configured system.
/// Implements INotifyPropertyChanged for data binding (WPF and WinUI 3).
/// Lives in Core so both GUIs share one auth-status source of truth.
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

        // Assets — Snipe-IT
        if (!string.IsNullOrEmpty(_config.SnipeUrl) && !string.IsNullOrEmpty(_config.SnipeApiKey))
        {
            systems[AuthSystemId.Snipe] = new AuthSystemStatus { SystemId = AuthSystemId.Snipe, State = AuthTokenState.Configured() };
        }

        // Tickets — TDX
        if (_config.Tdx != null && !string.IsNullOrEmpty(_config.Tdx.BaseUrl))
        {
            systems[AuthSystemId.Tdx] = new AuthSystemStatus { SystemId = AuthSystemId.Tdx, State = AuthTokenState.Configured() };
        }

        // Projects — DevOps
        if (_config.AzureDevOps != null && !string.IsNullOrEmpty(_config.AzureDevOps.Organization))
        {
            systems[AuthSystemId.DevOps] = new AuthSystemStatus { SystemId = AuthSystemId.DevOps, State = AuthTokenState.Configured() };
        }

        // Projects — GitHub
        if (_config.Tasks?.Providers?.GitHub is { Enabled: true })
        {
            systems[AuthSystemId.GitHub] = new AuthSystemStatus { SystemId = AuthSystemId.GitHub, State = AuthTokenState.Configured() };
        }

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

    public void SignOut()
    {
        foreach (var id in _systems.Keys.ToList())
            Update(id, AuthTokenState.Configured());
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

        // Graph / Intune
        if (_systems.ContainsKey(AuthSystemId.Graph) && graphService != null)
        {
            Update(AuthSystemId.Graph, AuthTokenState.Authenticating());
            Update(AuthSystemId.Intune, AuthTokenState.Authenticating());
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await graphService.GetManagedDevicesAsync(limit: 1);
                    Update(AuthSystemId.Graph, AuthTokenState.Valid("Service Credential"));
                    Update(AuthSystemId.Intune, AuthTokenState.Valid("Service Credential"));
                }
                catch (Exception ex)
                {
                    Update(AuthSystemId.Graph, AuthTokenState.Failed(ex.Message));
                    Update(AuthSystemId.Intune, AuthTokenState.Failed(ex.Message));
                }
            }));
        }

        // Entra
        if (_systems.ContainsKey(AuthSystemId.Entra) && graphService != null)
        {
            Update(AuthSystemId.Entra, AuthTokenState.Authenticating());
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await graphService.SearchGroupsAsync("test", 1);
                    Update(AuthSystemId.Entra, AuthTokenState.Valid("Service Credential"));
                }
                catch (Exception ex)
                {
                    Update(AuthSystemId.Entra, AuthTokenState.Failed(ex.Message));
                }
            }));
        }

        // Snipe-IT
        if (_systems.ContainsKey(AuthSystemId.Snipe) && snipeService != null)
        {
            Update(AuthSystemId.Snipe, AuthTokenState.Authenticating());
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await snipeService.GetAssetsAsync();
                    Update(AuthSystemId.Snipe, AuthTokenState.Valid(_config.SnipeUrl ?? "Snipe-IT"));
                }
                catch (Exception ex)
                {
                    Update(AuthSystemId.Snipe, AuthTokenState.Failed(ex.Message));
                }
            }));
        }

        // TDX
        if (_systems.ContainsKey(AuthSystemId.Tdx) && tdxService != null)
        {
            Update(AuthSystemId.Tdx, AuthTokenState.Authenticating());
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await tdxService.SearchTicketsAsync(new FleetMate.Core.Models.Tickets.TicketSearchRequest { MaxResults = 1 }, 1);
                    var userName = tdxService.AuthenticatedUserName;
                    Update(AuthSystemId.Tdx, AuthTokenState.Valid(userName ?? "Service Account"));
                }
                catch (Exception ex)
                {
                    Update(AuthSystemId.Tdx, AuthTokenState.Failed(ex.Message));
                }
            }));
        }

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
            
            // If we already have a valid SSO token, verify it works
            if (devOpsService.HasValidToken)
            {
                try
                {
                    await devOpsService.VerifyAuthAsync();
                    Update(AuthSystemId.DevOps, AuthTokenState.Valid(devOpsService.SsoUserName ?? "SSO User"));
                    return;
                }
                catch
                {
                    // Token is expired or invalid — fall through to az CLI check
                }
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
            var output = await ShellOutputAsync("gh", "auth status --active");
            if (output.Contains("Logged in"))
            {
                var user = output.Split("account ").LastOrDefault()?
                    .Split(' ').FirstOrDefault()?.Trim();
                Update(AuthSystemId.GitHub, AuthTokenState.Valid(user));
            }
            else
            {
                Update(AuthSystemId.GitHub, AuthTokenState.Configured());
            }
        }
        catch
        {
            Update(AuthSystemId.GitHub, AuthTokenState.Configured());
        }
    }

    // MARK: - Shell Helpers

    private async Task<(string Name, string Type)> RunAzAccountShowAsync()
    {
        var output = await ShellOutputAsync("az", "account show -o json");
        using var doc = JsonDocument.Parse(output);
        var user = doc.RootElement.GetProperty("user");
        return (user.GetProperty("name").GetString() ?? "", user.GetProperty("type").GetString() ?? "");
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
