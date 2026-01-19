using System.Diagnostics;

namespace FleetMate.Models.Tdx;

/// <summary>
/// TeamDynamix (TDX) API configuration
///
/// Required Environment Variables:
/// - TDX_BASE_URL: TDX Web API base URL (e.g., https://your-instance.teamdynamix.com/TDWebApi)
/// - TDX_APP_ID: TDX application ID for tickets
///
/// Authentication (choose one method):
/// Admin Auth (preferred for elevated operations):
/// - TDX_BEID: TDX BEID for admin authentication
/// - TDX_WEB_SERVICES_KEY: TDX WebServicesKey/Secret for admin authentication
///
/// Regular Auth (fallback):
/// - TDX_USERNAME: TDX username
/// - TDX_PASSWORD: TDX password
///
/// Optional:
/// - TDX_KEY_VAULT_NAME: Azure Key Vault name to load credentials from
/// </summary>
public class TdxConfig
{
    /// <summary>
    /// TDX Web API base URL (required - set via TDX_BASE_URL env var)
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// TDX Application ID for tickets
    /// </summary>
    public int AppId { get; set; }

    /// <summary>
    /// TDX BEID for admin authentication (preferred)
    /// </summary>
    public string? Beid { get; set; }

    /// <summary>
    /// TDX WebServicesKey/Secret for admin authentication
    /// </summary>
    public string? WebServicesKey { get; set; }

    /// <summary>
    /// TDX username for regular authentication (fallback)
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// TDX password for authentication (or use environment variable TDX_PASSWORD)
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Azure Key Vault name to load TDX credentials from
    /// Uses secrets: TdxBeid, TdxBeidSecret, TdxUsername, TdxPassword
    /// </summary>
    public string? KeyVaultName { get; set; }

    /// <summary>
    /// Environment variable names for TDX credentials
    /// </summary>
    public string BeidEnvVar { get; set; } = "TDX_BEID";
    public string WebServicesKeyEnvVar { get; set; } = "TDX_BEID_SECRET";
    public string UsernameEnvVar { get; set; } = "TDX_USERNAME";
    public string PasswordEnvVar { get; set; } = "TDX_PASSWORD";

    /// <summary>
    /// Default ticket type ID for new tickets
    /// </summary>
    public int? DefaultTypeId { get; set; }

    /// <summary>
    /// Default ticket source ID (e.g., "API", "FleetMate")
    /// </summary>
    public int? DefaultSourceId { get; set; }

    /// <summary>
    /// Default priority ID for new tickets
    /// </summary>
    public int? DefaultPriorityId { get; set; }

    /// <summary>
    /// Default status ID for new tickets
    /// </summary>
    public int? DefaultStatusId { get; set; }

    /// <summary>
    /// Default account/department ID for new tickets
    /// </summary>
    public int? DefaultAccountId { get; set; }

    /// <summary>
    /// Cache duration in minutes for reference data
    /// </summary>
    public int CacheMinutes { get; set; } = 30;

    // Cached Key Vault secrets
    private static Dictionary<string, string>? _kvSecrets;
    private static bool _kvLoaded;

    /// <summary>
    /// Get admin credentials (BEID + WebServicesKey) for admin login
    /// </summary>
    public (string? beid, string? webServicesKey) GetAdminCredentials()
    {
        // Try config first
        var beid = Beid;
        var key = WebServicesKey;

        // Try environment variables
        if (string.IsNullOrEmpty(beid))
            beid = Environment.GetEnvironmentVariable(BeidEnvVar);
        if (string.IsNullOrEmpty(key))
            key = Environment.GetEnvironmentVariable(WebServicesKeyEnvVar);

        // Try Key Vault
        if ((string.IsNullOrEmpty(beid) || string.IsNullOrEmpty(key)) && !string.IsNullOrEmpty(KeyVaultName))
        {
            LoadKeyVaultSecrets();
            if (_kvSecrets != null)
            {
                if (string.IsNullOrEmpty(beid) && _kvSecrets.TryGetValue("TdxBeid", out var kvBeid))
                    beid = kvBeid;
                if (string.IsNullOrEmpty(key) && _kvSecrets.TryGetValue("TdxBeidSecret", out var kvKey))
                    key = kvKey;
            }
        }

        return (beid, key);
    }

    /// <summary>
    /// Get regular credentials (Username + Password) for regular login
    /// </summary>
    public (string? username, string? password) GetRegularCredentials()
    {
        // Try config first
        var user = Username;
        var pass = Password;

        // Try environment variables
        if (string.IsNullOrEmpty(user))
            user = Environment.GetEnvironmentVariable(UsernameEnvVar);
        if (string.IsNullOrEmpty(pass))
            pass = Environment.GetEnvironmentVariable(PasswordEnvVar);

        // Try Key Vault
        if ((string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass)) && !string.IsNullOrEmpty(KeyVaultName))
        {
            LoadKeyVaultSecrets();
            if (_kvSecrets != null)
            {
                if (string.IsNullOrEmpty(user) && _kvSecrets.TryGetValue("TdxUsername", out var kvUser))
                    user = kvUser;
                if (string.IsNullOrEmpty(pass) && _kvSecrets.TryGetValue("TdxPassword", out var kvPass))
                    pass = kvPass;
            }
        }

        return (user, pass);
    }

    /// <summary>
    /// Legacy: Get the password from environment variable or config
    /// </summary>
    public string? GetPassword()
    {
        var (_, password) = GetRegularCredentials();
        return password;
    }

    private void LoadKeyVaultSecrets()
    {
        if (_kvLoaded || string.IsNullOrEmpty(KeyVaultName)) return;
        _kvLoaded = true;

        var secretNames = new[] { "TdxBeid", "TdxBeidSecret", "TdxUsername", "TdxPassword" };
        _kvSecrets = new Dictionary<string, string>();

        var azPath = FindAzureCli();
        if (azPath == null) return;

        foreach (var name in secretNames)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = azPath,
                    Arguments = $"keyvault secret show --vault-name {KeyVaultName} --name {name} --query value -o tsv",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) continue;

                var value = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();

                if (process.ExitCode == 0 && !string.IsNullOrEmpty(value))
                {
                    _kvSecrets[name] = value;
                }
            }
            catch
            {
                // Ignore individual secret failures
            }
        }
    }

    private static string? FindAzureCli()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
            @"C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd",
            @"C:\Program Files (x86)\Microsoft SDKs\Azure\CLI2\wbin\az.cmd"
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var azCmd = Path.Combine(dir, "az.cmd");
            if (File.Exists(azCmd)) return azCmd;
        }

        return null;
    }

    /// <summary>
    /// Get the API URL for a specific endpoint
    /// </summary>
    public string GetApiUrl(string endpoint)
    {
        if (string.IsNullOrEmpty(BaseUrl))
            throw new InvalidOperationException("TDX BaseUrl is not configured. Set TDX_BASE_URL environment variable.");

        var baseUrl = BaseUrl.TrimEnd('/');
        return $"{baseUrl}/api/{endpoint}";
    }

    /// <summary>
    /// Get the tickets API URL
    /// </summary>
    public string GetTicketsUrl(string? path = null)
    {
        if (string.IsNullOrEmpty(BaseUrl))
            throw new InvalidOperationException("TDX BaseUrl is not configured. Set TDX_BASE_URL environment variable.");

        var baseUrl = BaseUrl.TrimEnd('/');
        return string.IsNullOrEmpty(path)
            ? $"{baseUrl}/api/{AppId}/tickets"
            : $"{baseUrl}/api/{AppId}/tickets/{path}";
    }

    /// <summary>
    /// Get the assets API URL
    /// </summary>
    public string GetAssetsUrl(string? path = null)
    {
        if (string.IsNullOrEmpty(BaseUrl))
            throw new InvalidOperationException("TDX BaseUrl is not configured. Set TDX_BASE_URL environment variable.");

        var baseUrl = BaseUrl.TrimEnd('/');
        return string.IsNullOrEmpty(path)
            ? $"{baseUrl}/api/{AppId}/assets"
            : $"{baseUrl}/api/{AppId}/assets/{path}";
    }

    /// <summary>
    /// Check if TDX is configured (has required settings)
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(BaseUrl) && AppId > 0;
}
