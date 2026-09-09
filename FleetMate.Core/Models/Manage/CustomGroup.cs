using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Manage;

/// <summary>
/// A machine added to a custom group by hand: a roster machine picked from the
/// browser, a hostname typed in, or a bare IP. Ip is cached once resolved so
/// the group opens online without a fresh scan.
/// </summary>
public class AdhocDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Hostname { get; set; } = "";
    public string Ip { get; set; } = "";
    /// <summary>Roster serial when the device was picked from the roster; empty for typed-in hosts.</summary>
    public string Serial { get; set; } = "";

    public AdhocDevice() { }

    public AdhocDevice(string hostname, string ip, string serial = "")
    {
        Hostname = string.IsNullOrWhiteSpace(hostname) ? ip.Trim() : hostname.Trim();
        Ip = ip.Trim();
        Serial = serial.Trim();
    }

    [JsonIgnore]
    public RosterComputer Computer => string.IsNullOrEmpty(Serial)
        ? RosterComputer.Adhoc(Hostname, Ip)
        : new RosterComputer { Serial = Serial, Hostname = Hostname, Allocation = Hostname, Status = "Active", Platform = "Windows" };
}

/// <summary>A named, persisted set of machines outside the room structure.</summary>
public class CustomGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<AdhocDevice> Devices { get; set; } = new();

    public CustomGroup() { }

    public CustomGroup(string name)
    {
        Name = name.Trim();
    }

    [JsonIgnore]
    public RosterRoom AsRoom => new()
    {
        Number = Name,
        Computers = Devices.Select(d => d.Computer).ToList()
    };
}
