using FleetMate.Core.Config;
using FleetMate.Core.Services.Devices;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Installer-type packages (MSI/EXE in payload/) must have an EMPTY
/// install_location. Verifies the native check flags/fixes a non-empty one and
/// passes a correctly-empty one, matching control.ps1's Test-InstallerTypePackages.
/// </summary>
public class InstallerMaintenanceTests
{
    private static (FleetMateConfig cfg, string root) MakePackage(string installLocationLine, bool withInstaller = true)
    {
        var root = Path.Combine(Path.GetTempPath(), $"fm-inst-{Guid.NewGuid():N}");
        var pkgDir = Path.Combine(root, "installers", "Foo");
        var payload = Path.Combine(pkgDir, "payload");
        Directory.CreateDirectory(payload);
        if (withInstaller) File.WriteAllText(Path.Combine(payload, "setup.exe"), "MZ");
        File.WriteAllText(Path.Combine(pkgDir, "build-info.yaml"),
            "product:\n  name: Foo\n  version: 1.0.0\n" + installLocationLine);

        var cfg = new FleetMateConfig { RepoRoot = root, PackagesPath = "packages", InstallersPath = "installers" };
        return (cfg, root);
    }

    [Fact]
    public void NonEmptyInstallLocation_IsFlagged()
    {
        var (cfg, root) = MakePackage("  install_location: C:\\Program Files\\Foo\n");
        try
        {
            var result = new InstallerMaintenanceService(cfg).CheckInstallerTypes(fix: false);
            Assert.Equal(1, result.Total);
            Assert.Equal(0, result.Passed);
            Assert.Single(result.Issues);
            Assert.False(result.Issues[0].Fixed);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void EmptyInstallLocation_Passes()
    {
        var (cfg, root) = MakePackage("  install_location:\n");
        try
        {
            var result = new InstallerMaintenanceService(cfg).CheckInstallerTypes(fix: false);
            Assert.Equal(1, result.Total);
            Assert.Equal(1, result.Passed);
            Assert.Empty(result.Issues);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Fix_EmptiesInstallLocation()
    {
        var (cfg, root) = MakePackage("  install_location: C:\\Program Files\\Foo\n");
        try
        {
            var svc = new InstallerMaintenanceService(cfg);
            var fixResult = svc.CheckInstallerTypes(fix: true);
            Assert.Equal(1, fixResult.Fixed);

            // Re-check: now it should pass.
            var recheck = svc.CheckInstallerTypes(fix: false);
            Assert.Equal(1, recheck.Passed);
            Assert.Empty(recheck.Issues);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void NonInstallerPackage_IsIgnored()
    {
        var (cfg, root) = MakePackage("  install_location: C:\\somewhere\n", withInstaller: false);
        try
        {
            var result = new InstallerMaintenanceService(cfg).CheckInstallerTypes(fix: false);
            Assert.Equal(0, result.Total); // no MSI/EXE in payload -> not installer-type
        }
        finally { Directory.Delete(root, true); }
    }
}
