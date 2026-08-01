using System.Reflection;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Identity;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Services.Reporting;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Guards the "no service accounts" rule structurally rather than by inspection.
///
/// A deprecation that lives only in a commit message decays: the next person to
/// add a config property has no reason to know the rule exists. These tests fail
/// the build instead, and name the rule in the failure.
/// </summary>
public class NoServiceAccountsTests
{
    /// <summary>
    /// Property names that would reintroduce a shared credential. Matched
    /// case-insensitively as substrings so `TdxBeidSecret` is caught as well as
    /// `Beid`.
    /// </summary>
    private static readonly string[] ForbiddenFragments =
    {
        "ClientSecret", "WebServicesKey", "Beid", "Passphrase", "ApiKey",
    };

    /// <summary>
    /// Config types that must expose no shared credential at all. GitHub and
    /// Gitea are handled separately — see the tests below.
    /// </summary>
    public static TheoryData<Type> SecretlessConfigTypes => new()
    {
        typeof(GraphConfig),
        typeof(TdxConfig),
        typeof(AzureDevOpsConfig),
    };

    [Theory]
    [MemberData(nameof(SecretlessConfigTypes))]
    public void ConfigTypes_ExposeNoSharedCredential(Type configType)
    {
        var offenders = configType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(name => ForbiddenFragments.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{configType.Name} exposes {string.Join(", ", offenders)}. FleetMate authenticates as the " +
            "signed-in operator (SSO) or, for privileged elevation, as a managed identity. " +
            "Shared credentials are not an accepted auth path.");
    }

    [Fact]
    public void TdxConfig_HasNoUsernameOrPassword()
    {
        var properties = typeof(TdxConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        Assert.DoesNotContain("Username", properties);
        Assert.DoesNotContain("Password", properties);
        // The Key Vault existed only to feed the service account.
        Assert.DoesNotContain("KeyVaultName", properties);
    }

    [Fact]
    public void TdxConfig_HasNoAuthMethodChoice()
    {
        // There is exactly one way in, so there is nothing to choose between.
        // A lingering enum would imply the service-account path still exists.
        Assert.Null(Type.GetType("FleetMate.Core.Models.Tickets.TdxAuthMethod, FleetMate.Core"));
        Assert.DoesNotContain("AuthMethod",
            typeof(TdxConfig).GetProperties().Select(p => p.Name));
    }

    [Fact]
    public void TdxSso_IsEnabledWhereverTdxIsConfigured()
    {
        // SSO is not an opt-in mode any more; configuring TDX at all means SSO.
        Assert.True(new TdxConfig { BaseUrl = "https://td.example.edu/TDWebApi" }.SsoEnabled);
        Assert.False(new TdxConfig().SsoEnabled);
    }

    [Fact]
    public void ElevationConfig_IsTheOnlySurvivingNonUserIdentity()
    {
        // Managed identity for elevation is deliberately kept. This test exists
        // so that removing it is also a deliberate act.
        Assert.NotNull(Type.GetType("FleetMate.Core.Config.ElevationConfig, FleetMate.Core")
                       ?? typeof(FleetMateConfig).GetProperty("Elevation")?.PropertyType);
    }
}

public class SsoDefaultsTests
{
    [Fact]
    public void SnipeAndReportMate_DefaultToSso()
    {
        var config = FleetMateConfig.Load();

        Assert.True(config.SnipeUsesOidc);
        Assert.True(config.ReportMateUsesOidc);
        Assert.Equal(FleetMateConfig.DefaultSnipeOidcAudience, config.SnipeOidcAudience);
        Assert.Equal(FleetMateConfig.DefaultReportMateOidcAudience, config.ReportMateOidcAudience);
    }

    [Fact]
    public void AnExplicitAudienceBeatsTheDefault()
    {
        var config = new FleetMateConfig { SnipeOidcAudience = "api://custom-snipe" };

        // ApplySsoDefaults must not clobber a deliberate choice.
        var apply = typeof(FleetMateConfig).GetMethod(
            "ApplySsoDefaults", BindingFlags.NonPublic | BindingFlags.Static);
        apply!.Invoke(null, new object[] { config });

        Assert.Equal("api://custom-snipe", config.SnipeOidcAudience);
    }

    [Fact]
    public void SnipeService_PrefersBearerOverAnApiKey()
    {
        // Prefer-bearer is what makes migration additive: an estate can set the
        // audience without racing to delete keys everywhere first.
        using var service = new SnipeService(
            "https://snipe.example.edu", apiKey: "legacy-key", cacheMinutes: 5,
            oidcAudience: "api://snipe");

        Assert.True(service.UsesOidc);
    }

    [Fact]
    public void SnipeService_FallsBackToTheApiKeyWithoutAnAudience()
    {
        using var service = new SnipeService(
            "https://snipe.example.edu", apiKey: "legacy-key", cacheMinutes: 5, oidcAudience: null);

        Assert.False(service.UsesOidc);
    }

    [Fact]
    public void ReportMateService_PrefersBearerOverThePassphrase()
    {
        using var service = new ReportMateService(
            "https://reportmate.example.edu", passphrase: "legacy", cacheMinutes: 5,
            oidcAudience: "api://reportmate");

        Assert.True(service.UsesOidc);
    }

    [Fact]
    public void SnipeService_IsConfiguredWithoutAnyCredential()
    {
        // A URL alone must be enough — gating on a key is what hid a working
        // SSO Snipe from the auth panel entirely.
        using var service = new SnipeService("https://snipe.example.edu");
        Assert.True(service.IsConfigured);
    }
}

public class GraphPagingTests
{
    private static int PageSizeFor(GraphService service, int limit) =>
        (int)typeof(GraphService)
            .GetMethod("PageSizeFor", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, new object[] { limit })!;

    [Theory]
    // Graph rejects $top=1000 outright on directory collections, so a caller
    // asking for 1000 groups used to get zero of them.
    [InlineData(1000, 1000, 999)]
    [InlineData(5000, 5000, 999)]
    // Below the ceiling, the smaller of limit and configured page size wins.
    [InlineData(50, 100, 50)]
    [InlineData(500, 100, 100)]
    [InlineData(1, 100, 1)]
    public void PageSize_NeverExceedsGraphsCeiling(int limit, int configuredPageSize, int expected)
    {
        using var service = new GraphService(new GraphConfig { PageSize = configuredPageSize });
        Assert.Equal(expected, PageSizeFor(service, limit));
    }

    [Fact]
    public void PageSize_StaysUnderTheCeilingEvenIfConfigOverreaches()
    {
        using var service = new GraphService(new GraphConfig { PageSize = 100_000 });
        Assert.True(PageSizeFor(service, int.MaxValue) <= 999);
    }

    [Fact]
    public void DeviceGroupLimit_IsOneSharedConstant()
    {
        // Three call sites used to disagree; the preload's lower cap silently
        // won because it populated the cache first.
        Assert.Equal(1000, DeviceGroupFetch.Limit);
    }

    [Fact]
    public void DeviceGroupLimit_ExceedsTheGraphPageCeiling_AndThatIsFine()
    {
        // The limit is a total, not a page. Pagination follows nextLink, so a
        // 1000-group ask is served by two pages rather than being rejected.
        Assert.True(DeviceGroupFetch.Limit > 999);

        using var service = new GraphService(new GraphConfig { PageSize = 1000 });
        Assert.Equal(999, PageSizeFor(service, DeviceGroupFetch.Limit));
    }
}
