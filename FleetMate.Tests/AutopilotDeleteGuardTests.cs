using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services.Devices;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// `fleetmate autopilot delete` refuses while Intune still holds the device,
/// and refuses when it can't find out, rather than reading a failed lookup as
/// "no record".
/// </summary>
public class AutopilotDeleteGuardTests
{
    private const string BoundId = "2b7c6f0e-1d3a-4c5b-9e8f-0a1b2c3d4e5f";

    private sealed class FakeGraph(Func<string, ManagedDeviceLookup> answer) : IAutopilotDeleteGraph
    {
        public List<string> Filters { get; } = new();

        public Task<ManagedDeviceLookup> LookupManagedDevicesAsync(string filter)
        {
            Filters.Add(filter);
            return Task.FromResult(answer(filter));
        }
    }

    private static AutopilotDevice Registration(string? serial = "ABC-123", string? managedDeviceId = null) =>
        new() { Id = "ap-1", SerialNumber = serial, ManagedDeviceId = managedDeviceId };

    private static ManagedDeviceLookup None => ManagedDeviceLookup.Found(Array.Empty<IntuneDevice>());

    [Fact]
    public async Task AllowsDelete_WhenIntuneHasNoRecord()
    {
        var graph = new FakeGraph(_ => None);

        Assert.Null(await new AutopilotDeleteGuard(graph).CheckAsync(Registration()));
        Assert.Equal(new[] { "serialNumber eq 'ABC-123'" }, graph.Filters);
    }

    [Fact]
    public async Task Refuses_WhileTheSerialStillHasAnIntuneRecord()
    {
        var graph = new FakeGraph(_ => ManagedDeviceLookup.Found(new[] { new IntuneDevice { Id = "md-1", DeviceName = "LAB-07" } }));

        var refusal = await new AutopilotDeleteGuard(graph).CheckAsync(Registration());

        Assert.NotNull(refusal);
        Assert.Contains("still has an Intune record", refusal);
        Assert.Contains("LAB-07", refusal);
    }

    [Fact]
    public async Task Refuses_WhenTheLookupFails_InsteadOfTreatingItAsNoRecord()
    {
        var graph = new FakeGraph(_ => ManagedDeviceLookup.Failed("503 service unavailable"));

        var refusal = await new AutopilotDeleteGuard(graph).CheckAsync(Registration());

        Assert.NotNull(refusal);
        Assert.Contains("failed", refusal);
        Assert.Contains("503", refusal);
    }

    [Fact]
    public async Task ChecksTheBoundIntuneId_AsWellAsTheSerial()
    {
        var graph = new FakeGraph(f => f.StartsWith("id eq")
            ? ManagedDeviceLookup.Found(new[] { new IntuneDevice { Id = BoundId } })
            : None);

        var refusal = await new AutopilotDeleteGuard(graph).CheckAsync(Registration(managedDeviceId: BoundId));

        Assert.NotNull(refusal);
        Assert.Equal(2, graph.Filters.Count);
        Assert.Contains($"id eq '{BoundId}'", graph.Filters);
    }

    [Fact]
    public async Task UsesTheBoundId_WhenTheRegistrationHasNoSerial()
    {
        var graph = new FakeGraph(_ => None);

        Assert.Null(await new AutopilotDeleteGuard(graph).CheckAsync(Registration(serial: null, managedDeviceId: BoundId)));
        Assert.Equal(new[] { $"id eq '{BoundId}'" }, graph.Filters);
    }

    [Fact]
    public async Task Refuses_WhenThereIsNothingToCheckWith()
    {
        var graph = new FakeGraph(_ => None);

        var refusal = await new AutopilotDeleteGuard(graph).CheckAsync(
            Registration(serial: " ", managedDeviceId: "00000000-0000-0000-0000-000000000000"));

        Assert.NotNull(refusal);
        Assert.Empty(graph.Filters);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("00000000-0000-0000-0000-000000000000", null)]
    [InlineData("not-a-guid", null)]
    [InlineData(BoundId, BoundId)]
    public void BoundManagedDeviceId_IgnoresEmptyAndZeroIds(string? raw, string? expected) =>
        Assert.Equal(expected, AutopilotDeleteGuard.BoundManagedDeviceId(raw));
}
