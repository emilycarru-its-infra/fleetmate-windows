using FleetMate.Core.Config;
using Xunit;

namespace FleetMate.Tests;

public class ManagedSettingsTests
{
    private static Func<string, object?> Values(Dictionary<string, object?> values) =>
        name => values.TryGetValue(name, out var v) ? v : null;

    [Fact]
    public void PolicyOverridesTheOperatorsOwnSettings()
    {
        var config = new FleetMateConfig();
        FleetMateConfig.ApplyRegistryValues(Values(new()
        {
            ["DevOpsBaseUrl"] = "https://user.example",
            ["TdxBaseUrl"] = "https://user-tdx.example",
        }), config, fromPolicy: false);

        FleetMateConfig.ApplyRegistryValues(Values(new()
        {
            ["DevOpsBaseUrl"] = "https://managed.example",
        }), config, fromPolicy: true);

        Assert.Equal("https://managed.example", config.AzureDevOps!.HostUrl);
        // Unset in policy, so the operator's value stands.
        Assert.Equal("https://user-tdx.example", config.Tdx!.BaseUrl);
    }

    [Fact]
    public void PolicyNeverSuppliesASecret()
    {
        var config = new FleetMateConfig();
        FleetMateConfig.ApplyRegistryValues(Values(new()
        {
            ["SnipeUrl"] = "https://snipe.example",
            ["SnipeApiKey"] = "secret",
            ["ReportMatePassphrase"] = "secret",
        }), config, fromPolicy: true);

        Assert.Equal("https://snipe.example", config.SnipeUrl);
#pragma warning disable CS0618
        Assert.Null(config.SnipeApiKey);
        Assert.Null(config.ReportMatePassphrase);
#pragma warning restore CS0618
    }

    [Fact]
    public void PolicyDwordValuesAreReadAsText()
    {
        // LoadPolicy passes values through ToString(), so a REG_DWORD app ID works.
        var config = new FleetMateConfig();
        FleetMateConfig.ApplyRegistryValues(name => name == "TdxTicketingAppId" ? 44.ToString() : null,
            config, fromPolicy: true);
        Assert.Equal(44, config.Tdx!.TicketingAppId);
    }

    [Fact]
    public void PolicyPathIsTheStandardPoliciesKey()
    {
        Assert.Equal(@"SOFTWARE\Policies\FleetMate", FleetMateConfig.PolicyRegistryPath);
    }
}
