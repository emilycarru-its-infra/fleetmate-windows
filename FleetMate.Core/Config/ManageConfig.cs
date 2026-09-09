using FleetMate.Core.Models;
using FleetMate.Core.Services.Manage;

namespace FleetMate.Core.Config;

/// <summary>
/// Settings for the Manage tab: where the roster and command library live,
/// how SSH and RDP sessions are opened. Persisted as plain values under the
/// desktop registry key; nothing here is a secret (the RDP password has its
/// own DPAPI store).
/// </summary>
public class ManageConfig
{
    /// <summary>Path to the enrollment roster CSV (computers.csv).</summary>
    public string RosterPath { get; set; } = "";

    /// <summary>Path to the YAML command library; empty means the per-user default.</summary>
    public string CommandsPath { get; set; } = "";

    /// <summary>Private key for fleet SSH; empty means <c>~/.ssh/id_rsa.winadmins</c>.</summary>
    public string SshKeyPath { get; set; } = "";

    /// <summary>Account for fleet SSH; empty means the fleet admin account.</summary>
    public string SshUser { get; set; } = "";

    /// <summary>Windows Terminal profile name to open SSH sessions in; empty means the default profile.</summary>
    public string TerminalProfile { get; set; } = "";

    /// <summary>Account offered to Remote Desktop; empty means the SSH user.</summary>
    public string RdpUser { get; set; } = "";

    /// <summary>Show roster rows whose status is not an Active variant.</summary>
    public bool IncludeRetired { get; set; }

    /// <summary>Show the Provisioning catalog as a lab section.</summary>
    public bool IncludeProvisioning { get; set; }

    /// <summary>Parallel SSH probes during a scan.</summary>
    public int ProbeConcurrency { get; set; } = 12;

    public string ResolvedCommandsPath =>
        string.IsNullOrWhiteSpace(CommandsPath) ? new ManageStateStore().CommandsPath : ExpandHome(CommandsPath);

    public string ResolvedSshKeyPath =>
        ExpandHome(string.IsNullOrWhiteSpace(SshKeyPath) ? "~/.ssh/id_rsa.winadmins" : SshKeyPath);

    public string ResolvedSshUser =>
        string.IsNullOrWhiteSpace(SshUser) ? SecureShellConfig.FleetAdminUsername : SshUser.Trim();

    public string ResolvedRdpUser =>
        string.IsNullOrWhiteSpace(RdpUser) ? ResolvedSshUser : RdpUser.Trim();

    public bool HasRoster => !string.IsNullOrWhiteSpace(RosterPath) && File.Exists(ExpandHome(RosterPath));

    public bool HasSshKey => File.Exists(ResolvedSshKeyPath);

    /// <summary>The SSH configuration the Manage tab connects with.</summary>
    public SecureShellConfig ToSecureShellConfig() => new()
    {
        PrivateKeyPath = ResolvedSshKeyPath,
        PrivateKeyEnvVar = null,
        DefaultUsername = ResolvedSshUser,
        ConnectionTimeoutSeconds = 8,
        CommandTimeoutSeconds = 300,
        MaxConcurrentConnections = Math.Max(1, ProbeConcurrency),
        AcceptAllHostKeys = true,
        AutoCleanStaleHostKeys = true
    };

    public static string ExpandHome(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (path.StartsWith("~"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.GetFullPath(Path.Combine(home, path.TrimStart('~', '/', '\\')));
        }
        return Environment.ExpandEnvironmentVariables(path);
    }
}
