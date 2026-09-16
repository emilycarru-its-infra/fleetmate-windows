using FleetMate.Core.Models.Devices;
using Xunit;

namespace FleetMate.Tests;

public class WindowsUpdateInventoryTests
{
    private static readonly DateTime Cutoff = new(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CalculatesCoverageAcrossRecentlySyncedWindowsDevices()
    {
        var inventory = WindowsUpdateInventoryBuilder.Build(
            [
                Device("A", "10.0.26100.9457", Cutoff.AddHours(1)),
                Device("B", "10.0.26200.9457", Cutoff.AddHours(2)),
                Device("C", "10.0.26100.9000", Cutoff.AddHours(3)),
                Device("Old", "10.0.26100.9457", Cutoff.AddSeconds(-1)),
                Device("Mac", "15.6", Cutoff.AddHours(1), "macOS"),
            ],
            Cutoff,
            ["26100.9457", "26200.9457"]);

        Assert.Equal(3, inventory.TotalDevices);
        Assert.Equal(2, inventory.MatchingDevices);
        Assert.Equal(66.7, inventory.CoveragePercentage);
        Assert.Equal(["A", "B"], inventory.Devices.Select(device => device.DeviceName));
    }

    [Fact]
    public void NormalizesTheGraphOsVersionToTheWindowsBuild()
    {
        Assert.Equal("26100.9457", WindowsUpdateInventoryBuilder.NormalizeBuild("10.0.26100.9457"));
        Assert.Equal("26100.9457", WindowsUpdateInventoryBuilder.NormalizeBuild("26100.9457"));
    }

    [Fact]
    public void SummarizesEveryObservedBuildWithoutInferringKbIdentity()
    {
        var inventory = WindowsUpdateInventoryBuilder.Build(
            [
                Device("A", "10.0.26100.9457", Cutoff),
                Device("B", "10.0.26100.9457", Cutoff),
                Device("C", "10.0.26200.9448", Cutoff),
            ],
            Cutoff);

        Assert.Equal(3, inventory.MatchingDevices);
        Assert.Collection(
            inventory.Builds,
            build => Assert.Equal(new WindowsBuildCount("26100.9457", 2, 66.7), build),
            build => Assert.Equal(new WindowsBuildCount("26200.9448", 1, 33.3), build));
        Assert.DoesNotContain("KB", string.Join(' ', inventory.Builds.Select(build => build.Build)));
    }

    private static IntuneDevice Device(string name, string version, DateTime sync, string os = "Windows") => new()
    {
        DeviceName = name,
        SerialNumber = $"SERIAL-{name}",
        OperatingSystem = os,
        OsVersion = version,
        LastSyncDateTime = sync,
    };
}
