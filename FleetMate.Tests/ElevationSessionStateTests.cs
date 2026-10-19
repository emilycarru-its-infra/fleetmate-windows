using FleetMate.Core.Config;
using FleetMate.Core.Models;
using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

public class ElevationSessionStateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void NoContainerIsNone_UnlessThisProcessIsCreatingIt()
    {
        var missing = new ElevationSessionInfo(null, null);
        Assert.Equal(ElevationSessionState.None, missing.Classify(Now));
        Assert.Equal(ElevationSessionState.Starting, missing.Classify(Now, creating: true));
    }

    [Fact]
    public void RunningBeforeExpiryIsReady()
    {
        var info = new ElevationSessionInfo("Running", Now.ToUnixTimeSeconds() + 3600);
        Assert.Equal(ElevationSessionState.Ready, info.Classify(Now));
    }

    [Fact]
    public void RunningPastTheExpiresTagIsExpired()
    {
        // The container can lag its sleep by a moment; the tag is the contract.
        var info = new ElevationSessionInfo("Running", Now.ToUnixTimeSeconds() - 1);
        Assert.Equal(ElevationSessionState.Expired, info.Classify(Now));
    }

    [Fact]
    public void RunningWithNoTagIsReady()
    {
        Assert.Equal(ElevationSessionState.Ready, new ElevationSessionInfo("Running", null).Classify(Now));
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Waiting")]
    [InlineData("Creating")]
    public void BootingStatesAreStarting(string state)
    {
        Assert.Equal(ElevationSessionState.Starting, new ElevationSessionInfo(state, null).Classify(Now));
    }

    [Theory]
    [InlineData("Terminated")]
    [InlineData("Stopped")]
    [InlineData("Succeeded")]
    [InlineData("Failed")]
    public void FinishedContainersAreExpired(string state)
    {
        Assert.Equal(ElevationSessionState.Expired, new ElevationSessionInfo(state, null).Classify(Now));
    }

    [Fact]
    public void ParsesTheAzShowProjection()
    {
        var info = ElevationSession.ParseSessionInfo("{\"state\": \"Running\", \"expires\": \"1800003600\"}");
        Assert.Equal("Running", info.ContainerState);
        Assert.Equal(1800003600, info.ExpiresUnix);

        var untagged = ElevationSession.ParseSessionInfo("{\"state\": \"Terminated\", \"expires\": null}");
        Assert.Equal("Terminated", untagged.ContainerState);
        Assert.Null(untagged.ExpiresUnix);
    }

    [Fact]
    public void AggregateShowsWhatNeedsAttention()
    {
        Assert.Equal(ElevationSessionState.Ready,
            ElevationMonitor.Aggregate(new[] { ElevationSessionState.Ready, ElevationSessionState.Ready }));
        Assert.Equal(ElevationSessionState.Starting,
            ElevationMonitor.Aggregate(new[] { ElevationSessionState.Ready, ElevationSessionState.Starting }));
        Assert.Equal(ElevationSessionState.Expired,
            ElevationMonitor.Aggregate(new[] { ElevationSessionState.Ready, ElevationSessionState.Expired }));
        Assert.Equal(ElevationSessionState.Off,
            ElevationMonitor.Aggregate(new[] { ElevationSessionState.Off, ElevationSessionState.Off }));
    }

    [Fact]
    public void UnconfiguredElevationIsOff()
    {
        using var monitor = new ElevationMonitor(new ElevationConfig(), directTransport: false);
        Assert.False(monitor.Enabled);
        Assert.Equal(ElevationSessionState.Off, monitor.Overall());
    }

    [Fact]
    public void DirectTransportIsOff()
    {
        var config = new ElevationConfig
        {
            ResourceGroup = "rg", AcrImage = "img", TranscriptAccount = "acct", IdentityPrefix = "p-",
        };
        using var monitor = new ElevationMonitor(config, directTransport: true);
        Assert.Equal(ElevationSessionState.Off, monitor.Overall());
    }

    [Fact]
    public void TooltipSaysWhenItExpires()
    {
        var line = ElevationStatusText.DomainLine(GraphDomain.Devices, ElevationSessionState.Ready,
            Now.AddHours(5).AddMinutes(12), null, Now);
        Assert.Equal("Devices (Intune, Microsoft Graph): Ready, 5h 12m left", line);
    }

    [Theory]
    [InlineData(ElevationSessionState.Ready, ElevationDotTone.Green)]
    [InlineData(ElevationSessionState.Starting, ElevationDotTone.Blue)]
    [InlineData(ElevationSessionState.Expired, ElevationDotTone.Grey)]
    [InlineData(ElevationSessionState.None, ElevationDotTone.Grey)]
    [InlineData(ElevationSessionState.Unknown, ElevationDotTone.Grey)]
    public void ShieldDotStaysGreyWhenStopped(ElevationSessionState state, ElevationDotTone expected)
    {
        Assert.Equal(expected, ElevationStatusText.DotTone(state));
    }

    [Fact]
    public void StoppedSessionReadsAsRestartingNotAsAProblem()
    {
        Assert.Equal("Stopped", ElevationStatusText.StateLabel(ElevationSessionState.Expired));
        Assert.Equal("Stopped, restarts on next use",
            ElevationStatusText.Detail(ElevationSessionState.None, null, Now));
        Assert.Equal(AuthDisplayTone.Inactive, AuthDisplayStatus.Standby.Tone);
        Assert.Equal(AuthDisplayTone.Positive, AuthDisplayStatus.Ready.Tone);
        Assert.Equal("All valid", AuthDisplayStatus.Summary(new[] { AuthDisplayStatus.Ready, AuthDisplayStatus.Standby }).Label);
    }

    [Fact]
    public void PrewarmDefaultsOn()
    {
        Assert.True(new ElevationConfig().PrewarmOnLaunch);
    }

    [Theory]
    [InlineData(true, "", true)]
    [InlineData(true, "0", false)]
    [InlineData(true, "false", false)]
    [InlineData(false, "", false)]
    [InlineData(false, "1", false)]
    public void PrewarmEnvOverrideCanOnlyTurnItOff(bool configured, string env, bool expected)
    {
        Assert.Equal(expected,
            ElevationMonitor.PrewarmEnabled(new ElevationConfig { PrewarmOnLaunch = configured }, env));
    }
}
