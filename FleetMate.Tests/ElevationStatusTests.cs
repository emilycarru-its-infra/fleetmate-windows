using FleetMate.Core.Config;
using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// A failed elevated call and a genuine "no such record" both arrive at the
/// caller as null. These cover the distinction, because collapsing it reported
/// a fully enrolled machine as having no records at all — on a destructive
/// command, where it reads as "nothing to clean up".
/// </summary>
public class ElevationStatusTests
{
    [Fact]
    public void FreshStatus_HasNotFailed()
    {
        var status = new ElevationStatus();

        Assert.False(status.HasFailed);
        Assert.Equal(0, status.Failures);
        Assert.Null(status.LastError);
    }

    [Fact]
    public void RecordFailure_CountsAndKeepsTheMostRecentReason()
    {
        var status = new ElevationStatus();

        status.RecordFailure("first");
        status.RecordFailure("second");

        Assert.True(status.HasFailed);
        Assert.Equal(2, status.Failures);
        Assert.Equal("second", status.LastError);
    }

    /// <summary>
    /// A command must be able to tell whether ITS OWN reads failed, not whether
    /// anything failed earlier in the session — otherwise one unrelated failure
    /// would poison every later lookup.
    /// </summary>
    [Fact]
    public void FailedSince_IgnoresFailuresBeforeTheSnapshot()
    {
        var status = new ElevationStatus();
        status.RecordFailure("earlier, unrelated");

        var snapshot = status.Snapshot();
        Assert.False(status.FailedSince(snapshot));

        status.RecordFailure("during this lookup");
        Assert.True(status.FailedSince(snapshot));
    }

    [Fact]
    public void ConcurrentFailures_AreAllCounted()
    {
        var status = new ElevationStatus();

        Parallel.For(0, 200, i => status.RecordFailure($"call {i}"));

        Assert.Equal(200, status.Failures);
    }

    /// <summary>
    /// GraphService owns the status it hands to the transport; a caller must be
    /// able to consult it without reaching into the handler.
    /// </summary>
    [Fact]
    public void GraphService_ExposesAnElevationStatus()
    {
        using var graph = new GraphService(new GraphConfig());

        Assert.NotNull(graph.Elevation);
        Assert.False(graph.Elevation.HasFailed);
    }

    [Fact]
    public void DeviceRecordState_DefaultsToAReadableLookup()
    {
        var state = new GraphService.DeviceRecordState { Serial = "MJ0KP6EP" };

        Assert.False(state.LookupFailed);
        Assert.Null(state.LookupError);
    }

    /// <summary>
    /// The specific shape of the bug: no records AND no successful lookup. Every
    /// "is it clean?" property reads the same as a genuinely clean machine, so
    /// LookupFailed is the only thing separating them.
    /// </summary>
    [Fact]
    public void AnUnreadableState_IsIndistinguishableFromCleanExceptForLookupFailed()
    {
        var unreadable = new GraphService.DeviceRecordState
        {
            Serial = "MJ0KP6EP",
            LookupFailed = true,
            LookupError = "elevated devices call exited 1",
        };
        var genuinelyClean = new GraphService.DeviceRecordState { Serial = "MJ0KP6EP" };

        Assert.Null(unreadable.Intune);
        Assert.Null(genuinelyClean.Intune);
        Assert.Empty(unreadable.EntraDevices);
        Assert.Empty(genuinelyClean.EntraDevices);
        Assert.Equal(genuinelyClean.IsOrphaned, unreadable.IsOrphaned);

        Assert.True(unreadable.LookupFailed);
        Assert.False(genuinelyClean.LookupFailed);
    }

    /// <summary>
    /// Logs belong to the tool that writes them; they were being written into
    /// Cimian's directory, where nobody would look for FleetMate diagnostics.
    /// </summary>
    [Fact]
    public void LogPath_IsUnderTheManagedFleetRoot()
    {
        var config = new FleetMateConfig();

        // Every managed tool writes to %ProgramData%\Managed<Tool>\logs; resolved from
        // ProgramData rather than a hardcoded drive, which group policy can relocate.
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ManagedFleet", "logs"),
            config.LogPath);
    }

    [Fact]
    public void MigrateLegacyLogs_MovesHistoryOutOfEveryPreviousRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "fm-logs-" + Guid.NewGuid().ToString("N"));
        var oldA = Path.Combine(root, "FleetMate", "logs");
        var oldB = Path.Combine(root, "Cimian", "Logs");
        var destination = Path.Combine(root, "ManagedFleet", "logs");
        Directory.CreateDirectory(oldA);
        Directory.CreateDirectory(oldB);
        File.WriteAllText(Path.Combine(oldA, "fleetmate-20260812.log"), "a");
        File.WriteAllText(Path.Combine(oldB, "fleetmate-20260824_001.log"), "b");
        File.WriteAllText(Path.Combine(oldB, "unrelated.txt"), "not ours");

        try
        {
            var moved = FleetMateConfig.MigrateLegacyLogs(destination, new[] { oldA, oldB });

            Assert.Equal(2, moved);
            Assert.True(File.Exists(Path.Combine(destination, "fleetmate-20260812.log")));
            Assert.True(File.Exists(Path.Combine(destination, "fleetmate-20260824_001.log")));
            // Only our own logs move; anything else in a shared directory is left alone.
            Assert.True(File.Exists(Path.Combine(oldB, "unrelated.txt")));
            // Safe to run again.
            Assert.Equal(0, FleetMateConfig.MigrateLegacyLogs(destination, new[] { oldA, oldB }));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void MigrateLegacyLogs_KeepsTheExistingFileWhenBothSidesHaveTheName()
    {
        var root = Path.Combine(Path.GetTempPath(), "fm-logs-" + Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(root, "old");
        var destination = Path.Combine(root, "new");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(legacy, "fleetmate-20260812.log"), "legacy");
        File.WriteAllText(Path.Combine(destination, "fleetmate-20260812.log"), "current");

        try
        {
            Assert.Equal(0, FleetMateConfig.MigrateLegacyLogs(destination, new[] { legacy }));
            Assert.Equal("current", File.ReadAllText(Path.Combine(destination, "fleetmate-20260812.log")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
