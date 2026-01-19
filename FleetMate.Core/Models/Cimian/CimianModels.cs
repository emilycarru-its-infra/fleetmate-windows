namespace FleetMate.Models.Cimian;

/// <summary>
/// Analysis result for a Cimian package
/// </summary>
public class CimianPackageAnalysis
{
    public string? PkgInfoPath { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public List<CimianIssue> Issues { get; set; } = new();
    
    public bool HasErrors => Issues.Any(i => i.Severity == CimianIssueSeverity.Error);
    public bool HasWarnings => Issues.Any(i => i.Severity == CimianIssueSeverity.Warning);
    public bool HasAutoFixes => Issues.Any(i => i.AutoFixAvailable);
}

/// <summary>
/// A detected issue in a Cimian package
/// </summary>
public class CimianIssue
{
    public CimianIssueSeverity Severity { get; set; }
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Suggestion { get; set; }
    public bool AutoFixAvailable { get; set; }
    public Dictionary<string, object>? FixData { get; set; }
}

/// <summary>
/// Severity levels for Cimian issues
/// </summary>
public enum CimianIssueSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// Troubleshooting result for a failed package install
/// </summary>
public class CimianTroubleshootingResult
{
    public string PackageName { get; set; } = "";
    public string? PkgInfoPath { get; set; }
    public List<string> Recommendations { get; set; } = new();
    public List<CimianAutoFix> AutoFixes { get; set; } = new();
}

/// <summary>
/// An auto-fix action that can be applied
/// </summary>
public class CimianAutoFix
{
    public string Description { get; set; } = "";
    public string IssueCode { get; set; } = "";
    public string? PkgInfoPath { get; set; }
    public Dictionary<string, object>? Parameters { get; set; }
}

/// <summary>
/// Cimian device configuration (from C:\ProgramData\ManagedInstalls\Config.yaml)
/// </summary>
public class CimianDeviceConfig
{
    public string? ClientIdentifier { get; set; }
    public string? SoftwareRepoUrl { get; set; }
    public string? RepoPath { get; set; }
    public List<string>? Catalogs { get; set; }
    public string? LogLevel { get; set; }
    public bool? SuppressAutoInstall { get; set; }
    public int? DaysBetweenNotifications { get; set; }
    public int? InstallAppleSoftwareUpdates { get; set; }
    public string? ManagedInstallDir { get; set; }
    
    /// <summary>
    /// Parse catalog list from ClientIdentifier
    /// ClientIdentifier format: Usage/Catalog/Area/Location/Name
    /// </summary>
    public string? GetCatalogFromIdentifier()
    {
        if (string.IsNullOrEmpty(ClientIdentifier))
            return null;
        
        var parts = ClientIdentifier.Split('/');
        return parts.Length >= 2 ? parts[1] : null;
    }
}

/// <summary>
/// A catalog item from Cimian's catalog.yaml
/// </summary>
public class CimianCatalogItem
{
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }
    public string Version { get; set; } = "";
    public List<string>? Catalogs { get; set; }
    public string? Category { get; set; }
    public string? Description { get; set; }
    public string? Developer { get; set; }
    public CimianInstaller? Installer { get; set; }
    public List<CimianInstallItem>? Installs { get; set; }
    public List<string>? SupportedArchitectures { get; set; }
    public bool UnattendedInstall { get; set; }
    public bool UnattendedUninstall { get; set; }
    public string? PreinstallScript { get; set; }
    public string? PostinstallScript { get; set; }
    public string? UninstallScript { get; set; }
    public string? InstallcheckScript { get; set; }
    public List<string>? Requires { get; set; }
    public List<string>? UpdateFor { get; set; }
    public bool? OnDemand { get; set; }
    public string? MinimumOsVersion { get; set; }
    public string? MaximumOsVersion { get; set; }
    
    // Conditional install support
    public List<CimianCondition>? ConditionalItems { get; set; }
}

/// <summary>
/// Installer configuration
/// </summary>
public class CimianInstaller
{
    public string? Type { get; set; }
    public string? Location { get; set; }
    public string? Hash { get; set; }
    public long? Size { get; set; }
    public List<string>? Switches { get; set; }
    public string? ProductCode { get; set; }
}

/// <summary>
/// An install verification item
/// </summary>
public class CimianInstallItem
{
    public string Type { get; set; } = "file";
    public string? Path { get; set; }
    public string? Version { get; set; }
    public string? Md5checksum { get; set; }
    public string? ProductCode { get; set; }
}

/// <summary>
/// A conditional install condition
/// </summary>
public class CimianCondition
{
    public string Condition { get; set; } = "";
    public string? ManagedInstall { get; set; }
    public string? ManagedUninstall { get; set; }
}

/// <summary>
/// A Cimian manifest
/// </summary>
public class CimianManifest
{
    public string? DisplayName { get; set; }
    public List<string>? Catalogs { get; set; }
    public List<string>? IncludedManifests { get; set; }
    public List<string>? ManagedInstalls { get; set; }
    public List<string>? ManagedUninstalls { get; set; }
    public List<string>? OptionalInstalls { get; set; }
    public List<CimianCondition>? ConditionalItems { get; set; }
}

/// <summary>
/// Summary of a Cimian repo
/// </summary>
public class CimianRepoSummary
{
    public string RepoPath { get; set; } = "";
    public int TotalPackages { get; set; }
    public int TotalManifests { get; set; }
    public int TotalCatalogs { get; set; }
    public Dictionary<string, int> PackagesByCategory { get; set; } = new();
    public Dictionary<string, int> PackagesByCatalog { get; set; } = new();
    public List<string> RecentlyModified { get; set; } = new();
}
