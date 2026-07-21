using FleetMate.Core.Config;
using FleetMate.Core.Services.Devices;
using FleetMate.Core.Services.Projects;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Verifies the QA-outcome -> board State mapping used when syncing package
/// readiness to Azure DevOps. StateFor is pure (no network), so it exercises the
/// config-driven mapping without touching the ADO service.
/// </summary>
public class PackageReadinessServiceTests
{
    private static PackageReadinessService Make(PackageReadinessConfig cfg)
    {
        // AzureDevOpsService is required by ctor but unused by StateFor.
        var ado = new AzureDevOpsService(new AzureDevOpsConfig { Organization = "org", Project = "proj" });
        return new PackageReadinessService(ado, cfg, "proj");
    }

    [Theory]
    [InlineData(ChecklistStatus.Passed, "Done")]
    [InlineData(ChecklistStatus.Warning, "Doing")]
    [InlineData(ChecklistStatus.Failed, "Planned")]
    [InlineData(ChecklistStatus.Untested, "Planned")]
    public void StateFor_MapsDefaultStates(ChecklistStatus status, string expected)
    {
        var svc = Make(new PackageReadinessConfig());
        Assert.Equal(expected, svc.StateFor(status));
    }

    [Fact]
    public void StateFor_HonorsCustomStateNames()
    {
        var cfg = new PackageReadinessConfig
        {
            StatePassed = "Closed",
            StateWarning = "Active",
            StateFailed = "New",
            StateUntested = "New",
        };
        var svc = Make(cfg);
        Assert.Equal("Closed", svc.StateFor(ChecklistStatus.Passed));
        Assert.Equal("Active", svc.StateFor(ChecklistStatus.Warning));
        Assert.Equal("New", svc.StateFor(ChecklistStatus.Failed));
        Assert.Equal("New", svc.StateFor(ChecklistStatus.Untested));
    }

    [Fact]
    public void Defaults_AreEnabledIssueWithReadinessTag()
    {
        var cfg = new PackageReadinessConfig();
        Assert.True(cfg.Enabled);
        Assert.Equal("Issue", cfg.WorkItemType);
        Assert.Equal("PackageReadiness", cfg.Tag);
        Assert.Equal("[Package Readiness]", cfg.TitlePrefix);
    }
}
