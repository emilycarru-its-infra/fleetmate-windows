using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FleetMate.Core.Models.Manage;
using FleetMate.Core.Models.Reporting;
using FleetMate.Core.Services.Reporting;
using Serilog;

namespace FleetMate.Core.Services.Manage;

/// <summary>Inventory lookups the scanner needs; ReportMate in production, a fake in tests.</summary>
public interface IDeviceDirectory
{
    Task<List<Device>> GetDevicesAsync(CancellationToken cancellationToken);
    Task<(string? ip, DateTime? collectedAt)> GetAddressAsync(string serial, CancellationToken cancellationToken);
    /// <summary>
    /// Every address the inventory knows in one call, keyed by upper-cased
    /// serial. The default is empty so a directory that has no bulk report
    /// falls through to <see cref="GetAddressAsync"/> per machine.
    /// </summary>
    Task<Dictionary<string, (string ip, DateTime? collectedAt)>> GetAddressesAsync(CancellationToken cancellationToken)
        => Task.FromResult(new Dictionary<string, (string ip, DateTime? collectedAt)>());
}

/// <summary>Network checks the scanner needs; real sockets in production, a fake in tests.</summary>
public interface IReachabilityProbe
{
    Task<string?> ResolveAsync(string hostname, CancellationToken cancellationToken);
    Task<bool> IsTcpOpenAsync(string ip, int port, CancellationToken cancellationToken);
}

public class ReportMateDeviceDirectory : IDeviceDirectory
{
    private readonly ReportMateService _reportMate;

    /// <summary>
    /// The fleet map answers within this or the scan moves on without it. The
    /// shared HttpClient's own timeout is far longer; this stops the wait, not
    /// the request, so a slow inventory cannot stall the whole scan.
    /// </summary>
    public static readonly TimeSpan FleetMapTimeout = TimeSpan.FromSeconds(45);

    public ReportMateDeviceDirectory(ReportMateService reportMate) => _reportMate = reportMate;

    public Task<List<Device>> GetDevicesAsync(CancellationToken cancellationToken) => _reportMate.GetDevicesAsync();

    public async Task<(string? ip, DateTime? collectedAt)> GetAddressAsync(string serial, CancellationToken cancellationToken)
    {
        var info = await _reportMate.GetDeviceNetworkAsync(serial);
        return (info?.PrimaryIpv4, info?.CollectedAt);
    }

    public async Task<Dictionary<string, (string ip, DateTime? collectedAt)>> GetAddressesAsync(CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, (string ip, DateTime? collectedAt)>();
        foreach (var (serial, row) in await _reportMate.GetFleetAddressesAsync().WaitAsync(FleetMapTimeout, cancellationToken))
        {
            if (row.PrimaryIp is { Length: > 0 } ip)
                map[serial] = (ip, row.NetworkInfo?.CollectedAt);
        }
        return map;
    }
}

public class NetworkReachabilityProbe : IReachabilityProbe
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);
    public TimeSpan ResolveTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public async Task<string?> ResolveAsync(string hostname, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hostname)) return null;
        if (IPAddress.TryParse(hostname, out var literal)) return literal.ToString();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ResolveTimeout);
            var addresses = await Dns.GetHostAddressesAsync(hostname, AddressFamily.InterNetwork, cts.Token);
            return addresses.FirstOrDefault(a => !IPAddress.IsLoopback(a))?.ToString();
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return null;
        }
    }

    public async Task<bool> IsTcpOpenAsync(string ip, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(IPAddress.Parse(ip), port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Resolves a set of roster machines to addresses and checks whether SSH
/// or RDP answer. Inventory first (one bulk call, then per-device network
/// modules for the machines the room needs), DNS for whatever inventory
/// does not know, and a TCP probe of 22 and 3389 for every address. ICMP is
/// not used: it is filtered on parts of the fleet network, so a ping says
/// nothing about whether a machine can be managed.
/// </summary>
public class HostScanner
{
    public const int SshPort = 22;
    public const int RdpPort = 3389;

    private readonly IDeviceDirectory? _directory;
    private readonly IReachabilityProbe _probe;

    /// <summary>Inventory records older than this are not asked for an address; the machine is probably off.</summary>
    public TimeSpan InventoryFreshness { get; init; } = TimeSpan.FromDays(14);

    public int Concurrency { get; init; } = 16;

    public HostScanner(IDeviceDirectory? directory, IReachabilityProbe? probe = null)
    {
        _directory = directory;
        _probe = probe ?? new NetworkReachabilityProbe();
    }

    /// <summary>
    /// Scan the given machines. <paramref name="storedAddresses"/> supplies
    /// addresses remembered from an earlier scan (custom groups); they are
    /// probed directly and kept even when unreachable so RDP remains a click away.
    /// Progress reports a short status line.
    /// </summary>
    public async Task<(Dictionary<string, HostScanResult> results, ScanSummary summary)> ScanAsync(
        IReadOnlyList<RosterComputer> computers,
        IReadOnlyDictionary<string, string>? storedAddresses,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var results = new Dictionary<string, HostScanResult>();
        var candidates = new Dictionary<string, (string ip, AddressSource source, DateTime? collectedAt)>();

        // Stored addresses first: they are the operator's own knowledge.
        if (storedAddresses != null)
        {
            foreach (var c in computers)
            {
                if (storedAddresses.TryGetValue(c.Serial, out var ip) && !string.IsNullOrWhiteSpace(ip))
                    candidates[c.Serial] = (ip, AddressSource.Stored, null);
            }
        }

        // Inventory: bulk list, then the network module for each machine that needs an address.
        var reportMateAvailable = false;
        var fromReportMate = 0;
        if (_directory != null)
        {
            progress?.Report("Checking ReportMate...");
            List<Device> devices;
            try
            {
                devices = await _directory.GetDevicesAsync(cancellationToken);
                reportMateAvailable = devices.Count > 0;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ReportMate device list unavailable; scanning by DNS only");
                devices = new List<Device>();
            }

            if (reportMateAvailable)
            {
                var bySerial = devices
                    .Where(d => !string.IsNullOrEmpty(d.SerialNumber))
                    .GroupBy(d => d.SerialNumber.ToUpperInvariant())
                    .ToDictionary(g => g.Key, g => g.First());
                var byHost = devices
                    .Where(d => !string.IsNullOrEmpty(d.Hostname) || !string.IsNullOrEmpty(d.DeviceName))
                    .GroupBy(d => (d.Hostname is { Length: > 0 } h ? h : d.DeviceName).ToUpperInvariant())
                    .ToDictionary(g => g.Key, g => g.First());

                // The fleet network report is one request for every address the
                // inventory knows; the per-device module is the fallback for the
                // machines it does not cover.
                Dictionary<string, (string ip, DateTime? collectedAt)> fleetAddresses;
                try
                {
                    progress?.Report("ReportMate: fetching fleet addresses...");
                    fleetAddresses = await _directory.GetAddressesAsync(cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Debug(ex, "Fleet network report unavailable; falling back to per-device lookups");
                    fleetAddresses = new Dictionary<string, (string ip, DateTime? collectedAt)>();
                }

                var pending = new List<(RosterComputer computer, Device device)>();
                foreach (var c in computers.Where(c => !candidates.ContainsKey(c.Serial) && !c.IsAdhoc))
                {
                    Device? device = null;
                    if (bySerial.TryGetValue(c.Serial.ToUpperInvariant(), out var bySer)) device = bySer;
                    else if (c.HasHostname && byHost.TryGetValue(c.Hostname.ToUpperInvariant(), out var byName)) device = byName;
                    if (device == null) continue;

                    if (device.LastSeen.HasValue && DateTime.UtcNow - device.LastSeen.Value.ToUniversalTime() > InventoryFreshness)
                        continue;

                    if (!string.IsNullOrWhiteSpace(device.IpAddress) && IPAddress.TryParse(device.IpAddress, out _))
                        candidates[c.Serial] = (device.IpAddress, AddressSource.ReportMate, device.CollectedAt);
                    else if (fleetAddresses.TryGetValue(device.SerialNumber.ToUpperInvariant(), out var known) && IPAddress.TryParse(known.ip, out _))
                        candidates[c.Serial] = (known.ip, AddressSource.ReportMate, known.collectedAt ?? device.CollectedAt);
                    else
                        pending.Add((c, device));
                }

                if (pending.Count > 0)
                {
                    progress?.Report($"ReportMate: fetching addresses for {pending.Count} machines...");
                    await RunThrottled(pending, async item =>
                    {
                        try
                        {
                            var (ip, collectedAt) = await _directory.GetAddressAsync(item.device.SerialNumber, cancellationToken);
                            if (!string.IsNullOrWhiteSpace(ip) && IPAddress.TryParse(ip, out _))
                            {
                                lock (candidates) candidates[item.computer.Serial] = (ip!, AddressSource.ReportMate, collectedAt ?? item.device.CollectedAt);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Log.Debug(ex, "Network module unavailable for {Serial}", item.device.SerialNumber);
                        }
                    }, cancellationToken);
                }

                fromReportMate = candidates.Count(kv => kv.Value.source == AddressSource.ReportMate);
                progress?.Report($"ReportMate matched {fromReportMate}/{computers.Count}...");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // DNS for the rest.
        var needDns = computers.Where(c => !candidates.ContainsKey(c.Serial) && c.HasHostname).ToList();
        if (needDns.Count > 0)
        {
            progress?.Report($"Resolving {needDns.Count} hostnames...");
            await RunThrottled(needDns, async c =>
            {
                var ip = await _probe.ResolveAsync(c.Hostname, cancellationToken);
                if (ip != null)
                {
                    lock (candidates) candidates[c.Serial] = (ip, AddressSource.Dns, null);
                }
            }, cancellationToken);
        }
        var fromDns = candidates.Count(kv => kv.Value.source == AddressSource.Dns);

        cancellationToken.ThrowIfCancellationRequested();

        // Probe every candidate for SSH and RDP.
        progress?.Report($"Probing {candidates.Count} addresses...");
        var probed = new Dictionary<string, HostScanResult>();
        await RunThrottled(candidates.ToList(), async kv =>
        {
            var result = await ProbeAddressAsync(kv.Key, kv.Value.ip, kv.Value.source, kv.Value.collectedAt, cancellationToken);
            lock (probed) probed[kv.Key] = result;
        }, cancellationToken);

        // An inventory address can be a DHCP lease behind: when it answered on
        // neither port, resolve the name again and probe the fresh address before
        // calling the machine offline. The same address back keeps the inventory
        // result; a different one replaces it.
        var machineBySerial = computers.GroupBy(c => c.Serial).ToDictionary(g => g.Key, g => g.First());
        var silent = probed.Values
            .Where(r => r.Source == AddressSource.ReportMate && r.State == HostState.Unreachable
                        && machineBySerial.TryGetValue(r.Serial, out var c) && c.HasHostname)
            .ToList();
        if (silent.Count > 0)
        {
            progress?.Report($"Re-resolving {silent.Count} silent addresses...");
            await RunThrottled(silent, async stale =>
            {
                var fresh = await _probe.ResolveAsync(machineBySerial[stale.Serial].Hostname, cancellationToken);
                if (fresh == null || fresh == stale.Ip) return;
                var retried = await ProbeAddressAsync(stale.Serial, fresh, AddressSource.Dns, null, cancellationToken);
                lock (probed) probed[stale.Serial] = retried;
            }, cancellationToken);
        }

        foreach (var c in computers)
            results[c.Serial] = probed.TryGetValue(c.Serial, out var r) ? r : HostScanResult.Unresolved(c.Serial);

        sw.Stop();
        var resolved = results.Values.Count(r => r.HasAddress);
        var online = results.Values.Count(r => r.State == HostState.Online);
        var mode = reportMateAvailable && fromReportMate > 0 ? ScanMode.ReportMate
            : resolved > 0 ? ScanMode.DnsOnly
            : ScanMode.Limited;

        return (results, new ScanSummary
        {
            Mode = mode,
            Total = computers.Count,
            Resolved = resolved,
            Online = online,
            FromReportMate = fromReportMate,
            FromDns = fromDns,
            ReportMateAvailable = reportMateAvailable,
            Duration = sw.Elapsed
        });
    }

    /// <summary>Re-check one machine: resolve again (or reuse the address) and probe.</summary>
    public async Task<HostScanResult> RescanAsync(RosterComputer computer, string? knownIp, CancellationToken cancellationToken)
    {
        var ip = knownIp;
        var source = knownIp != null ? AddressSource.Stored : AddressSource.None;
        if (string.IsNullOrEmpty(ip) && computer.HasHostname)
        {
            ip = await _probe.ResolveAsync(computer.Hostname, cancellationToken);
            source = AddressSource.Dns;
        }
        if (string.IsNullOrEmpty(ip)) return HostScanResult.Unresolved(computer.Serial);

        var ssh = _probe.IsTcpOpenAsync(ip, SshPort, cancellationToken);
        var rdp = _probe.IsTcpOpenAsync(ip, RdpPort, cancellationToken);
        await Task.WhenAll(ssh, rdp);
        return new HostScanResult { Serial = computer.Serial, Ip = ip, Source = source, SshOpen = ssh.Result, RdpOpen = rdp.Result };
    }

    private async Task<HostScanResult> ProbeAddressAsync(string serial, string ip, AddressSource source, DateTime? collectedAt, CancellationToken cancellationToken)
    {
        var ssh = _probe.IsTcpOpenAsync(ip, SshPort, cancellationToken);
        var rdp = _probe.IsTcpOpenAsync(ip, RdpPort, cancellationToken);
        await Task.WhenAll(ssh, rdp);
        return new HostScanResult
        {
            Serial = serial,
            Ip = ip,
            Source = source,
            AddressCollectedAt = collectedAt,
            SshOpen = ssh.Result,
            RdpOpen = rdp.Result
        };
    }

    private async Task RunThrottled<T>(IReadOnlyList<T> items, Func<T, Task> body, CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, Concurrency));
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);
            try { await body(item); }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
    }
}
