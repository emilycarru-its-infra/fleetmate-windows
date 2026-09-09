using CommunityToolkit.Mvvm.ComponentModel;
using FleetMate.Core.Models;
using FleetMate.Core.Models.Manage;

namespace FleetMate.GUI.ViewModels.Manage;

/// <summary>
/// One machine in the current room or group: roster identity plus whatever
/// the last scan and probe learned. Display strings live here so the view
/// stays declarative and the wording is testable.
/// </summary>
public partial class MachineRowViewModel : ObservableObject
{
    public RosterComputer Computer { get; }

    [ObservableProperty] private HostScanResult _scan;
    [ObservableProperty] private MachineProbe? _probe;
    [ObservableProperty] private SecureShellOutcome? _probeOutcome;
    [ObservableProperty] private string? _probeError;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isRescanning;
    [ObservableProperty] private bool _isProbing;
    [ObservableProperty] private CommandRunStatus? _lastRunStatus;

    public MachineRowViewModel(RosterComputer computer)
    {
        Computer = computer;
        _scan = HostScanResult.Unresolved(computer.Serial);
    }

    public string Serial => Computer.Serial;
    public string Name => Computer.DisplayName;
    /// <summary>The roster's friendly name (allocation), falling back to the hostname or serial.</summary>
    public string FriendlyName => Computer.Allocation.Length > 0 ? Computer.Allocation : Computer.DisplayName;
    public string Hostname => Computer.Hostname;
    public string Ip => Scan.Ip;
    public bool HasAddress => Scan.HasAddress;
    public bool IsOnline => Scan.State == HostState.Online;
    public bool IsUnreachable => Scan.State == HostState.Unreachable;
    public bool SshOpen => Scan.SshOpen;
    public bool RdpOpen => Scan.RdpOpen;
    public bool SshAuthFailed => ProbeOutcome == SecureShellOutcome.AuthFailed;
    public bool HasProbe => Probe != null;

    /// <summary>Colour key for the status dot: online, unreachable, offline, scanning.</summary>
    public string StatusKey =>
        IsRescanning ? "scanning"
        : IsOnline ? "online"
        : IsUnreachable ? "unreachable"
        : "offline";

    public string StatusLabel =>
        IsRescanning ? "Scanning"
        : IsOnline ? "Online"
        : IsUnreachable ? "Known address, no answer"
        : Computer.HasHostname ? "Offline" : "No hostname";

    public string AddressLabel
    {
        get
        {
            if (!HasAddress) return "";
            var label = Scan.Ip;
            if (Scan.Source == AddressSource.ReportMate && Scan.AddressAge is { } age)
                label += age > HostScanResult.StaleAddressAge ? $"  (inventory, {FormatAge(age)} old)" : "";
            return label;
        }
    }

    public string SourceLabel => Scan.Source switch
    {
        AddressSource.ReportMate => Scan.AddressIsStale ? "ReportMate (stale)" : "ReportMate",
        AddressSource.Dns => "DNS",
        AddressSource.Stored => "Stored",
        _ => ""
    };

    /// <summary>Who is at the console, or where the machine is instead.</summary>
    public string UserLabel
    {
        get
        {
            if (Probe == null) return SshAuthFailed ? "SSH key rejected" : "";
            return Probe.AtLoginWindow ? "Sign-in screen" : Probe.ConsoleUserShort;
        }
    }

    public string OsLabel => Probe?.OsVersion ?? "";
    public string UptimeLabel => Probe?.Uptime ?? "";
    public string JoinLabel => Probe?.JoinType switch
    {
        "entra" => "Entra joined",
        "hybrid" => "Hybrid joined",
        "domain" => "Domain joined",
        "none" => "Not joined",
        _ => ""
    };

    /// <summary>Remote-access readiness in one phrase.</summary>
    public string RemoteAccessLabel
    {
        get
        {
            if (!HasAddress) return "";
            var parts = new List<string>();
            parts.Add(SshAuthFailed ? "SSH: key rejected" : SshOpen ? "SSH ready" : "SSH closed");
            if (Probe != null)
                parts.Add(Probe.RdpReady ? (Probe.RdpNlaRequired ? "RDP ready (NLA)" : "RDP ready") : Probe.RdpEnabled ? "RDP enabled, port closed" : "RDP disabled");
            else
                parts.Add(RdpOpen ? "RDP port open" : "RDP closed");
            return string.Join("  ·  ", parts);
        }
    }

    public string RemoteAccessHelp
    {
        get
        {
            var lines = new List<string>();
            if (!HasAddress) lines.Add("No address yet. Scan the room, or add the machine's address to a custom group.");
            if (SshAuthFailed) lines.Add("sshd answered but rejected the key: the shared admin key is not authorised on this machine, or the SSH user is wrong.");
            else if (HasAddress && !SshOpen) lines.Add("Port 22 is closed: the SSH server is not installed or not running.");
            if (Probe is { RdpEnabled: false }) lines.Add("Remote Desktop is disabled on this machine (fDenyTSConnections is set).");
            if (Probe is { RdpEnabled: true, RdpPortListening: false }) lines.Add("Remote Desktop is enabled but nothing is listening on 3389.");
            if (Scan.AddressIsStale) lines.Add($"The inventory address is {FormatAge(Scan.AddressAge!.Value)} old and may belong to another machine now.");
            return lines.Count == 0 ? "SSH and RDP both answered." : string.Join("\n", lines);
        }
    }

    public string CimianLabel => Probe?.CimianClientIdentifier ?? "";
    public string LastRunLabel => LastRunStatus?.Label() ?? "";

    /// <summary>The inventory line for tickets and hand-off notes.</summary>
    public string CopyLine => Computer.InventoryLine(HasAddress ? Ip : null, Probe?.OsVersion);

    /// <summary>Everything the row knows, for "Copy all info".</summary>
    public string CopyAllInfo()
    {
        var lines = new List<string>
        {
            $"Name: {FriendlyName}",
        };
        if (Computer.HasHostname && Computer.Hostname != FriendlyName) lines.Add($"Hostname: {Computer.Hostname}");
        if (HasAddress) lines.Add($"IP: {Ip}" + (SourceLabel.Length > 0 ? $" ({SourceLabel})" : ""));
        if (!Computer.IsAdhoc) lines.Add($"Serial: {Serial}");
        if (Computer.Asset.Length > 0) lines.Add($"Asset: {Computer.Asset}");
        if (Computer.Location.Length > 0) lines.Add($"Location: {Computer.Location}");
        if (Computer.Fleet.Length > 0) lines.Add($"Fleet: {Computer.Fleet}");
        lines.Add($"Status: {StatusLabel}");
        if (Probe != null)
        {
            lines.Add($"Console user: {(Probe.AtLoginWindow ? "(sign-in screen)" : Probe.ConsoleUser)}");
            lines.Add($"OS: {Probe.OsVersion}");
            lines.Add($"Uptime: {Probe.Uptime}");
            if (JoinLabel.Length > 0) lines.Add($"Join: {JoinLabel}");
            lines.Add($"Remote access: {RemoteAccessLabel}");
            if (Probe.CimianClientIdentifier.Length > 0) lines.Add($"Client identifier: {Probe.CimianClientIdentifier}");
            if (Probe.CimianLastRun.Length > 0) lines.Add($"Last managed run: {Probe.CimianLastRun}");
            if (Probe.FreeSpaceGb.Length > 0) lines.Add($"Free space: {Probe.FreeSpaceGb} GB");
            if (Probe.TopApps.Count > 0) lines.Add($"Apps: {string.Join(", ", Probe.TopApps)}");
        }
        else if (HasAddress)
        {
            lines.Add($"Remote access: {RemoteAccessLabel}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    internal static string FormatAge(TimeSpan age)
    {
        if (age.TotalMinutes < 90) return $"{(int)Math.Max(1, age.TotalMinutes)} min";
        if (age.TotalHours < 48) return $"{(int)age.TotalHours} h";
        return $"{(int)age.TotalDays} d";
    }

    partial void OnScanChanged(HostScanResult value) => RaiseDerived();
    partial void OnProbeChanged(MachineProbe? value) => RaiseDerived();
    partial void OnProbeOutcomeChanged(SecureShellOutcome? value) => RaiseDerived();
    partial void OnIsRescanningChanged(bool value) => RaiseDerived();
    partial void OnLastRunStatusChanged(CommandRunStatus? value) => OnPropertyChanged(nameof(LastRunLabel));

    private void RaiseDerived()
    {
        foreach (var name in new[]
                 {
                     nameof(Ip), nameof(HasAddress), nameof(IsOnline), nameof(IsUnreachable), nameof(SshOpen), nameof(RdpOpen),
                     nameof(SshAuthFailed), nameof(HasProbe), nameof(StatusKey), nameof(StatusLabel), nameof(AddressLabel),
                     nameof(SourceLabel), nameof(UserLabel), nameof(OsLabel), nameof(UptimeLabel), nameof(JoinLabel),
                     nameof(RemoteAccessLabel), nameof(RemoteAccessHelp), nameof(CimianLabel), nameof(CopyLine)
                 })
            OnPropertyChanged(name);
    }
}
