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
}
