using FleetMate.Core.Models.Manage;
using FleetMate.Core.Models.Reporting;
using FleetMate.Core.Services.Manage;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The scanner resolves through inventory first, DNS second, and calls a
/// machine online only when SSH or RDP actually answer. Fakes stand in for
/// ReportMate and the network; nothing here opens a socket.
/// </summary>
public class HostScannerTests
{
    private sealed class FakeDirectory : IDeviceDirectory
    {
        public List<Device> Devices { get; } = new();
        public Dictionary<string, (string? ip, DateTime? at)> Addresses { get; } = new();
        public int AddressCalls;
        public bool Throw;

        public Task<List<Device>> GetDevicesAsync(CancellationToken cancellationToken)
        {
            if (Throw) throw new HttpRequestException("inventory down");
            return Task.FromResult(Devices);
        }

        public Task<(string? ip, DateTime? collectedAt)> GetAddressAsync(string serial, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref AddressCalls);
            return Task.FromResult(Addresses.TryGetValue(serial, out var a) ? a : (null, null));
        }
    }

    private sealed class FakeProbe : IReachabilityProbe
    {
        public Dictionary<string, string> Dns { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> OpenSsh { get; } = new();
        public HashSet<string> OpenRdp { get; } = new();

        public Task<string?> ResolveAsync(string hostname, CancellationToken cancellationToken) =>
            Task.FromResult(Dns.TryGetValue(hostname, out var ip) ? ip : null);

        public Task<bool> IsTcpOpenAsync(string ip, int port, CancellationToken cancellationToken) =>
            Task.FromResult(port == HostScanner.SshPort ? OpenSsh.Contains(ip) : OpenRdp.Contains(ip));
    }

    private static RosterComputer Machine(string serial, string host) =>
        new() { Serial = serial, Hostname = host, Status = "Active" };

    [Fact]
    public async Task Inventory_WinsOverDns_AndProbeDecidesOnline()
    {
        var dir = new FakeDirectory();
        dir.Devices.Add(new Device { SerialNumber = "S1", IpAddress = "10.0.0.1", LastSeen = DateTime.UtcNow });
        dir.Devices.Add(new Device { SerialNumber = "S2", LastSeen = DateTime.UtcNow });
        dir.Addresses["S2"] = ("10.0.0.2", DateTime.UtcNow.AddHours(-2));
        var probe = new FakeProbe();
        probe.Dns["HOST-1"] = "10.9.9.9";   // must not be used: inventory already answered
        probe.Dns["HOST-3"] = "10.0.0.3";
        probe.OpenSsh.Add("10.0.0.1");
        probe.OpenRdp.Add("10.0.0.3");

        var scanner = new HostScanner(dir, probe);
        var (results, summary) = await scanner.ScanAsync(
            new[] { Machine("S1", "HOST-1"), Machine("S2", "HOST-2"), Machine("S3", "HOST-3"), Machine("S4", "HOST-4") },
            null, null, CancellationToken.None);

        Assert.Equal("10.0.0.1", results["S1"].Ip);
        Assert.Equal(AddressSource.ReportMate, results["S1"].Source);
        Assert.Equal(HostState.Online, results["S1"].State);
        Assert.True(results["S1"].SshOpen);

        Assert.Equal("10.0.0.2", results["S2"].Ip);
        Assert.Equal(HostState.Unreachable, results["S2"].State);
        Assert.False(results["S2"].AddressIsStale);

        Assert.Equal(AddressSource.Dns, results["S3"].Source);
        Assert.Equal(HostState.Online, results["S3"].State);
        Assert.True(results["S3"].RdpOpen);

        Assert.Equal(HostState.Unresolved, results["S4"].State);

        Assert.Equal(ScanMode.ReportMate, summary.Mode);
        Assert.Equal(2, summary.FromReportMate);
        Assert.Equal(1, summary.FromDns);
        Assert.Equal(3, summary.Resolved);
        Assert.Equal(2, summary.Online);
        Assert.Equal(1, dir.AddressCalls);
    }

    [Fact]
    public async Task StaleInventoryRecords_AreNotAskedForAnAddress()
    {
        var dir = new FakeDirectory();
        dir.Devices.Add(new Device { SerialNumber = "S1", LastSeen = DateTime.UtcNow.AddDays(-60) });
        dir.Addresses["S1"] = ("10.0.0.1", DateTime.UtcNow.AddDays(-60));
        var probe = new FakeProbe();
        probe.Dns["HOST-1"] = "10.0.0.5";

        var (results, _) = await new HostScanner(dir, probe).ScanAsync(new[] { Machine("S1", "HOST-1") }, null, null, CancellationToken.None);

        Assert.Equal(0, dir.AddressCalls);
        Assert.Equal("10.0.0.5", results["S1"].Ip);
        Assert.Equal(AddressSource.Dns, results["S1"].Source);
    }

    [Fact]
    public async Task OldAddress_IsFlaggedStale()
    {
        var dir = new FakeDirectory();
        dir.Devices.Add(new Device { SerialNumber = "S1", LastSeen = DateTime.UtcNow });
        dir.Addresses["S1"] = ("10.0.0.1", DateTime.UtcNow.AddDays(-3));

        var (results, _) = await new HostScanner(dir, new FakeProbe()).ScanAsync(new[] { Machine("S1", "HOST-1") }, null, null, CancellationToken.None);

        Assert.True(results["S1"].AddressIsStale);
        Assert.True(results["S1"].AddressAge > TimeSpan.FromDays(2));
    }

    [Fact]
    public async Task InventoryDown_FallsBackToDns()
    {
        var dir = new FakeDirectory { Throw = true };
        var probe = new FakeProbe();
        probe.Dns["HOST-1"] = "10.0.0.1";
        probe.OpenSsh.Add("10.0.0.1");

        var (results, summary) = await new HostScanner(dir, probe).ScanAsync(new[] { Machine("S1", "HOST-1") }, null, null, CancellationToken.None);

        Assert.Equal(HostState.Online, results["S1"].State);
        Assert.Equal(ScanMode.DnsOnly, summary.Mode);
        Assert.False(summary.ReportMateAvailable);
    }

    [Fact]
    public async Task NothingResolves_IsLimited()
    {
        var (results, summary) = await new HostScanner(null, new FakeProbe()).ScanAsync(new[] { Machine("S1", "HOST-1") }, null, null, CancellationToken.None);
        Assert.Equal(HostState.Unresolved, results["S1"].State);
        Assert.Equal(ScanMode.Limited, summary.Mode);
    }

    [Fact]
    public async Task StoredAddresses_AreProbedAndKeptEvenWhenSilent()
    {
        var probe = new FakeProbe();
        var adhoc = RosterComputer.Adhoc("", "10.0.0.7");
        var stored = new Dictionary<string, string> { [adhoc.Serial] = "10.0.0.7" };

        var (results, _) = await new HostScanner(null, probe).ScanAsync(new[] { adhoc }, stored, null, CancellationToken.None);

        Assert.Equal("10.0.0.7", results[adhoc.Serial].Ip);
        Assert.Equal(AddressSource.Stored, results[adhoc.Serial].Source);
        Assert.Equal(HostState.Unreachable, results[adhoc.Serial].State);
    }

    [Fact]
    public async Task MachineWithoutHostname_ResolvesBySerialThroughInventory()
    {
        var dir = new FakeDirectory();
        dir.Devices.Add(new Device { SerialNumber = "S9", IpAddress = "10.0.0.9", LastSeen = DateTime.UtcNow });
        var probe = new FakeProbe();
        probe.OpenRdp.Add("10.0.0.9");
        var noHost = new RosterComputer { Serial = "S9", Allocation = "Spare 01", Status = "Active" };

        var (results, _) = await new HostScanner(dir, probe).ScanAsync(new[] { noHost }, null, null, CancellationToken.None);

        Assert.Equal(HostState.Online, results["S9"].State);
    }

    [Fact]
    public async Task InventoryMatchesByHostnameWhenSerialDiffers()
    {
        var dir = new FakeDirectory();
        dir.Devices.Add(new Device { SerialNumber = "OTHER", Hostname = "HOST-1", IpAddress = "10.0.0.1", LastSeen = DateTime.UtcNow });

        var (results, _) = await new HostScanner(dir, new FakeProbe()).ScanAsync(new[] { Machine("S1", "host-1") }, null, null, CancellationToken.None);

        Assert.Equal("10.0.0.1", results["S1"].Ip);
        Assert.Equal(AddressSource.ReportMate, results["S1"].Source);
    }

    [Fact]
    public async Task Cancellation_StopsTheScan()
    {
        var dir = new FakeDirectory();
        dir.Devices.Add(new Device { SerialNumber = "S1", IpAddress = "10.0.0.1", LastSeen = DateTime.UtcNow });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HostScanner(dir, new FakeProbe()).ScanAsync(new[] { Machine("S1", "HOST-1") }, null, null, cts.Token));
    }

    [Fact]
    public async Task Rescan_ReusesKnownAddressOrResolves()
    {
        var probe = new FakeProbe();
        probe.Dns["HOST-1"] = "10.0.0.1";
        probe.OpenSsh.Add("10.0.0.1");
        probe.OpenSsh.Add("10.0.0.2");
        var scanner = new HostScanner(null, probe);

        var resolved = await scanner.RescanAsync(Machine("S1", "HOST-1"), null, CancellationToken.None);
        Assert.Equal("10.0.0.1", resolved.Ip);
        Assert.Equal(HostState.Online, resolved.State);

        var known = await scanner.RescanAsync(Machine("S2", "HOST-2"), "10.0.0.2", CancellationToken.None);
        Assert.Equal(AddressSource.Stored, known.Source);
        Assert.True(known.SshOpen);
    }

    [Fact]
    public void ScanMode_Labels()
    {
        Assert.Equal("ReportMate active", ScanMode.ReportMate.Label());
        Assert.Equal("DNS only", ScanMode.DnsOnly.Label());
        Assert.Equal("Limited connectivity", ScanMode.Limited.Label());
        Assert.Equal("No scan yet", ScanMode.Unknown.Label());
    }
}
