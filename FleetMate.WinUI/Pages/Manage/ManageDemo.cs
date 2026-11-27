using System.Text;
using FleetMate.Core.Models;
using FleetMate.Core.Services.Manage;

namespace FleetMate.WinUI.Pages.Manage;

/// <summary>
/// Demo mode's Manage tab: a made-up roster (labs, kiosks, staff and faculty
/// machines), addresses from the documentation range 192.0.2.0/24, and an SSH
/// transport that answers from canned output. Every name, serial and person
/// here is invented; nothing is resolved, dialled or launched.
/// </summary>
internal static class ManageDemo
{
    public static void Seed(Shell.ShellServices services) =>
        services.SeedManageDemo(Roster(), new Probe(), new Runner(), new NoLaunch());

    private static readonly string[] People = { "Avery Quinn", "Jordan Blake", "Riley Chen", "Morgan Hale", "Casey Ortiz", "Devon Park" };

    private static string Roster()
    {
        var csv = new StringBuilder("serial,catalog,area,location,asset,usage,status,allocation,username,platform,fleet,hostname\n");
        var n = 0;
        void Add(string catalog, string area, string location, string usage, string allocation, string user, string fleet, string host) =>
            csv.Append($"DEMO{++n:D4},{catalog},{area},{location},A-{1000 + n},{usage},Active,{allocation},{user},Windows,{fleet},{host}\n");

        foreach (var (fleet, area, room, count) in new[]
                 {
                     ("Animation Lab", "Media Arts", "B210", 8), ("Print Studio", "Design", "C114", 6),
                     ("Sound Suite", "Media Arts", "B230", 4), ("Drawing Room", "Fine Arts", "D301", 5),
                 })
        {
            var prefix = new string(fleet.Where(char.IsUpper).ToArray());
            for (var i = 1; i <= count; i++) Add("Curriculum", area, room, "Shared", $"{fleet} {i:D2}", "", fleet, $"{prefix}-{room}-{i:D2}");
        }
        Add("Kiosk", "Library", "Library Atrium", "Shared", "Library Kiosk 1", "", "", "KIOSK-LIB-01");
        Add("Kiosk", "Library", "Library Atrium", "Shared", "Library Kiosk 2", "", "", "KIOSK-LIB-02");
        Add("Kiosk", "Front Desk", "Main Entrance", "Shared", "Front Desk Kiosk", "", "", "KIOSK-FD-01");
        foreach (var (person, dept) in People.Take(4).Zip(new[] { "IT", "IT", "Finance", "Registrar" }))
            Add("Staff", dept, "A2" + person.Length, "Assigned", person, Login(person), "", "STAFF-" + Login(person).ToUpperInvariant());
        foreach (var person in People.Skip(2))
            Add("Faculty", "Faculty", "F1" + person.Length, "Assigned", person, Login(person), "", "FAC-" + Login(person).ToUpperInvariant());
        return csv.ToString();
    }

    private static string Login(string person)
    {
        var parts = person.Split(' ');
        return (parts[0][0] + parts[1]).ToLowerInvariant();
    }

    /// <summary>Stable fake address and state per hostname: most answer, a few are off, one has a closed SSH port.</summary>
    private static int Seed(string host) => host.Aggregate(17, (h, c) => h * 31 + c) & 0x7fffffff;

    private static string? AddressFor(string host) => Seed(host) % 7 == 0 ? null : $"192.0.2.{10 + Seed(host) % 200}";

    private sealed class Probe : IReachabilityProbe
    {
        public async Task<string?> ResolveAsync(string hostname, CancellationToken cancellationToken)
        {
            await Task.Delay(20, cancellationToken);
            return AddressFor(hostname);
        }

        public Task<bool> IsTcpOpenAsync(string ip, int port, CancellationToken cancellationToken)
        {
            var last = int.Parse(ip[(ip.LastIndexOf('.') + 1)..]);
            return Task.FromResult(port == 22 ? last % 11 != 0 : last % 5 != 0);
        }
    }

    private sealed class Runner : IRemoteRunner, IRemoteFileCopier
    {
        private static readonly string ProbeCommand = RemoteScriptEncoder.Wrap(Core.Models.Manage.MachineProbe.ProbeScript);

        public async Task<SecureShellResult> RunAsync(string ip, string command, Action<string>? onChunk, CancellationToken cancellationToken,
            string? username = null, string? deviceName = null)
        {
            var host = deviceName ?? ip;
            await Task.Delay(40 + Seed(host) % 120, cancellationToken);
            if (command == ProbeCommand)
                return Ok(ip, ProbeOutput(host), connected: true);
            if (Seed(host) % 9 == 0)
                return new SecureShellResult { Host = ip, Outcome = SecureShellOutcome.CommandFailed, ExitCode = 1, Stderr = "Access is denied.", Connected = true };
            var output = $"{host}: completed{Environment.NewLine}";
            onChunk?.Invoke(output);
            return Ok(ip, output, connected: true);
        }

        public Task<SecureShellResult> UploadAsync(string ip, string localPath, string remotePath, CancellationToken cancellationToken,
            string? username = null, string? deviceName = null) => Task.FromResult(Ok(ip, ""));

        private static SecureShellResult Ok(string ip, string stdout, bool connected = true) =>
            new() { Host = ip, Outcome = SecureShellOutcome.Success, ExitCode = 0, Stdout = stdout, Connected = connected };

        private static string ProbeOutput(string host)
        {
            var seed = Seed(host);
            var user = seed % 3 == 0 ? "" : $"EXAMPLE\\{Login(People[seed % People.Length])}";
            return string.Join("\n",
                $"user={user}", $"host={host}", $"os=Windows 11 Education 10.0.2{6100 + seed % 2 * 100}", "build=26100",
                $"uptime={seed % 9}d {seed % 24}h {seed % 60}m", $"join={(seed % 4 == 0 ? "hybrid" : "entra")}",
                $"rdp={(seed % 6 == 0 ? "disabled" : "enabled")}", "nla=yes", "rdp_port=listening", "ssh_port=listening", "sshd=Running",
                $"cimian_id=Shared/{host.Split('-')[0]}", $"cimian_last=2026-11-2{seed % 6} 0{seed % 9}:15",
                $"free_gb={40 + seed % 300}.{seed % 10}", "apps=explorer,msedge,Photoshop,Teams");
        }
    }

    private sealed class NoLaunch : IProcessLauncher
    {
        public void Start(string fileName, string arguments, bool hidden = false) { }
    }
}
