using Microsoft.Win32;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Serilog;
using FleetMate.Models.SecureShell;
using FleetMate.Models.Graph;
using FleetMate.Models.Tdx;

namespace FleetMate.Config;

/// <summary>
/// Configuration for FleetMate
///
/// Credentials are loaded in order of precedence:
/// 1. Environment variables (highest priority, for CI/CD)
/// 2. Windows Registry (HKCU\SOFTWARE\FleetMate) - for developer workstations
/// 3. .env files (legacy fallback)
/// 4. Config YAML files (default values)
///
/// Required Environment Variables:
/// - REPORTMATE_URL: ReportMate API base URL
/// - REPORTMATE_PASSPHRASE: ReportMate API passphrase
/// - SNIPE_URL: Snipe-IT instance URL
/// - SNIPE_API_KEY: Snipe-IT API key
/// - GRAPH_TENANT_ID: Azure AD tenant ID for Microsoft Graph
/// - GRAPH_CLIENT_ID: Azure AD application client ID
/// - GRAPH_CLIENT_SECRET: Azure AD application client secret (optional, uses Azure CLI SSO if not set)
/// - DEVOPS_ORGANIZATION: Azure DevOps organization name
/// - DEVOPS_PROJECT: Azure DevOps project name
/// - DEVOPS_PAT: Azure DevOps personal access token (optional, uses Azure CLI SSO if not set)
/// - TDX_BASE_URL: TeamDynamix Web API base URL
/// - TDX_APP_ID: TeamDynamix application ID
/// - TDX_USERNAME: TeamDynamix username (for regular auth)
/// - TDX_PASSWORD: TeamDynamix password (for regular auth)
/// - TDX_BEID: TeamDynamix BEID (for admin auth)
/// - TDX_WEB_SERVICES_KEY: TeamDynamix web services key (for admin auth)
/// - SSH_HOST: SSH host for remote operations
/// - SSH_USER: SSH username
/// - SSH_KEY_PATH: Path to SSH private key
/// </summary>
public class FleetMateConfig
{
    // Registry path for FleetMate credentials (OMA-URI style CSP path)
    private const string RegistryPath = @"SOFTWARE\FleetMate";
    
    // ReportMate API settings (required: REPORTMATE_URL, REPORTMATE_PASSPHRASE)
    public string? ReportMateUrl { get; set; }
    public string? ReportMatePassphrase { get; set; }
    
    // Snipe-IT API settings
    public string? SnipeUrl { get; set; }
    public string? SnipeApiKey { get; set; }
    
    // Deployment repo paths (relative to repo root or absolute)
    public string DeploymentPath { get; set; } = "deployment";
    public string PkgsinfoPath { get; set; } = "deployment/pkgsinfo";
    public string PkgsPath { get; set; } = "deployment/pkgs";
    public string ManifestsPath { get; set; } = "deployment/manifests";
    public string CatalogsPath { get; set; } = "deployment/catalogs";
    
    // Quality control paths
    public string QualityPath { get; set; } = "quality";
    public string PackagesPath { get; set; } = "packages";
    public string InstallersPath { get; set; } = "installers";
    
    // Logging
    public string LogPath { get; set; } = @"C:\ProgramData\Cimian\Logs";
    public string LogLevel { get; set; } = "Information";
    
    // Cache settings
    public int CacheMinutes { get; set; } = 5;

    // SecureShell Configuration
    public SecureShellConfig? SecureShell { get; set; }

    // Azure DevOps Configuration
    public AzureDevOpsConfig? AzureDevOps { get; set; }

    // Microsoft Graph Configuration
    public GraphConfig? Graph { get; set; }

    // TeamDynamix Configuration
    public TdxConfig? Tdx { get; set; }

    /// <summary>
    /// Get the repo root path (where .git folder is)
    /// </summary>
    public string? RepoRoot { get; set; }
    
    private static readonly string[] ConfigLocations = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fleetmate", "config.yaml"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FleetMate", "config.yaml"),
        @"C:\ProgramData\Cimian\fleetmate.yaml"
    };
    
    private static readonly string EnvFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fleetmate", ".env");
    
    public static FleetMateConfig Load()
    {
        var config = new FleetMateConfig();
        
        // Try to find and load config file
        foreach (var location in ConfigLocations)
        {
            if (File.Exists(location))
            {
                try
                {
                    var yaml = File.ReadAllText(location);
                    var deserializer = new DeserializerBuilder()
                        .WithNamingConvention(CamelCaseNamingConvention.Instance)
                        .IgnoreUnmatchedProperties()
                        .Build();
                    
                    config = deserializer.Deserialize<FleetMateConfig>(yaml) ?? config;
                    Log.Debug("Loaded config from {Path}", location);
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to load config from {Path}", location);
                }
            }
        }
        
        // Load .env file for secrets
        if (File.Exists(EnvFile))
        {
            LoadEnvFile(EnvFile, config);
        }
        
        // Also check for .env in current directory or repo root
        var localEnv = Path.Combine(Directory.GetCurrentDirectory(), ".env");
        if (File.Exists(localEnv))
        {
            LoadEnvFile(localEnv, config);
        }
        
        // Load from Windows Registry (CSP OMA-URI style credentials store)
        // This is the preferred method for developer workstations
        LoadFromRegistry(config);
        
        // Environment variables override everything (for CI/CD and temporary overrides)
        var envUrl = Environment.GetEnvironmentVariable("REPORTMATE_URL");
        if (!string.IsNullOrEmpty(envUrl))
            config.ReportMateUrl = envUrl;
        
        var envPassphrase = Environment.GetEnvironmentVariable("REPORTMATE_PASSPHRASE");
        if (!string.IsNullOrEmpty(envPassphrase))
            config.ReportMatePassphrase = envPassphrase;
        
        // Snipe-IT environment variables
        var snipeUrl = Environment.GetEnvironmentVariable("SNIPE_URL");
        if (!string.IsNullOrEmpty(snipeUrl))
            config.SnipeUrl = snipeUrl;
        
        var snipeApiKey = Environment.GetEnvironmentVariable("SNIPE_API_KEY");
        if (!string.IsNullOrEmpty(snipeApiKey))
            config.SnipeApiKey = snipeApiKey;
        
        // Try to find repo root
        config.RepoRoot = FindRepoRoot();
        
        return config;
    }
    
    /// <summary>
    /// Load credentials from Windows Registry (HKCU\SOFTWARE\FleetMate)
    /// This is the CSP OMA-URI style approach for persistent credential storage
    /// </summary>
    private static void LoadFromRegistry(FleetMateConfig config)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (key == null)
            {
                Log.Debug("Registry key not found: HKCU\\{Path}", RegistryPath);
                return;
            }
            
            // ReportMate credentials
            var reportMateUrl = key.GetValue("ReportMateUrl") as string;
            if (!string.IsNullOrEmpty(reportMateUrl))
                config.ReportMateUrl = reportMateUrl;
            
            var reportMatePassphrase = key.GetValue("ReportMatePassphrase") as string;
            if (!string.IsNullOrEmpty(reportMatePassphrase))
                config.ReportMatePassphrase = reportMatePassphrase;
            
            // Snipe-IT credentials
            var snipeUrl = key.GetValue("SnipeUrl") as string;
            if (!string.IsNullOrEmpty(snipeUrl))
                config.SnipeUrl = snipeUrl;
            
            var snipeApiKey = key.GetValue("SnipeApiKey") as string;
            if (!string.IsNullOrEmpty(snipeApiKey))
                config.SnipeApiKey = snipeApiKey;
            
            // Graph/Entra ID credentials
            config.Graph ??= new GraphConfig();
            var graphTenantId = key.GetValue("GraphTenantId") as string;
            if (!string.IsNullOrEmpty(graphTenantId))
                config.Graph.TenantId = graphTenantId;

            var graphClientId = key.GetValue("GraphClientId") as string;
            if (!string.IsNullOrEmpty(graphClientId))
            {
                config.Graph.ClientId = graphClientId;
                config.Graph.UseAzureCliAuth = false;
            }

            var graphClientSecret = key.GetValue("GraphClientSecret") as string;
            if (!string.IsNullOrEmpty(graphClientSecret))
            {
                config.Graph.ClientSecret = graphClientSecret;
                config.Graph.UseAzureCliAuth = false;
            }
            
            // TeamDynamix credentials
            config.Tdx ??= new TdxConfig();
            var tdxBaseUrl = key.GetValue("TdxBaseUrl") as string;
            if (!string.IsNullOrEmpty(tdxBaseUrl))
                config.Tdx.BaseUrl = tdxBaseUrl;
            
            var tdxAppId = key.GetValue("TdxAppId") as string;
            if (!string.IsNullOrEmpty(tdxAppId) && int.TryParse(tdxAppId, out var appId))
                config.Tdx.AppId = appId;
            
            var tdxUsername = key.GetValue("TdxUsername") as string;
            if (!string.IsNullOrEmpty(tdxUsername))
                config.Tdx.Username = tdxUsername;
            
            var tdxPassword = key.GetValue("TdxPassword") as string;
            if (!string.IsNullOrEmpty(tdxPassword))
                config.Tdx.Password = tdxPassword;
            
            var tdxBeid = key.GetValue("TdxBeid") as string;
            if (!string.IsNullOrEmpty(tdxBeid))
                config.Tdx.Beid = tdxBeid;
            
            var tdxWebServicesKey = key.GetValue("TdxWebServicesKey") as string;
            if (!string.IsNullOrEmpty(tdxWebServicesKey))
                config.Tdx.WebServicesKey = tdxWebServicesKey;
            
            Log.Debug("Loaded credentials from registry: HKCU\\{Path}", RegistryPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load credentials from registry");
        }
    }
    
    /// <summary>
    /// Save credentials to Windows Registry
    /// Called by 'fleetmate configure' command
    /// </summary>
    public static void SaveToRegistry(string? reportMateUrl, string? reportMatePassphrase, 
        string? snipeUrl = null, string? snipeApiKey = null)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            if (key == null)
            {
                Log.Error("Failed to create registry key: HKCU\\{Path}", RegistryPath);
                return;
            }
            
            if (!string.IsNullOrEmpty(reportMateUrl))
                key.SetValue("ReportMateUrl", reportMateUrl);
            
            if (!string.IsNullOrEmpty(reportMatePassphrase))
                key.SetValue("ReportMatePassphrase", reportMatePassphrase);
            
            if (!string.IsNullOrEmpty(snipeUrl))
                key.SetValue("SnipeUrl", snipeUrl);
            
            if (!string.IsNullOrEmpty(snipeApiKey))
                key.SetValue("SnipeApiKey", snipeApiKey);
            
            Log.Information("Saved credentials to registry: HKCU\\{Path}", RegistryPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save credentials to registry");
            throw;
        }
    }
    
    /// <summary>
    /// Check if registry credentials are configured
    /// </summary>
    public static bool HasRegistryCredentials()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (key == null) return false;
            
            var passphrase = key.GetValue("ReportMatePassphrase") as string;
            return !string.IsNullOrEmpty(passphrase);
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// Clear all FleetMate credentials from registry
    /// </summary>
    public static void ClearRegistry()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(RegistryPath, false);
            Log.Information("Cleared registry credentials: HKCU\\{Path}", RegistryPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to clear registry credentials");
        }
    }
    
    private static void LoadEnvFile(string path, FleetMateConfig config)
    {
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                    continue;
                
                var parts = trimmed.Split('=', 2);
                if (parts.Length != 2)
                    continue;
                
                var key = parts[0].Trim();
                var value = parts[1].Trim().Trim('"', '\'');
                
                switch (key.ToUpperInvariant())
                {
                    case "REPORTMATE_URL":
                        config.ReportMateUrl = value;
                        break;
                    case "REPORTMATE_PASSPHRASE":
                        config.ReportMatePassphrase = value;
                        break;
                    case "SNIPE_URL":
                        config.SnipeUrl = value;
                        break;
                    case "SNIPE_API_KEY":
                        config.SnipeApiKey = value;
                        break;
                    case "GRAPH_TENANT_ID":
                        config.Graph ??= new GraphConfig();
                        config.Graph.TenantId = value;
                        break;
                    case "GRAPH_CLIENT_ID":
                        config.Graph ??= new GraphConfig();
                        config.Graph.ClientId = value;
                        config.Graph.UseAzureCliAuth = false;
                        break;
                    case "GRAPH_CLIENT_SECRET":
                        config.Graph ??= new GraphConfig();
                        config.Graph.ClientSecret = value;
                        config.Graph.UseAzureCliAuth = false;
                        break;
                    case "GRAPH_USE_AZURE_CLI":
                        config.Graph ??= new GraphConfig();
                        if (bool.TryParse(value, out var useCli))
                        {
                            config.Graph.UseAzureCliAuth = useCli;
                        }
                        break;
                    case "TDX_BASE_URL":
                        config.Tdx ??= new TdxConfig();
                        config.Tdx.BaseUrl = value;
                        break;
                    case "TDX_APP_ID":
                        config.Tdx ??= new TdxConfig();
                        if (int.TryParse(value, out var appId))
                        {
                            config.Tdx.AppId = appId;
                        }
                        break;
                    case "TDX_USERNAME":
                        config.Tdx ??= new TdxConfig();
                        config.Tdx.Username = value;
                        break;
                    case "TDX_PASSWORD":
                        config.Tdx ??= new TdxConfig();
                        config.Tdx.Password = value;
                        break;
                    case "TDX_BEID":
                        config.Tdx ??= new TdxConfig();
                        config.Tdx.Beid = value;
                        break;
                    case "TDX_WEB_SERVICES_KEY":
                        config.Tdx ??= new TdxConfig();
                        config.Tdx.WebServicesKey = value;
                        break;
                    case "SECURE_SHELL_PRIVATE_KEY":
                        config.SecureShell ??= new SecureShellConfig();
                        config.SecureShell.PrivateKeyEnvVar = "SECURE_SHELL_PRIVATE_KEY";
                        Environment.SetEnvironmentVariable("SECURE_SHELL_PRIVATE_KEY", value);
                        break;
                }
            }
            Log.Debug("Loaded secrets from {Path}", path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load .env from {Path}", path);
        }
    }
    
    private static string? FindRepoRoot()
    {
        var current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, ".git")))
            {
                return current;
            }
            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }
        return null;
    }
    
    /// <summary>
    /// Get absolute path for a config-relative path
    /// </summary>
    public string ResolvePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            return relativePath;

        if (RepoRoot != null)
            return Path.Combine(RepoRoot, relativePath);

        return Path.Combine(Directory.GetCurrentDirectory(), relativePath);
    }
}

/// <summary>
/// Azure DevOps configuration for work item management
/// </summary>
public class AzureDevOpsConfig
{
    /// <summary>
    /// Azure DevOps organization name (e.g., "emilycarru-its-infra")
    /// </summary>
    public string? Organization { get; set; }

    /// <summary>
    /// Azure DevOps project name (e.g., "Devices")
    /// </summary>
    public string? Project { get; set; }

    /// <summary>
    /// Default work item type for created items
    /// </summary>
    public string DefaultWorkItemType { get; set; } = "Bug";

    /// <summary>
    /// Default iteration path (empty = current sprint)
    /// </summary>
    public string? DefaultIterationPath { get; set; }

    /// <summary>
    /// Cache duration for boards/sprints in minutes
    /// </summary>
    public int CacheMinutes { get; set; } = 30;

    /// <summary>
    /// Base URL for Azure DevOps API
    /// </summary>
    public string BaseUrl => $"https://dev.azure.com/{Organization}";
}
