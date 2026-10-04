using System.Diagnostics;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Reporting;

namespace FleetMate.Core.Services.Reporting;

/// <summary>
/// MunkiReport has no API, so the CLI SSHes to the server and reads its
/// SQLite database (macOS parity). The host, user, key and database path come
/// from config or the MUNKIREPORT_SSH_* environment variables; none is assumed.
/// </summary>
public sealed class MunkiReportService(FleetMateConfig config)
{
    public const string DefaultDbPath = "/var/munkireport/db/db.sqlite";
    private const string DeviceColumns =
        "serial_number, hostname, machine_name, os_version, buildversion, machine_model, cpu_type, physical_memory, remote_ip, timestamp";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(config.MunkiReportSshHost);

    /// <summary>Run a command on the MunkiReport server over SSH.</summary>
    public async Task<string> ExecuteSshAsync(string command, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("MunkiReport SSH is not configured. Set munkiReportSshHost or MUNKIREPORT_SSH_HOST.");

        var start = new ProcessStartInfo(SshExecutable())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in SshArguments(config, command)) start.ArgumentList.Add(arg);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start ssh.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            var err = (await stderr).Trim();
            throw new InvalidOperationException($"SSH failed: {(err.Length == 0 ? "Unknown error" : err)}");
        }
        return await stdout;
    }

    /// <summary>
    /// The ssh arguments. Batch mode never prompts. A new host key is accepted
    /// on first use and checked afterwards, rather than never checked.
    /// </summary>
    public static List<string> SshArguments(FleetMateConfig config, string command)
    {
        var args = new List<string> { "-o", "StrictHostKeyChecking=accept-new", "-o", "BatchMode=yes" };
        if (!string.IsNullOrWhiteSpace(config.MunkiReportSshKeyPath))
            args.AddRange(new[] { "-i", ExpandHome(config.MunkiReportSshKeyPath!) });
        args.Add($"{(string.IsNullOrWhiteSpace(config.MunkiReportSshUser) ? "root" : config.MunkiReportSshUser)}@{config.MunkiReportSshHost}");
        args.Add(command);
        return args;
    }

    /// <summary>The remote sqlite3 command for <paramref name="sql"/>, quoted for a POSIX shell.</summary>
    public static string SqliteCommand(string sql, string? dbPath) =>
        $"sqlite3 -header -separator '|' {ShellQuote(dbPath ?? DefaultDbPath)} {ShellQuote(sql)}";

    public async Task<List<Dictionary<string, string>>> ExecuteSqlAsync(string sql, CancellationToken ct = default) =>
        ParseRows(await ExecuteSshAsync(SqliteCommand(sql, config.MunkiReportDbPath), ct));

    /// <summary>sqlite3's header-plus-pipe-separated output as rows keyed by column.</summary>
    public static List<Dictionary<string, string>> ParseRows(string output)
    {
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (lines.Count < 2) return new();
        var headers = lines[0].Split('|');
        return lines.Skip(1).Select(line =>
        {
            var values = line.Split('|');
            var row = new Dictionary<string, string>();
            for (var i = 0; i < headers.Length && i < values.Length; i++) row[headers[i]] = values[i];
            return row;
        }).ToList();
    }

    public async Task<List<MunkiDevice>> GetDevicesAsync() =>
        (await ExecuteSqlAsync($"SELECT {DeviceColumns} FROM reportdata ORDER BY hostname")).Select(MunkiDevice.FromRow).ToList();

    public async Task<MunkiDevice?> GetDeviceAsync(string identifier)
    {
        var id = SqlLiteral(identifier);
        var rows = await ExecuteSqlAsync($"SELECT {DeviceColumns} FROM reportdata WHERE serial_number = {id} OR hostname = {id} LIMIT 1");
        return rows.Select(MunkiDevice.FromRow).FirstOrDefault();
    }

    public async Task<MunkiInfo?> GetMunkiInfoAsync(string serial)
    {
        var rows = await ExecuteSqlAsync(
            "SELECT serial_number, version, manifest, manifesturl, runtype, starttime, endtime FROM munkiinfo " +
            $"WHERE serial_number = {SqlLiteral(serial)} ORDER BY starttime DESC LIMIT 1");
        return rows.Select(MunkiInfo.FromRow).FirstOrDefault();
    }

    public async Task<List<ManagedInstall>> GetManagedInstallsAsync(string serial) =>
        (await ExecuteSqlAsync(
            "SELECT serial_number, name, display_name, version, installed_version, status, installed FROM managedinstalls " +
            $"WHERE serial_number = {SqlLiteral(serial)} ORDER BY name")).Select(ManagedInstall.FromRow).ToList();

    public async Task<List<MunkiInstallError>> GetErrorsAsync() =>
        (await ExecuteSqlAsync(
            "SELECT m.serial_number, r.hostname, m.name, m.display_name, m.version, m.status FROM managedinstalls m " +
            "JOIN reportdata r ON m.serial_number = r.serial_number WHERE m.status != 'installed' AND m.status != '' " +
            "ORDER BY m.name, r.hostname")).Select(MunkiInstallError.FromRow).ToList();

    /// <summary>A SQL string literal; doubling quotes keeps a serial from closing the string.</summary>
    public static string SqlLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>Single-quote for the remote POSIX shell.</summary>
    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    private static string ExpandHome(string path) =>
        path.StartsWith("~") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\')) : path;

    /// <summary>Windows' own OpenSSH client when present, else whatever ssh is on PATH.</summary>
    private static string SshExecutable()
    {
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
        return File.Exists(system) ? system : "ssh";
    }
}
