using FleetMate.Commands.Devices;
using FleetMate.Commands.Shared;
using FleetMate.Core.Config;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Minimal smoke coverage proving the test harness runs and the CLI's
/// credential-free command factories construct a valid command tree.
/// Broader deterministic coverage lives in the dedicated test classes.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void TestHarness_Runs()
    {
        Assert.True(true);
    }

    [Fact]
    public void QaCommand_BuildsNamedCommand()
    {
        var config = new FleetMateConfig();
        var command = QaCommand.Create(config);

        Assert.NotNull(command);
        Assert.Equal("qa", command.Name);
    }

    [Fact]
    public void ConfigureCommand_BuildsNamedCommand()
    {
        var config = new FleetMateConfig();
        var command = ConfigureCommand.Create(config);

        Assert.NotNull(command);
        Assert.Equal("configure", command.Name);
    }
}
