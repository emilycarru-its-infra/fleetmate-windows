using FleetMate.Core.Config;
using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The aze domain slug/name mapping is structural (part of the protocol). The
/// org-specific managed-identity name is built as ElevationConfig.IdentityPrefix
/// + DomainName — no hardcoded "DevOps-*" identities in source anymore.
/// </summary>
public class ElevationDomainTests
{
    [Theory]
    [InlineData(GraphDomain.Terraform, "terraform", "Terraform")]
    [InlineData(GraphDomain.Devices, "devices", "Devices")]
    [InlineData(GraphDomain.Identity, "identity", "Identity")]
    [InlineData(GraphDomain.Systems, "systems", "Systems")]
    [InlineData(GraphDomain.Cloud, "cloud", "Cloud")]
    public void Domain_MapsToSlugAndName(GraphDomain domain, string slug, string name)
    {
        Assert.Equal(slug, domain.Slug());
        Assert.Equal(name, domain.DomainName());
    }

    [Fact]
    public void EveryDomain_HasDistinctSlugAndName()
    {
        var domains = Enum.GetValues<GraphDomain>();
        var slugs = domains.Select(d => d.Slug()).ToList();
        var names = domains.Select(d => d.DomainName()).ToList();

        Assert.All(slugs, s => Assert.False(string.IsNullOrWhiteSpace(s)));
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        Assert.Equal(slugs.Count, slugs.Distinct().Count());
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void IdentityName_ComposesConfiguredPrefixWithDomain()
    {
        // The identity name comes from config, not hardcoded source.
        var prefix = "DevOps-";
        Assert.Equal("DevOps-Devices", prefix + GraphDomain.Devices.DomainName());
        Assert.Equal("DevOps-Identity", prefix + GraphDomain.Identity.DomainName());
    }

    [Fact]
    public void ElevationConfig_IsConfigured_RequiresEveryField()
    {
        Assert.False(new ElevationConfig().IsConfigured);
        Assert.False(new ElevationConfig { ResourceGroup = "rg" }.IsConfigured);

        var full = new ElevationConfig
        {
            ResourceGroup = "rg",
            AcrImage = "reg.azurecr.io/img:latest",
            TranscriptAccount = "acct",
            IdentityPrefix = "DevOps-",
        };
        Assert.True(full.IsConfigured);
    }

    /// <summary>
    /// Which managed identity a Graph call runs as. The directory's /devices
    /// collection routes to devices, not identity: deleting a device object and
    /// deleting its Intune record are one re-provisioning operation, and keeping
    /// them in one domain means neither identity needs a scope the other holds.
    /// </summary>
    [Theory]
    // Intune
    [InlineData("https://graph.microsoft.com/v1.0/deviceManagement/managedDevices", GraphDomain.Devices)]
    [InlineData("https://graph.microsoft.com/v1.0/deviceManagement/windowsAutopilotDeviceIdentities", GraphDomain.Devices)]
    [InlineData("https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps", GraphDomain.Devices)]
    // Directory device objects
    [InlineData("https://graph.microsoft.com/v1.0/devices", GraphDomain.Devices)]
    [InlineData("https://graph.microsoft.com/v1.0/devices/839c6139-1d9d-4b8d-9c35-2319c85e24c9", GraphDomain.Devices)]
    [InlineData("https://graph.microsoft.com/v1.0/devices?$filter=displayName%20eq%20'ANIM-STD-04'", GraphDomain.Devices)]
    [InlineData("https://graph.microsoft.com/beta/devices", GraphDomain.Devices)]
    // Identity
    [InlineData("https://graph.microsoft.com/v1.0/users/rod@example.org", GraphDomain.Identity)]
    [InlineData("https://graph.microsoft.com/v1.0/groups", GraphDomain.Identity)]
    [InlineData("https://graph.microsoft.com/v1.0/directoryRoles", GraphDomain.Identity)]
    public void RouteDomain_SendsCallsToTheRightIdentity(string url, GraphDomain expected)
    {
        Assert.Equal(expected, ElevationHttpHandler.RouteDomain(url));
    }

    [Fact]
    public void RouteDomain_DoesNotMistakeAUserQueryMentioningDevicesForADeviceCall()
    {
        // A query string can mention devices without being a /devices call; only
        // the path segment after the API version decides.
        Assert.Equal(GraphDomain.Identity,
            ElevationHttpHandler.RouteDomain("https://graph.microsoft.com/v1.0/users?$expand=registeredDevices"));
    }
}
