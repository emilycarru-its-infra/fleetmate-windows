using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Serilog;

namespace FleetMate.Config;

/// <summary>
/// Configuration for FleetMate
/// </summary>
public class FleetMateConfig
{
    // ReportMate API settings
    public string ReportMateUrl { get; set; } = "https://reportmate-functions-api.blackdune-79551938.canadacentral.azurecontainerapps.io";
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
        
        // Environment variables override config file
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
