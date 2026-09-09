namespace FleetMate.Core.Models.Manage;

/// <summary>
/// Live facts fetched from a machine over SSH by <see cref="ProbeScript"/>.
/// The script prints key=value lines; anything it cannot read prints empty.
/// </summary>
public class MachineProbe
{
    public string Hostname { get; init; } = "";
    public string Ip { get; init; } = "";

    /// <summary>Signed-in console user as DOMAIN\user or AzureAD\user; empty at the sign-in screen.</summary>
    public string ConsoleUser { get; init; } = "";
    public string ComputerName { get; init; } = "";
    public string OsVersion { get; init; } = "";
    public string OsBuild { get; init; } = "";
    public string Uptime { get; init; } = "";
    /// <summary>entra, domain, hybrid, or none.</summary>
    public string JoinType { get; init; } = "";

    public bool RdpEnabled { get; init; }
    public bool RdpNlaRequired { get; init; }
    public bool RdpPortListening { get; init; }
    public bool SshPortListening { get; init; }
    public string SshdStatus { get; init; } = "";

    public string CimianClientIdentifier { get; init; } = "";
    public string CimianLastRun { get; init; } = "";
    public string FreeSpaceGb { get; init; } = "";
    public List<string> TopApps { get; init; } = new();

    public DateTime FetchedAt { get; init; } = DateTime.Now;

    public bool AtLoginWindow => string.IsNullOrWhiteSpace(ConsoleUser);
    public bool RdpReady => RdpEnabled && RdpPortListening;
    public bool SshReady => SshPortListening;

    /// <summary>Short user label without the domain prefix.</summary>
    public string ConsoleUserShort
    {
        get
        {
            var slash = ConsoleUser.LastIndexOf('\\');
            return slash >= 0 ? ConsoleUser[(slash + 1)..] : ConsoleUser;
        }
    }

    /// <summary>
    /// PowerShell run on the target through <c>powershell -EncodedCommand</c>.
    /// Kept compact: the encoded form must fit the remote shell's command line.
    /// Every value is written even when a lookup fails so the parser sees a stable key set.
    /// </summary>
    public const string ProbeScript = """
        $ErrorActionPreference='SilentlyContinue'
        $cs=Get-CimInstance Win32_ComputerSystem
        $os=Get-CimInstance Win32_OperatingSystem
        "user=$($cs.UserName)"
        "host=$env:COMPUTERNAME"
        "os=$($os.Caption -replace '^Microsoft ','') $($os.Version)"
        "build=$($os.BuildNumber)"
        $up=(Get-Date)-$os.LastBootUpTime
        "uptime=$([int]$up.TotalDays)d $($up.Hours)h $($up.Minutes)m"
        $d=(dsregcmd /status) -join "`n"
        $aad=$d -match 'AzureAdJoined\s*:\s*YES'; $dom=$d -match 'DomainJoined\s*:\s*YES'
        "join=$(if($aad -and $dom){'hybrid'}elseif($aad){'entra'}elseif($dom){'domain'}else{'none'})"
        $ts=Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
        $pol=Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
        $deny=if($null -ne $pol.fDenyTSConnections){$pol.fDenyTSConnections}else{$ts.fDenyTSConnections}
        "rdp=$(if($deny -eq 0){'enabled'}else{'disabled'})"
        $nla=(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp').UserAuthentication
        "nla=$(if($nla -eq 1){'yes'}else{'no'})"
        $l=Get-NetTCPConnection -State Listen | Select-Object -ExpandProperty LocalPort
        "rdp_port=$(if($l -contains 3389){'listening'}else{'not-listening'})"
        "ssh_port=$(if($l -contains 22){'listening'}else{'not-listening'})"
        "sshd=$((Get-Service sshd).Status)"
        $ci=(Get-Content 'C:\ProgramData\ManagedInstalls\Config.yaml' | Where-Object {$_ -match '^\s*ClientIdentifier\s*:'} | Select-Object -First 1) -replace '^\s*ClientIdentifier\s*:\s*',''
        "cimian_id=$($ci.Trim().Trim('"',"'"))"
        "cimian_last=$((Get-Item 'C:\ProgramData\ManagedInstalls\Logs\ManagedSoftwareUpdate.log').LastWriteTime.ToString('yyyy-MM-dd HH:mm'))"
        "free_gb=$([math]::Round((Get-PSDrive C).Free/1GB,1))"
        $sid=(Get-Process explorer | Select-Object -First 1).SessionId
        "apps=$(((Get-Process | Where-Object {$_.SessionId -eq $sid -and $_.MainWindowHandle -ne 0} | Select-Object -ExpandProperty ProcessName -Unique | Sort-Object | Select-Object -First 10) -join ','))"
        """;

    public static MachineProbe Parse(string hostname, string ip, string raw)
    {
        var d = ParseKeyValues(raw);
        return new MachineProbe
        {
            Hostname = hostname,
            Ip = ip,
            ConsoleUser = Get(d, "user"),
            ComputerName = Get(d, "host"),
            OsVersion = Get(d, "os"),
            OsBuild = Get(d, "build"),
            Uptime = Get(d, "uptime"),
            JoinType = Get(d, "join"),
            RdpEnabled = Get(d, "rdp") == "enabled",
            RdpNlaRequired = Get(d, "nla") == "yes",
            RdpPortListening = Get(d, "rdp_port") == "listening",
            SshPortListening = Get(d, "ssh_port") == "listening",
            SshdStatus = Get(d, "sshd"),
            CimianClientIdentifier = Get(d, "cimian_id"),
            CimianLastRun = Get(d, "cimian_last"),
            FreeSpaceGb = Get(d, "free_gb"),
            TopApps = Get(d, "apps").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
        };
    }

    internal static Dictionary<string, string> ParseKeyValues(string raw)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in (raw ?? "").Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            d[line[..eq]] = line[(eq + 1)..].Trim();
        }
        return d;
    }

    private static string Get(Dictionary<string, string> d, string key) =>
        d.TryGetValue(key, out var v) ? v : "";
}
