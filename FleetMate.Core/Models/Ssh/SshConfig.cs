namespace FleetMate.Models.Ssh;

/// <summary>
/// SSH connection configuration
/// </summary>
public class SshConfig
{
    /// <summary>
    /// Path to private key file (supports ~ for home directory)
    /// </summary>
    public string PrivateKeyPath { get; set; } = "~/.ssh/id_rsa";

    /// <summary>
    /// Environment variable containing the private key content (for Azure Key Vault)
    /// If set, takes precedence over PrivateKeyPath
    /// </summary>
    public string? PrivateKeyEnvVar { get; set; } = "SECURE_SHELL_PRIVATE_KEY";

    /// <summary>
    /// Azure Key Vault name for retrieving the private key
    /// If set, attempts to load SecureShellPrivateKey from the vault
    /// </summary>
    public string? KeyVaultName { get; set; }

    /// <summary>
    /// Default username for SSH connections (ithelp for devices without user login)
    /// </summary>
    public string DefaultUsername { get; set; } = "ithelp";

    /// <summary>
    /// Connection timeout in seconds
    /// </summary>
    public int ConnectionTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Command execution timeout in seconds
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Maximum concurrent SSH connections for batch operations
    /// </summary>
    public int MaxConcurrentConnections { get; set; } = 10;

    /// <summary>
    /// Default SSH port
    /// </summary>
    public int Port { get; set; } = 22;

    /// <summary>
    /// Resolves the private key path, expanding ~ to home directory
    /// </summary>
    public string ResolvedKeyPath
    {
        get
        {
            if (PrivateKeyPath.StartsWith("~"))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, PrivateKeyPath.TrimStart('~', '/', '\\'));
            }
            return PrivateKeyPath;
        }
    }

    /// <summary>
    /// Gets the private key content from environment variable if configured
    /// </summary>
    public string? GetPrivateKeyFromEnv()
    {
        if (string.IsNullOrEmpty(PrivateKeyEnvVar)) return null;
        return Environment.GetEnvironmentVariable(PrivateKeyEnvVar);
    }
}
