using FleetMate.Core.Models.Manage;
using FleetMate.Core.Services.Manage;
using Xunit;

namespace FleetMate.Tests;

public class ManageStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fleetmate-manage-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void History_RoundTripsNewestFirstAndTrims()
    {
        var store = new ManageStateStore(_root);
        var history = store.LoadHistory();
        Assert.Empty(history);

        for (var i = 0; i < ManageStateStore.HistoryLimit + 5; i++)
            history = store.AddHistory(history, $"cmd {i}", $"echo {i}");

        Assert.Equal(ManageStateStore.HistoryLimit, history.Count);
        Assert.Equal($"cmd {ManageStateStore.HistoryLimit + 4}", history[0].Label);

        var reloaded = new ManageStateStore(_root).LoadHistory();
        Assert.Equal(history.Select(h => h.Command), reloaded.Select(h => h.Command));

        store.ClearHistory();
        Assert.Empty(new ManageStateStore(_root).LoadHistory());
    }

    [Fact]
    public void CustomGroups_RoundTripWithDevices()
    {
        var store = new ManageStateStore(_root);
        var group = new CustomGroup("Front desk");
        group.Devices.Add(new AdhocDevice("DESK-01", "10.0.0.10"));
        group.Devices.Add(new AdhocDevice("", "10.0.0.11"));
        group.Devices.Add(new AdhocDevice("LAB-05", "", serial: "S005"));
        store.SaveCustomGroups(new[] { group });

        var loaded = new ManageStateStore(_root).LoadCustomGroups();
        Assert.Single(loaded);
        Assert.Equal(group.Id, loaded[0].Id);
        Assert.Equal("Front desk", loaded[0].Name);
        Assert.Equal(3, loaded[0].Devices.Count);
        Assert.Equal("10.0.0.11", loaded[0].Devices[1].Hostname);
        Assert.Equal("S005", loaded[0].Devices[2].Serial);
        Assert.Equal("S005", loaded[0].Devices[2].Computer.Serial);
        Assert.True(loaded[0].Devices[0].Computer.IsAdhoc);
    }

    [Fact]
    public void CorruptFile_StartsEmptyInsteadOfThrowing()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "history.json"), "{ not json");
        Assert.Empty(new ManageStateStore(_root).LoadHistory());
    }

    [Fact]
    public void DefaultRoot_IsUnderLocalAppData()
    {
        Assert.EndsWith(Path.Combine("FleetMate", "manage"), ManageStateStore.DefaultRoot);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ManageStateStore.DefaultRoot);
    }
}
