using FleetMate.Core.Config;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Reporting;
using FleetMate.Core.Services.Tickets;

namespace FleetMate.WinUI;

public sealed class DesktopRuntime
{
    private TdxSsoResult? _tdxSession;
    public FleetMateConfig Config { get; private set; } = FleetMateConfig.LoadDesktop();

    public void Reload() => Config = FleetMateConfig.LoadDesktop();

    public void SetTdxSession(TdxSsoResult session) => _tdxSession = session;

    public async Task<ModuleSnapshot> LoadModuleAsync(string module)
    {
        Reload();
        return module switch
        {
            "Devices" => await LoadDevicesAsync(),
            "Inventory" => await LoadInventoryAsync(),
            "Tickets" => await LoadTicketsAsync(),
            "Projects" => await LoadProjectsAsync(),
            "Identity" => await LoadIdentityAsync(),
            _ => new(module, "No data source is registered.", [])
        };
    }

    private async Task<ModuleSnapshot> LoadDevicesAsync()
    {
        using var graph = new GraphService(Config.Graph!, Config.Elevation);
        var devices = await graph.GetManagedDevicesAsync(limit: 100);
        var rows = devices.Select(x => new ModuleRow(
            x.DeviceName,
            string.Join(" · ", new[] { x.Model, x.OperatingSystem, x.OsVersion }.Where(v => !string.IsNullOrWhiteSpace(v))),
            $"{x.ComplianceState ?? "unknown"} · {x.UserPrincipalName ?? "unassigned"}"));
        return new("Devices", $"{devices.Count} Microsoft Intune devices shown", rows.ToList());
    }

    private async Task<ModuleSnapshot> LoadInventoryAsync()
    {
        using var service = SnipeService.FromConfig(Config);
        var assets = await service.GetAssetsAsync();
        var rows = assets.Take(100).Select(x => new ModuleRow(
            string.IsNullOrWhiteSpace(x.Name) ? x.AssetTag : x.Name,
            $"{x.AssetTag} · {x.Model?.Name ?? x.ModelNumber ?? "Unknown model"}",
            $"{x.StatusLabel?.Name ?? "Unknown status"} · {x.AssignedTo?.Name ?? "Unassigned"}"));
        return new("Inventory", $"{assets.Count} Snipe-IT assets · first {Math.Min(100, assets.Count)} shown", rows.ToList());
    }

    private async Task<ModuleSnapshot> LoadTicketsAsync()
    {
        if (Config.Tdx is null || _tdxSession is not { Success: true, Token: not null } session || session.Expiry <= DateTime.UtcNow)
            throw new InvalidOperationException("Open Settings → Authentication and sign in to TeamDynamix.");

        using var service = new TdxService(Config.Tdx);
        service.SetSsoToken(session.Token, session.Expiry, session.UserEmail, session.UserName);
        var tickets = await service.SearchTicketsAsync(new TicketSearchRequest { MaxResults = 100 }, 100);
        var rows = tickets.Select(x => new ModuleRow(
            $"#{x.Id}  {x.Title}",
            $"{x.StatusName ?? "Unknown status"} · {x.PriorityName ?? "No priority"}",
            $"{x.RequestorName ?? "Unknown requestor"} · {x.ResponsibleFullName ?? "Unassigned"}"));
        return new("Tickets", $"{tickets.Count} TeamDynamix tickets shown", rows.ToList());
    }

    private async Task<ModuleSnapshot> LoadProjectsAsync()
    {
        using var service = new AzureDevOpsService(Config.AzureDevOps!);
        var boards = await service.GetBoardsAsync();
        var rows = boards.Select(x => new ModuleRow(x.Name, Config.AzureDevOps?.Project ?? "Azure DevOps", "Board"));
        return new("Projects", $"{boards.Count} Azure DevOps boards", rows.ToList());
    }

    private async Task<ModuleSnapshot> LoadIdentityAsync()
    {
        using var graph = new GraphService(Config.Graph!, Config.Elevation);
        var users = await graph.SearchUsersAsync(string.Empty, 50);
        var rows = users.Select(x => new ModuleRow(
            x.DisplayName,
            x.UserPrincipalName,
            $"{x.Department ?? "No department"} · {(x.AccountEnabled == false ? "Disabled" : "Enabled")}"));
        return new("Identity", $"{users.Count} Microsoft Entra users shown", rows.ToList());
    }

    public async Task<IReadOnlyList<ServiceCheck>> CheckAsync()
    {
        var checks = new List<ServiceCheck>();
        await Add(checks, "Entra", async () =>
        {
            var source = EntraTokenSource.Shared ?? EntraTokenSource.Configure(Config.Graph?.TenantId, Config.EntraClientId);
            _ = await source.GetTokenAsync("https://graph.microsoft.com");
            return "Windows broker session valid";
        });

        if (!string.IsNullOrWhiteSpace(Config.Graph?.TenantId))
            await Add(checks, "Microsoft Graph", async () =>
            {
                using var service = new GraphService(Config.Graph, Config.Elevation);
                var rows = await service.GetManagedDevicesAsync(limit: 1);
                return rows.Count > 0 ? "Managed devices visible" : "Reachable";
            });

        if (!string.IsNullOrWhiteSpace(Config.AzureDevOps?.Organization))
            await Add(checks, "Azure DevOps", async () =>
            {
                using var service = new AzureDevOpsService(Config.AzureDevOps);
                return await service.VerifyAuthAsync() ? "Operator SSO valid" : throw new InvalidOperationException("Broker token rejected");
            });

        if (!string.IsNullOrWhiteSpace(Config.SnipeUrl))
            await Add(checks, "Snipe-IT", async () =>
            {
                using var service = SnipeService.FromConfig(Config);
                return $"{(await service.GetAssetsAsync()).Count} assets";
            });

        if (!string.IsNullOrWhiteSpace(Config.ReportMateUrl))
            await Add(checks, "ReportMate", async () =>
            {
                using var service = ReportMateService.FromConfig(Config);
                return $"{(await service.GetDevicesAsync()).Count} devices";
            });

        if (!string.IsNullOrWhiteSpace(Config.Tdx?.BaseUrl))
            await Add(checks, "TeamDynamix", async () =>
            {
                var result = _tdxSession is { Success: true, Token: not null } && _tdxSession.Expiry > DateTime.UtcNow
                    ? _tdxSession
                    : await new TdxSsoService(Config.Tdx.BaseUrl).TrySilentSsoAsync();
                if (!result.Success || string.IsNullOrEmpty(result.Token))
                    throw new InvalidOperationException("Complete the one-time TeamDynamix browser sign-in in Settings");
                using var service = new TdxService(Config.Tdx);
                service.SetSsoToken(result.Token, result.Expiry, result.UserEmail, result.UserName);
                _ = await service.VerifyTicketAccessAsync();
                return $"SSO as {result.UserName ?? result.UserEmail ?? "operator"}; ticket API readable";
            });

        return checks;
    }

    private static async Task Add(List<ServiceCheck> checks, string name, Func<Task<string>> probe)
    {
        try { checks.Add(new(name, true, await probe())); }
        catch (Exception ex) { checks.Add(new(name, false, ex.Message.Split('\r', '\n')[0])); }
    }
}

public sealed record ServiceCheck(string Name, bool Success, string Detail);
public sealed record ModuleSnapshot(string Title, string Summary, IReadOnlyList<ModuleRow> Rows);
public sealed record ModuleRow(string Title, string Subtitle, string Detail);
