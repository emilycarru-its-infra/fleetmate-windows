using FleetMate.Core.Config;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Deterministic coverage of FleetMateConfig: default values, path resolution,
/// computed properties, and YAML deserialization contract. No registry, no
/// files, no environment dependence.
/// </summary>
public class ConfigTests
{
    [Fact]
    public void Defaults_MatchDocumentedLayout()
    {
        var config = new FleetMateConfig();

        Assert.Equal("deployment", config.DeploymentPath);
        Assert.Equal("deployment/pkgsinfo", config.PkgsinfoPath);
        Assert.Equal("deployment/pkgs", config.PkgsPath);
        Assert.Equal("deployment/manifests", config.ManifestsPath);
        Assert.Equal("deployment/catalogs", config.CatalogsPath);
        Assert.Equal("quality", config.QualityPath);
        Assert.Equal("Information", config.LogLevel);
        Assert.Equal(5, config.CacheMinutes);
    }

    [Fact]
    public void ResolvePath_ReturnsRootedPathUnchanged()
    {
        var config = new FleetMateConfig { RepoRoot = @"C:\repo" };
        var rooted = @"C:\absolute\path";

        Assert.Equal(rooted, config.ResolvePath(rooted));
    }

    [Fact]
    public void ResolvePath_CombinesWithRepoRootWhenRelative()
    {
        var config = new FleetMateConfig { RepoRoot = @"C:\repo" };

        Assert.Equal(Path.Combine(@"C:\repo", "deployment"), config.ResolvePath("deployment"));
    }

    [Fact]
    public void ResolvePath_FallsBackToCurrentDirectoryWithoutRepoRoot()
    {
        var config = new FleetMateConfig { RepoRoot = null };

        var expected = Path.Combine(Directory.GetCurrentDirectory(), "quality");
        Assert.Equal(expected, config.ResolvePath("quality"));
    }

    [Fact]
    public void AzureDevOpsConfig_BaseUrl_IsBuiltFromOrganization()
    {
        var ado = new AzureDevOpsConfig { Organization = "emilycarru-its-infra" };

        Assert.Equal("https://dev.azure.com/emilycarru-its-infra", ado.BaseUrl);
    }

    [Fact]
    public void Yaml_Deserializes_CamelCaseIntoConfigGraph()
    {
        // Mirrors the DeserializerBuilder used by FleetMateConfig.Load().
        const string yaml = """
            reportMateUrl: https://reportmate.example
            cacheMinutes: 12
            graph:
              tenantId: tenant-123
              useAzureCliAuth: false
            azureDevOps:
              organization: contoso
              project: Devices
            unknownFutureKey: ignored
            """;

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var config = deserializer.Deserialize<FleetMateConfig>(yaml);

        Assert.NotNull(config);
        Assert.Equal("https://reportmate.example", config.ReportMateUrl);
        Assert.Equal(12, config.CacheMinutes);
        Assert.NotNull(config.Graph);
        Assert.Equal("tenant-123", config.Graph!.TenantId);
        Assert.False(config.Graph.UseAzureCliAuth);
        Assert.NotNull(config.AzureDevOps);
        Assert.Equal("contoso", config.AzureDevOps!.Organization);
        Assert.Equal("https://dev.azure.com/contoso", config.AzureDevOps.BaseUrl);
    }
}
