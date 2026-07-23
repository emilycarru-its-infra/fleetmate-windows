using Microsoft.Win32;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Serilog;
using FleetMate.Core.Models;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Identity;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Models.Projects;

namespace FleetMate.Core.Config;

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
/// - DEVOPS_CLIENT_ID: Azure AD app client ID for OAuth2 SSO (optional)
/// - DEVOPS_TENANT_ID: Azure AD tenant ID for OAuth2 SSO (optional)
/// NOTE: NO PAT (Personal Access Token) — Azure DevOps uses SSO only.
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

    // Cimian tooling. cimipkg builds .msi by default (.pkg/.nupkg are fallbacks);
    // sbin-installer installs .pkg/.nupkg, msiexec installs .msi.
    public string SbinInstallerPath { get; set; } = @"C:\Program Files\sbin\installer.exe";
    
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

    // aze secretless elevation infrastructure (env/org-specific; no hardcoded defaults)
    public ElevationConfig? Elevation { get; set; }

    // TeamDynamix Configuration
    public TdxConfig? Tdx { get; set; }

    // Tasks Configuration (multi-provider task management)
    public TasksConfig? Tasks { get; set; }

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
            
            // Azure DevOps credentials
            config.AzureDevOps ??= new AzureDevOpsConfig();
            var devOpsOrganization = key.GetValue("DevOpsOrganization") as string;
            if (!string.IsNullOrEmpty(devOpsOrganization))
                config.AzureDevOps.Organization = devOpsOrganization;
            
            var devOpsProject = key.GetValue("DevOpsProject") as string;
            if (!string.IsNullOrEmpty(devOpsProject))
                config.AzureDevOps.Project = devOpsProject;

            var devOpsClientId = key.GetValue("DevOpsClientId") as string;
            if (!string.IsNullOrEmpty(devOpsClientId))
                config.AzureDevOps.ClientId = devOpsClientId;

            var devOpsTenantId = key.GetValue("DevOpsTenantId") as string;
            if (!string.IsNullOrEmpty(devOpsTenantId))
                config.AzureDevOps.TenantId = devOpsTenantId;

            // aze secretless elevation infrastructure (org-specific). Without these
            // keys, elevation is unconfigured and every Graph call fails as a masked
            // BadGateway — so managed boxes provisioned via the registry get them here,
            // the same way the Mac gets them from ~/.fleetmate/config.yaml.
            var elevationResourceGroup = key.GetValue("ElevationResourceGroup") as string;
            var elevationAcrImage = key.GetValue("ElevationAcrImage") as string;
            var elevationTranscriptAccount = key.GetValue("ElevationTranscriptAccount") as string;
            var elevationIdentityPrefix = key.GetValue("ElevationIdentityPrefix") as string;
            if (!string.IsNullOrEmpty(elevationResourceGroup) || !string.IsNullOrEmpty(elevationAcrImage) ||
                !string.IsNullOrEmpty(elevationTranscriptAccount) || !string.IsNullOrEmpty(elevationIdentityPrefix))
            {
                config.Elevation ??= new ElevationConfig();
                if (!string.IsNullOrEmpty(elevationResourceGroup)) config.Elevation.ResourceGroup = elevationResourceGroup;
                if (!string.IsNullOrEmpty(elevationAcrImage)) config.Elevation.AcrImage = elevationAcrImage;
                if (!string.IsNullOrEmpty(elevationTranscriptAccount)) config.Elevation.TranscriptAccount = elevationTranscriptAccount;
                if (!string.IsNullOrEmpty(elevationIdentityPrefix)) config.Elevation.IdentityPrefix = elevationIdentityPrefix;
                if (key.GetValue("ElevationDefaultTtlHours") is string ttlRaw && int.TryParse(ttlRaw, out var ttl))
                    config.Elevation.DefaultTtlHours = ttl;
            }

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
    /// Azure DevOps organization name (e.g., "contoso")
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
    /// Azure AD client ID for OAuth2 PKCE SSO authentication (optional)
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Azure AD tenant ID for OAuth2 PKCE SSO authentication (optional)
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Base URL for Azure DevOps API
    /// </summary>
    public string BaseUrl => $"https://dev.azure.com/{Organization}";

    /// <summary>
    /// Package-readiness board sync (fleetmate test/qa -> DevOps work items).
    /// </summary>
    public PackageReadinessConfig PackageReadiness { get; set; } = new();
}

/// <summary>
/// Configuration for syncing QA results to an Azure DevOps board as work items.
/// One upserted work item per package; its State reflects the latest QA outcome.
/// Everything is config-driven (no hardcoded board/type/state) so it ports to any
/// process. Sync is a no-op unless AzureDevOps.Organization and .Project are set.
/// </summary>
public class PackageReadinessConfig
{
    /// <summary>Master switch. When false, test/qa never touch the board.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Work item type to create/update (e.g. "Issue", "Task", "Bug").</summary>
    public string WorkItemType { get; set; } = "Issue";

    /// <summary>Title prefix; the package name is appended to form the (unique) title.</summary>
    public string TitlePrefix { get; set; } = "[Package Readiness]";

    /// <summary>Tag applied to every readiness item (used to find them again).</summary>
    public string Tag { get; set; } = "PackageReadiness";

    /// <summary>Optional area path for created items (e.g. "Devices\\Windows"). Null = project default.</summary>
    public string? AreaPath { get; set; }

    /// <summary>Optional iteration path for created items (e.g. "Devices\\Fall '26 Term"). Null = default.</summary>
    public string? IterationPath { get; set; }

    /// <summary>Board State for a passing package.</summary>
    public string StatePassed { get; set; } = "Done";

    /// <summary>Board State for a package with warnings.</summary>
    public string StateWarning { get; set; } = "Doing";

    /// <summary>Board State for a failing package.</summary>
    public string StateFailed { get; set; } = "Planned";

    /// <summary>Board State for an untested/unknown package.</summary>
    public string StateUntested { get; set; } = "Planned";
}

/// <summary>
/// Configuration for multi-provider task management
/// </summary>
public class TasksConfig
{
    /// <summary>
    /// Task provider configurations
    /// </summary>
    public TaskProvidersConfig Providers { get; set; } = new();

    /// <summary>
    /// Microsoft Planner sync configuration (one-way push)
    /// </summary>
    public PlannerSyncConfig? Planner { get; set; }

    /// <summary>
    /// Markdown file sync configuration
    /// </summary>
    public MarkdownSyncConfig? Markdown { get; set; }

    /// <summary>
    /// Default provider for task creation when not specified
    /// </summary>
    public string DefaultProvider { get; set; } = "azdevops";

    /// <summary>
    /// Alias for Planner sync configuration
    /// </summary>
    public PlannerSyncConfig? PlannerSync { get => Planner; set => Planner = value; }

    /// <summary>
    /// Alias for Markdown sync configuration
    /// </summary>
    public MarkdownSyncConfig? MarkdownSync { get => Markdown; set => Markdown = value; }
}

/// <summary>
/// Configuration for individual task providers
/// </summary>
public class TaskProvidersConfig
{
    /// <summary>
    /// Azure DevOps task provider configuration
    /// </summary>
    public AzureDevOpsProviderConfig? AzDevOps { get; set; }

    /// <summary>
    /// GitHub task provider configuration
    /// </summary>
    public GitHubProviderConfig? GitHub { get; set; }

    /// <summary>
    /// Gitea task provider configuration
    /// </summary>
    public GiteaProviderConfig? Gitea { get; set; }
}

/// <summary>
/// Azure DevOps provider configuration for tasks
/// </summary>
public class AzureDevOpsProviderConfig
{
    /// <summary>Whether this provider is enabled</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Override organization (uses AzureDevOps.Organization if not set)</summary>
    public string? Organization { get; set; }

    /// <summary>Override project (uses AzureDevOps.Project if not set)</summary>
    public string? Project { get; set; }

    /// <summary>Default work item type for created tasks</summary>
    public string DefaultWorkItemType { get; set; } = "Bug";

    /// <summary>Area path for created tasks</summary>
    public string? AreaPath { get; set; }

    /// <summary>Iteration path for created tasks (empty = current sprint)</summary>
    public string? IterationPath { get; set; }
}

/// <summary>
/// GitHub provider configuration for tasks
/// </summary>
public class GitHubProviderConfig
{
    /// <summary>Whether this provider is enabled</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Repository owner (user or organization)</summary>
    public string? Owner { get; set; }

    /// <summary>Repository name</summary>
    public string? Repo { get; set; }

    /// <summary>Personal access token (or use 'gh auth token')</summary>
    public string? Token { get; set; }

    /// <summary>Default labels to apply to created issues</summary>
    public List<string> DefaultLabels { get; set; } = new();

    /// <summary>Use GitHub CLI for authentication if token not provided</summary>
    public bool UseGhCli { get; set; } = true;

    /// <summary>GitHub OAuth App client ID for Device Flow authentication.
    /// Register at github.com/settings/applications/new with "Device" grant type enabled.</summary>
    public string? OauthClientId { get; set; }

    /// <summary>Organization name for org-level Projects v2 queries</summary>
    public string? Organization { get; set; }

    /// <summary>Default project number to use</summary>
    public int? ProjectNumber { get; set; }

    /// <summary>Default scope for project queries (organization, user, repository)</summary>
    public string ProjectScope { get; set; } = "organization";
}

/// <summary>
/// Gitea provider configuration for tasks
/// </summary>
public class GiteaProviderConfig
{
    /// <summary>Whether this provider is enabled</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Gitea server URL (e.g., "https://git.example.com")</summary>
    public string? Url { get; set; }

    /// <summary>Repository owner</summary>
    public string? Owner { get; set; }

    /// <summary>Repository name</summary>
    public string? Repo { get; set; }

    /// <summary>API token</summary>
    public string? Token { get; set; }

    /// <summary>Default labels to apply to created issues</summary>
    public List<string> DefaultLabels { get; set; } = new();
}

/// <summary>
/// Microsoft Planner sync configuration (one-way push)
/// </summary>
public class PlannerSyncConfig
{
    /// <summary>Whether Planner sync is enabled (deprecated — use GitHub Projects v2 instead)</summary>
    [Obsolete("Planner sync is deprecated. Use GitHub Projects v2 via the 'projects' command.")]
    public bool Enabled { get; set; } = false;

    /// <summary>Planner plan ID to sync tasks to</summary>
    public string? PlanId { get; set; }

    /// <summary>Default bucket ID for new tasks (null = first available)</summary>
    public string? DefaultBucketId { get; set; }

    /// <summary>Path to store sync state mapping file</summary>
    public string StatePath { get; set; } = "~/.fleetmate/planner-sync-state.json";
}

/// <summary>
/// Markdown file sync configuration
/// </summary>
public class MarkdownSyncConfig
{
    /// <summary>Whether markdown sync is enabled</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Path to the planning repo/directory</summary>
    public string? RepoPath { get; set; }

    /// <summary>Subdirectory for board markdown files</summary>
    public string BoardsPath { get; set; } = "boards";

    /// <summary>Subdirectory for individual task markdown files</summary>
    public string TasksPath { get; set; } = "tasks";

    /// <summary>Watch for file changes and auto-sync</summary>
    public bool WatchForChanges { get; set; } = true;

    /// <summary>Path to the markdown file (single file mode)</summary>
    public string? FilePath { get; set; }
}

/// <summary>
/// aze secretless elevation infrastructure. All values are environment/org-specific
/// and come from app settings (config.yaml / registry / env) — there are no hardcoded
/// defaults. Elevation fails fast with a clear message when these are unset.
/// </summary>
public class ElevationConfig
{
    /// <summary>Resource group holding the elevation session containers AND the per-domain managed identities.</summary>
    public string? ResourceGroup { get; set; }

    /// <summary>Container image for the elevation session (e.g. an ACR image reference).</summary>
    public string? AcrImage { get; set; }

    /// <summary>Storage account name for elevation transcripts (passed into the container).</summary>
    public string? TranscriptAccount { get; set; }

    /// <summary>Managed-identity name prefix; the per-domain identity is {Prefix}{Domain} (e.g. prefix "DevOps-" → "DevOps-Devices").</summary>
    public string? IdentityPrefix { get; set; }

    /// <summary>Default elevation session TTL in hours.</summary>
    public int DefaultTtlHours { get; set; } = 8;

    /// <summary>True only when every required field is set — elevation refuses to run otherwise.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ResourceGroup) &&
        !string.IsNullOrWhiteSpace(AcrImage) &&
        !string.IsNullOrWhiteSpace(TranscriptAccount) &&
        !string.IsNullOrWhiteSpace(IdentityPrefix);
}

