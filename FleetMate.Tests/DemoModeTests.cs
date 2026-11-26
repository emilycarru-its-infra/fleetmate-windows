using FleetMate.Core.Config;
using Xunit;

namespace FleetMate.Tests;

/// <summary>Demo mode, which keeps screenshots in a public repository to fictional data.</summary>
public class DemoModeTests
{
    [Theory]
    [InlineData(new[] { "app.exe", "--demo" }, null, true)]
    [InlineData(new[] { "app.exe", "--capture", "out" }, null, true)]
    [InlineData(new[] { "app.exe", "--CAPTURE", "out" }, null, true)]
    [InlineData(new[] { "app.exe" }, "1", true)]
    [InlineData(new[] { "app.exe" }, " 1 ", true)]
    [InlineData(new[] { "app.exe" }, null, false)]
    [InlineData(new[] { "app.exe" }, "0", false)]
    [InlineData(new[] { "app.exe", "--theme", "dark" }, "", false)]
    public void TurnsOnForDemoCaptureOrTheEnvironment(string[] args, string? env, bool expected) =>
        Assert.Equal(expected, DemoMode.IsRequested(args, env));

    [Fact]
    public void CapturesNeedDemoModeAndNoLiveSession()
    {
        Assert.Null(DemoMode.CaptureRefusal(demoMode: true, liveSession: false));
        Assert.NotNull(DemoMode.CaptureRefusal(demoMode: false, liveSession: false));
        Assert.NotNull(DemoMode.CaptureRefusal(demoMode: true, liveSession: true));
        Assert.NotNull(DemoMode.CaptureRefusal(demoMode: false, liveSession: true));
    }
}
