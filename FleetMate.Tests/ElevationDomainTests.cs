using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The aze elevation domain → managed-identity map is the security-critical
/// contract that must stay in lockstep with the documented aze system. These
/// assertions pin every domain's slug and backing identity name.
/// </summary>
public class ElevationDomainTests
{
    [Theory]
    [InlineData(GraphDomain.Terraform, "terraform", "DevOps-Terraform")]
    [InlineData(GraphDomain.Devices, "devices", "DevOps-Devices")]
    [InlineData(GraphDomain.Identity, "identity", "DevOps-Identity")]
    [InlineData(GraphDomain.Systems, "systems", "DevOps-Systems")]
    [InlineData(GraphDomain.Cloud, "cloud", "DevOps-Cloud")]
    public void Domain_MapsToSlugAndIdentity(GraphDomain domain, string slug, string identity)
    {
        Assert.Equal(slug, domain.Slug());
        Assert.Equal(identity, domain.IdentityName());
    }

    [Fact]
    public void EveryDomain_HasNonEmptyDistinctSlugAndIdentity()
    {
        var domains = Enum.GetValues<GraphDomain>();
        var slugs = domains.Select(d => d.Slug()).ToList();
        var identities = domains.Select(d => d.IdentityName()).ToList();

        Assert.All(slugs, s => Assert.False(string.IsNullOrWhiteSpace(s)));
        Assert.All(identities, i => Assert.StartsWith("DevOps-", i));
        Assert.Equal(slugs.Count, slugs.Distinct().Count());
        Assert.Equal(identities.Count, identities.Distinct().Count());
    }
}
