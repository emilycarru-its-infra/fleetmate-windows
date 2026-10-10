using System.Net;
using FleetMate.Core.Config;
using FleetMate.Core.Models;
using FleetMate.Core.Services;
using FleetMate.GUI.Views.Shared;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FleetMate.Tests;

/// <summary>Every module is switchable, and its description names no product.</summary>
public class ModuleEnablementTests
{
    [Fact]
    public void EveryModuleIsListedAndEnrollmentIsNotATab()
    {
        var tags = AppModules.All.Select(m => m.Tag).ToList();
        foreach (var expected in new[] { "Development", "Projects", "Devices", "Reporting", "Manage", "Inventory", "Identity", "Tickets", "Enrollment" })
            Assert.Contains(expected, tags);
        Assert.False(AppModules.All.Single(m => m.Tag == AppModules.Enrollment).IsTab);
        Assert.DoesNotContain(AppModules.Tabs, m => m.Tag == AppModules.Enrollment);
    }

    [Fact]
    public void NothingStoredMeansEveryModuleOn()
    {
        var hidden = AppModules.ParseHidden(null);
        Assert.All(AppModules.All, m => Assert.True(AppModules.IsOn(hidden, m.Tag)));
    }

    [Fact]
    public void EnrollmentRoundTripsAndOlderNamesAreRead()
    {
        var hidden = AppModules.ParseHidden("Apple; snipe,tdx,somethingNew");
        Assert.Equal("Inventory;Tickets;Enrollment", AppModules.FormatHidden(hidden));
        Assert.False(AppModules.IsOn(hidden, AppModules.Enrollment));
    }

    [Fact]
    public void EnrollmentDoesNotCountAsAVisibleTab()
    {
        // Every tab hidden but Enrollment left on still hides every tab, so it is refused.
        var allTabs = string.Join(";", AppModules.Tabs.Select(m => m.Tag));
        Assert.Empty(AppModules.ParseHidden(allTabs));

        var allButTickets = AppModules.ParseHidden(string.Join(";", AppModules.Tabs.Where(m => m.Tag != "Tickets").Select(m => m.Tag)));
        Assert.Same(allButTickets, AppModules.WithModule(allButTickets, "Tickets", shown: false));
        // Switching Enrollment off on top of that is fine: Tickets still shows.
        Assert.Contains(AppModules.Enrollment, AppModules.WithModule(allButTickets, AppModules.Enrollment, shown: false));
    }

    [Fact]
    public void SubtitlesNameNoProducts()
    {
        var products = new[] { "Snipe", "TeamDynamix", "TDX", "Intune", "Entra", "Azure", "GitHub", "Microsoft", "ReportMate", "Autopilot", "Apple School", "Business Manager" };
        foreach (var module in AppModules.All)
            foreach (var product in products)
            {
                Assert.DoesNotContain(product, module.Subtitle, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(product, module.Title, StringComparison.OrdinalIgnoreCase);
            }
    }

    [Fact]
    public void EnrollmentIsReadyWithTheDevicesConnectionOrAnOrganization()
    {
        var config = new FleetMateConfig();
        Assert.False(AppModules.IsConfigured(AppModules.Enrollment, config));
        Assert.True(AppModules.IsConfigured(AppModules.Enrollment, config, hasEnrollmentOrganizations: true));
        config.Graph = new GraphConfig { TenantId = "00000000-0000-0000-0000-000000000000" };
        Assert.True(AppModules.IsConfigured(AppModules.Enrollment, config));
    }
}

/// <summary>Sign-ins grouped by where the credential comes from, under one status model.</summary>
public class AuthProviderGroupingTests
{
    [Fact]
    public void ElevatedGraphGroupsUnderTheAzSignIn()
    {
        var config = new FleetMateConfig();
        var groups = AuthProviderGrouping.Group(
            new[] { AuthSystemId.Intune, AuthSystemId.DevOps, AuthSystemId.GitHub, AuthSystemId.Entra, AuthSystemId.Tdx },
            config, graphUsesElevation: true);
        Assert.Equal(new[] { CredentialProvider.SingleSignOn, CredentialProvider.AzureCli, CredentialProvider.GitHubCli },
            groups.Select(g => g.Provider));
        Assert.Equal(new[] { AuthSystemId.DevOps, AuthSystemId.Tdx }, groups[0].Systems);
        Assert.Equal(new[] { AuthSystemId.Intune, AuthSystemId.Entra }, groups[1].Systems);
    }

    [Fact]
    public void DirectGraphIsSingleSignOn()
    {
        Assert.Equal(CredentialProvider.SingleSignOn,
            AuthProviderGrouping.Provider(AuthSystemId.Entra, new FleetMateConfig(), graphUsesElevation: false));
        Assert.Null(AuthProviderGrouping.ElevationDomain(AuthSystemId.Intune, graphUsesElevation: false));
        Assert.False(AuthProviderGrouping.GraphUsesElevation("direct"));
        Assert.True(AuthProviderGrouping.GraphUsesElevation("elevation"));
    }

    [Fact]
    public void ElevationDomains()
    {
        Assert.Equal(GraphDomain.Devices, AuthProviderGrouping.ElevationDomain(AuthSystemId.Intune, true));
        Assert.Equal(GraphDomain.Devices, AuthProviderGrouping.ElevationDomain(AuthSystemId.Graph, true));
        Assert.Equal(GraphDomain.Identity, AuthProviderGrouping.ElevationDomain(AuthSystemId.Entra, true));
        Assert.Null(AuthProviderGrouping.ElevationDomain(AuthSystemId.DevOps, true));
    }

    [Fact]
    public void InventoryFollowsItsSignInMethod()
    {
        var config = new FleetMateConfig { SnipeUrl = "https://assets.example.com" };
        Assert.Equal(CredentialProvider.StoredCredential, AuthProviderGrouping.Provider(AuthSystemId.Snipe, config, true));
        config.SnipeOidcAudience = "api://inventory";
        Assert.Equal(CredentialProvider.SingleSignOn, AuthProviderGrouping.Provider(AuthSystemId.Snipe, config, true));
    }

    [Fact]
    public void SingleSignOnListsFirstAndEmptyProvidersAreLeftOut()
    {
        Assert.Equal(CredentialProvider.SingleSignOn, Enum.GetValues<CredentialProvider>()[0]);
        var groups = AuthProviderGrouping.Group(new[] { AuthSystemId.Gitea }, new FleetMateConfig(), true);
        Assert.Equal(new[] { CredentialProvider.StoredCredential }, groups.Select(g => g.Provider));
    }

    [Fact]
    public void ConfiguredIsNeverAFinalStatus()
    {
        Assert.Equal(AuthDisplayKind.Checking, AuthDisplayStatus.From(AuthTokenState.Configured(), null).Kind);
        Assert.Equal(AuthDisplayStatus.NeedsSignIn, AuthDisplayStatus.From(AuthTokenState.Configured(), DateTime.Now));
        Assert.Equal(AuthDisplayStatus.Valid, AuthDisplayStatus.From(AuthTokenState.Valid("ada"), DateTime.Now));
        Assert.Equal(AuthDisplayStatus.Failed("boom"), AuthDisplayStatus.From(AuthTokenState.Failed("boom"), DateTime.Now));
        Assert.Equal(AuthDisplayStatus.NotConfigured, AuthDisplayStatus.From(AuthTokenState.NotConfigured(), null));
        Assert.Contains("svc-reader", AuthDisplayStatus.From(AuthTokenState.SP("svc-reader"), DateTime.Now).Text);
    }

    [Fact]
    public void StatusesAreNeverRedAndReadTheSame()
    {
        Assert.Equal("Checking…", AuthDisplayStatus.Checking().Label);
        Assert.Equal("Starting elevation session…", AuthDisplayStatus.Checking("Starting elevation session…").Label);
        Assert.Equal("Needs sign-in", AuthDisplayStatus.NeedsSignIn.Label);
        Assert.Equal("Not configured", AuthDisplayStatus.NotConfigured.Label);
        Assert.Equal("Failed", AuthDisplayStatus.Failed("x").Label);
        Assert.Equal(AuthDisplayTone.Attention, AuthDisplayStatus.Failed("x").Tone);
        Assert.Equal(AuthDisplayTone.Attention, AuthDisplayStatus.NeedsSignIn.Tone);
        Assert.Equal(AuthDisplayTone.Inactive, AuthDisplayStatus.NotConfigured.Tone);
    }

    [Fact]
    public void GroupSummary()
    {
        Assert.Equal("All valid", AuthDisplayStatus.Summary(new[] { AuthDisplayStatus.Valid, AuthDisplayStatus.Valid }).Label);
        Assert.Equal(AuthDisplayTone.Neutral, AuthDisplayStatus.Summary(new[] { AuthDisplayStatus.Valid, AuthDisplayStatus.Checking() }).Tone);
        var mixed = AuthDisplayStatus.Summary(new[]
        {
            AuthDisplayStatus.Valid, AuthDisplayStatus.Failed("x"), AuthDisplayStatus.NeedsSignIn,
            AuthDisplayStatus.Valid, AuthDisplayStatus.Valid, AuthDisplayStatus.NotConfigured,
        });
        Assert.Equal("2 of 5 need attention", mixed.Label);
        Assert.Equal(AuthDisplayTone.Attention, mixed.Tone);
        Assert.Equal("Needs sign-in", AuthDisplayStatus.Summary(new[] { AuthDisplayStatus.NeedsSignIn }).Label);
        Assert.Equal(AuthDisplayTone.Inactive, AuthDisplayStatus.Summary(new[] { AuthDisplayStatus.NotConfigured }).Tone);
    }

    [Fact]
    public void ParsesTheAzAccount()
    {
        var account = CliAccountProbe.ParseAzAccount(
            """{"name":"Sub One","tenantId":"t-1","user":{"name":"admin@example.com","type":"user"}}""");
        Assert.Equal("admin@example.com", account?.User);
        Assert.Equal("t-1", account?.TenantId);
        Assert.Equal("Sub One", account?.Subscription);
        Assert.False(account?.IsServicePrincipal);
        Assert.True(CliAccountProbe.ParseAzAccount("""{"user":{"name":"app","type":"servicePrincipal"}}""")?.IsServicePrincipal);
        Assert.Null(CliAccountProbe.ParseAzAccount("Please run 'az login' to setup account."));
        Assert.Null(CliAccountProbe.ParseAzAccount(""));
    }

    [Fact]
    public void ParsesTheGhStatus()
    {
        Assert.Equal("octocat", CliAccountProbe.ParseGhStatus("github.com\n  ✓ Logged in to github.com account octocat (keyring)\n  - Active account: true")?.User);
        Assert.Null(CliAccountProbe.ParseGhStatus("You are not logged into any GitHub hosts. To log in, run: gh auth login"));
    }
}

/// <summary>Elevation sessions take their image and groups from settings, and fail with the real reason.</summary>
public class ElevationSettingsTests
{
    [Fact]
    public void NoImageIsBuiltInAndTheMessageSaysWhereToSetIt()
    {
        var config = new ElevationConfig { ResourceGroup = "Sessions", IdentityPrefix = "Ops-" };
        Assert.False(config.IsConfigured);
        Assert.Equal(new[] { "elevation_image" }, config.Missing);
        var message = config.NotConfiguredMessage;
        Assert.Contains("no elevation session image is configured", message);
        Assert.Contains("elevation_image", message);
        Assert.Contains("ElevationImage", message);
        Assert.Contains("FLEETMATE_ELEVATION_IMAGE", message);
    }

    [Fact]
    public void TranscriptAccountIsOptionalAndIdentitiesDefaultToTheSessionGroup()
    {
        var config = new ElevationConfig { ResourceGroup = "Sessions", AcrImage = "registry.example.com/elevation-session:latest", IdentityPrefix = "Ops-" };
        Assert.True(config.IsConfigured);
        Assert.Equal("Sessions", config.EffectiveIdentityResourceGroup);
        config.IdentityResourceGroup = "Identities";
        Assert.Equal("Identities", config.EffectiveIdentityResourceGroup);
    }

    [Fact]
    public void ConfiguredValuesAreTrimmedAndBlanksLeaveValuesAlone()
    {
        var config = new ElevationConfig { ResourceGroup = "Sessions" };
        config.Apply(image: " registry.example.com/elevation-session:latest ", resourceGroup: "  ", transcriptAccount: "transcripts");
        Assert.Equal("registry.example.com/elevation-session:latest", config.AcrImage);
        Assert.Equal("Sessions", config.ResourceGroup);
        Assert.Equal("transcripts", config.TranscriptAccount);
    }

    [Fact]
    public void EnvironmentVariablesSetEverySetting()
    {
        var env = new Dictionary<string, string>
        {
            ["FLEETMATE_ELEVATION_IMAGE"] = "registry.example.com/img:1",
            ["FLEETMATE_ELEVATION_RESOURCE_GROUP"] = "Sessions",
            ["FLEETMATE_ELEVATION_IDENTITY_RESOURCE_GROUP"] = "Identities",
            ["FLEETMATE_ELEVATION_TRANSCRIPT_ACCOUNT"] = "transcripts",
            ["FLEETMATE_ELEVATION_IDENTITY_PREFIX"] = "Ops-",
        };
        var config = new ElevationConfig();
        config.ApplyEnvironment(name => env.GetValueOrDefault(name));
        Assert.True(config.IsConfigured);
        Assert.Equal("Identities", config.IdentityResourceGroup);
        Assert.Equal("transcripts", config.TranscriptAccount);
    }

    [Fact]
    public void FlatYamlKeysAreRead()
    {
        var yaml = """
            elevation:
              resourceGroup: Sessions
              identityPrefix: Ops-
            elevation_image: registry.example.com/elevation-session:latest
            elevation_identity_resource_group: Identities
            """;
        var config = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<FleetMateConfig>(yaml);
        config.FoldFlatElevationKeys();
        Assert.Equal("registry.example.com/elevation-session:latest", config.Elevation?.AcrImage);
        Assert.Equal("Sessions", config.Elevation?.ResourceGroup);
        Assert.Equal("Identities", config.Elevation?.IdentityResourceGroup);
        Assert.True(config.Elevation?.IsConfigured);
    }

    [Fact]
    public void RegistryReadsTheImageUnderEitherName()
    {
        var config = new FleetMateConfig();
        FleetMateConfig.ApplyRegistryValues(name => name switch
        {
            "ElevationImage" => "registry.example.com/new:1",
            "ElevationAcrImage" => "registry.example.com/old:1",
            "ElevationResourceGroup" => "Sessions",
            _ => null,
        }, config, fromPolicy: true);
        Assert.Equal("registry.example.com/new:1", config.Elevation?.AcrImage);

        var older = new FleetMateConfig();
        FleetMateConfig.ApplyRegistryValues(name => name == "ElevationAcrImage" ? "registry.example.com/old:1" : null, older, fromPolicy: false);
        Assert.Equal("registry.example.com/old:1", older.Elevation?.AcrImage);
    }

    [Fact]
    public void AzMessageDropsWarnings()
    {
        Assert.Equal("ERROR: (AuthorizationFailed) denied",
            ElevationSession.AzMessage(("", "WARNING: preview\nERROR: (AuthorizationFailed) denied\n", 1)));
        Assert.Equal("some output", ElevationSession.AzMessage(("some output\n", "", 2)));
        Assert.Equal("az exited with code 3", ElevationSession.AzMessage(("", "", 3)));
    }

    [Fact]
    public void TransitionalStatesAreWaitedOn()
    {
        Assert.True(ElevationSession.IsTransitional("Pending"));
        Assert.True(ElevationSession.IsTransitional("Creating"));
        Assert.True(ElevationSession.IsTransitional("Repairing"));
        Assert.False(ElevationSession.IsTransitional("Running"));
        Assert.False(ElevationSession.IsTransitional("Terminated"));
        Assert.False(ElevationSession.IsTransitional(""));
    }

    [Fact]
    public void ContainerHoldsWithAzeHoldWhenTheImageHasIt()
    {
        var line = ElevationSession.ContainerCommandLine("cid", 3600, 1800);
        Assert.Contains("--client-id cid", line);
        Assert.Contains("exec aze-hold 3600 1800", line);
        Assert.EndsWith("sleep 3600'", line);
    }

    [Fact]
    public async Task AnUnusableSessionReportsTheRealReason()
    {
        var status = new ElevationStatus();
        using var client = new HttpClient(new ElevationHttpHandler(new ElevationConfig(), status))
        {
            BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"),
        };
        var response = await client.GetAsync("deviceManagement/managedDevices");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("Elevation is not set up", status.LastError);
        Assert.DoesNotContain("ElevationException", status.LastError);
        Assert.Contains("Elevation is not set up", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void OtherFailuresNameOnlyTheirType()
    {
        var reason = ElevationHttpHandler.FailureReason(new InvalidOperationException("{\"value\":[\"secret\"]}"));
        Assert.Contains("InvalidOperationException", reason);
        Assert.DoesNotContain("secret", reason);
    }
}

/// <summary>About shows the stamped release version, not the assembly version.</summary>
public class AppVersionDisplayTests
{
    [Theory]
    [InlineData("2026.10.10.0158", "2026.10.10.0158")]
    [InlineData("2026.10.10.0158+abc1234def", "2026.10.10.0158")]
    [InlineData("1.0.0+abc1234def5678", "dev (abc1234)")]
    [InlineData("1.0.0", "dev")]
    [InlineData(null, "dev")]
    public void Formats(string? informational, string expected) =>
        Assert.Equal(expected, AppVersionDisplay.Format(informational));

    [Fact]
    public void AboutListsRelatedProjects() =>
        Assert.Equal(new[] { "ReportMate", "BootstrapMate", "Cimian", "ASBMUtil" }, SettingsPage.RelatedProjects.Select(p => p.Name));
}
