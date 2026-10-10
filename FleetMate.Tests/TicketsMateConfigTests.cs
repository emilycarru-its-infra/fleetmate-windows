using FleetMate.Core.Config;
using FleetMate.Core.Models;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// TicketsMate runs Tickets alone: whatever the files, registry, policy or
/// environment set for the other modules is cleared, and only TeamDynamix is
/// listed, probed or signed in to.
/// </summary>
public class TicketsMateConfigTests
{
    private static FleetMateConfig EveryModuleConfigured()
    {
#pragma warning disable CS0618 // the retired secrets are part of what must be cleared
        return new FleetMateConfig
        {
            Graph = new GraphConfig { TenantId = "00000000-0000-0000-0000-000000000001", ClientId = "client" },
            Elevation = new ElevationConfig { ResourceGroup = "rg", AcrImage = "img", TranscriptAccount = "acct", IdentityPrefix = "p-" },
            SnipeUrl = "https://assets.example.edu",
            SnipeOidcAudience = "api://snipe",
            SnipeApiKey = "key",
            ReportMateUrl = "https://reports.example.edu",
            ReportMateOidcAudience = "api://reportmate",
            ReportMatePassphrase = "passphrase",
            AzureDevOps = new AzureDevOpsConfig { Organization = "example-org", Project = "Example" },
            Tasks = new TasksConfig { Providers = new TaskProvidersConfig { Gitea = new GiteaProviderConfig { Enabled = true, Url = "https://git.example.edu" } } },
            Manage = new ManageConfig { RosterPath = @"C:\roster\computers.csv" },
            SecureShell = new SecureShellConfig(),
            HandbookRepoUrl = "https://git.example.edu/handbook.git",
            HandbookSiteUrl = "https://handbook.example.edu",
            AgentsHubRepoUrl = "https://git.example.edu/agents.git",
            Tdx = new TdxConfig { BaseUrl = "https://td.example.edu/TDWebApi", AppId = 116 },
            EntraClientId = "broker-client",
            LogLevel = "Debug",
        };
#pragma warning restore CS0618
    }

    [Fact]
    public void LimitToTickets_ClearsEveryOtherModule()
    {
        var config = EveryModuleConfigured();
        config.LimitToTickets();

        Assert.Null(config.Graph);
        Assert.Null(config.Elevation);
        Assert.Null(config.SnipeUrl);
        Assert.Null(config.SnipeOidcAudience);
        Assert.Null(config.ReportMateUrl);
        Assert.Null(config.ReportMateOidcAudience);
#pragma warning disable CS0618
        Assert.Null(config.SnipeApiKey);
        Assert.Null(config.ReportMatePassphrase);
#pragma warning restore CS0618
        Assert.Null(config.AzureDevOps);
        Assert.Null(config.Tasks);
        Assert.Equal("", config.Manage.RosterPath);
        Assert.Null(config.SecureShell);
        Assert.Null(config.HandbookRepoUrl);
        Assert.Null(config.HandbookSiteUrl);
        Assert.Null(config.AgentsHubRepoUrl);
        Assert.False(config.SnipeUsesOidc);
        Assert.False(config.ReportMateUsesOidc);
    }

    [Fact]
    public void LimitToTickets_KeepsTeamDynamixAndSharedSettings()
    {
        var config = EveryModuleConfigured();
        config.LimitToTickets();

        Assert.NotNull(config.Tdx);
        Assert.Equal("https://td.example.edu/TDWebApi", config.Tdx!.BaseUrl);
        Assert.Equal("broker-client", config.EntraClientId);
        Assert.Equal("Debug", config.LogLevel);
    }

    [Fact]
    public void LimitToTickets_LeavesNoGitHubProviderConfigured()
    {
        var config = EveryModuleConfigured();
        config.Tasks!.Providers.GitHub = new GitHubProviderConfig { Token = "token" };
        config.LimitToTickets();

        Assert.Null(config.Tasks?.Providers?.GitHub);
        Assert.Null(config.GitHubProviderOrDefault().Token);
    }

    [Fact]
    public void TicketsMateAuthListsTeamDynamixAlone()
    {
        var auth = new AuthManager(EveryModuleConfigured(), AppEdition.TicketsMate);

        Assert.Equal(new[] { AuthSystemId.Tdx }, auth.ConfiguredSystems.Select(s => s.SystemId));
        Assert.Equal(AuthStateKind.Configured, auth.Systems[AuthSystemId.Tdx].State.Kind);
    }

    [Fact]
    public void TicketsMateAuthListsAnUnconfiguredTeamDynamixSoItCanBeSetUp()
    {
        var auth = new AuthManager(new FleetMateConfig(), AppEdition.TicketsMate);

        Assert.Equal(new[] { AuthSystemId.Tdx }, auth.ConfiguredSystems.Select(s => s.SystemId));
        Assert.Equal(AuthStateKind.NotConfigured, auth.Systems[AuthSystemId.Tdx].State.Kind);
    }

    [Fact]
    public async Task TicketsMateNeverProbesTheOtherSystems()
    {
        var auth = new AuthManager(EveryModuleConfigured(), AppEdition.TicketsMate);

        // With no services passed, only DevOps and GitHub would be probed (they
        // need none); TicketsMate must not touch either.
        await auth.ProbeAllAsync(null, null, null, null);

        Assert.False(auth.Systems.ContainsKey(AuthSystemId.DevOps));
        Assert.False(auth.Systems.ContainsKey(AuthSystemId.GitHub));
        Assert.Equal(AuthStateKind.Configured, auth.Systems[AuthSystemId.Tdx].State.Kind);
    }

    [Fact]
    public void FleetMateAuthStillListsEveryConfiguredSystem()
    {
        var auth = new AuthManager(EveryModuleConfigured(), AppEdition.FleetMate);
        var listed = auth.ConfiguredSystems.Select(s => s.SystemId).ToHashSet();

        foreach (var id in new[] { AuthSystemId.Graph, AuthSystemId.Intune, AuthSystemId.Entra, AuthSystemId.Snipe,
                                   AuthSystemId.Tdx, AuthSystemId.DevOps, AuthSystemId.GitHub, AuthSystemId.Gitea })
            Assert.Contains(id, listed);
    }
}
