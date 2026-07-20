// FleetMate.Core/Models/Cimian/QaModels.cs
// QA system models - porting from control.ps1 quality control

namespace FleetMate.Core.Models.Devices;

/// <summary>
/// QA test category types matching control.ps1 categories
/// </summary>
public enum QaCategory
{
    All,
    Unit,
    Systems,
    Lint,
    Autofix,
    Deployment
}

/// <summary>
/// Package location type - where the package was found
/// </summary>
public enum PackageLocationType
{
    Local,          // ./packages or ./installers
    Deployment,     // ./deployment/pkgsinfo
    NotFound
}

/// <summary>
/// Package source directory
/// </summary>
public enum PackageSource
{
    Packages,       // ./packages directory
    Installers,     // ./installers directory
    Deployment      // ./deployment/pkgsinfo
}

/// <summary>
/// Installer type from pkginfo
/// </summary>
public enum InstallerType
{
    Unknown,
    Msi,
    Exe,
    Pkg,
    Nupkg,
    Script
}

/// <summary>
/// QA step result severity
/// </summary>
public enum QaSeverity
{
    Info,
    Warning,
    Error,
    Success
}

/// <summary>
/// Result of locating a package in the repository
/// </summary>
public class PackageLocation
{
    public string Name { get; set; } = "";
    public PackageLocationType Type { get; set; } = PackageLocationType.NotFound;
    public string Path { get; set; } = "";
    public string? YamlPath { get; set; }
    public string? PkgInfoPath { get; set; }  // Alias for YamlPath
    public PackageSource Source { get; set; }
    public string? Version { get; set; }
    public string? BasePackageName { get; set; }
    public List<string> AvailableVersions { get; set; } = new();
    public bool IsVersioned { get; set; }
    public bool Found => Type != PackageLocationType.NotFound;
}

/// <summary>
/// Individual QA step result
/// </summary>
public class QaStepResult
{
    public string StepName { get; set; } = "";
    public int StepNumber { get; set; }
    public bool Success { get; set; }
    public QaSeverity Severity { get; set; } = QaSeverity.Info;
    public List<string> Messages { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Output { get; set; } = new();
    public Dictionary<string, object> Details { get; set; } = new();
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Overall QA run result
/// </summary>
public class QaResult
{
    public string PackageName { get; set; } = "";
    public PackageLocation Location { get; set; } = new();
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration => EndTime - StartTime;
    
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public bool Success => Failed == 0 && Total > 0;
    
    // Rate over executed (non-skipped) steps, so a clean run with skipped steps reads 100%.
    public double SuccessRate => (Total - Skipped) > 0 ? Math.Round((double)Passed / (Total - Skipped) * 100, 1) : 0;
    
    public List<QaStepResult> Steps { get; set; } = new();
    
    public QaRunMode Mode { get; set; }
    public QaOptions Options { get; set; } = new();
}

/// <summary>
/// QA run mode
/// </summary>
public enum QaRunMode
{
    SinglePackage,
    AllPackages,
    Category,
    BulkRepackage,
    BulkImport,
    CheckInstallerType
}

/// <summary>
/// Options for running QA
/// </summary>
public class QaOptions
{
    public bool DryRun { get; set; }
    public bool Fix { get; set; }
    public bool ShowDetails { get; set; }
    public bool InstallOnly { get; set; }
    public bool UninstallFirst { get; set; }
    public QaCategory Category { get; set; } = QaCategory.All;
}

/// <summary>
/// Parsed pkginfo manifest (from YAML)
/// </summary>
public class PkgInfoManifest
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Description { get; set; }
    public string? Category { get; set; }
    public string? Developer { get; set; }
    public List<string> Catalogs { get; set; } = new();
    public List<string> SupportedArchitectures { get; set; } = new();
    public bool UnattendedInstall { get; set; }
    public bool UnattendedUninstall { get; set; }
    
    public InstallerInfo? Installer { get; set; }
    public List<InstallsItem> Installs { get; set; } = new();
    public List<UninstallerItem> Uninstaller { get; set; } = new();
    
    public string? PreinstallScript { get; set; }
    public string? PostinstallScript { get; set; }
    public string? PreuninstallScript { get; set; }
    public string? PostuninstallScript { get; set; }
    
    public string FilePath { get; set; } = "";
}

/// <summary>
/// Installer section from pkginfo
/// </summary>
public class InstallerInfo
{
    public string? Type { get; set; }
    public string? Location { get; set; }
    public long Size { get; set; }
    public string? Hash { get; set; }
    public List<string> Flags { get; set; } = new();
    public List<string> Switches { get; set; } = new();
}

/// <summary>
/// Installs array item from pkginfo
/// </summary>
public class InstallsItem
{
    public string? Path { get; set; }
    public string? Type { get; set; }
    public string? Version { get; set; }
    public string? Md5Checksum { get; set; }
}

/// <summary>
/// Uninstaller array item
/// </summary>
public class UninstallerItem
{
    public string? Type { get; set; }
    public string? Path { get; set; }
    public string? ProductCode { get; set; }
    public List<string>? Flags { get; set; }
    public List<string>? Switches { get; set; }
    public string? Script { get; set; }
}

/// <summary>
/// Build info from build-info.yaml
/// </summary>
public class BuildInfo
{
    public string? PackageName { get; set; }
    public string? Version { get; set; }
    public string? InstallLocation { get; set; }
    public string? PostinstallAction { get; set; }
    public ProductInfo? Product { get; set; }
}

/// <summary>
/// Product section from build-info.yaml
/// </summary>
public class ProductInfo
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? InstallLocation { get; set; }
}

/// <summary>
/// YAML validation result
/// </summary>
public class YamlValidationResult
{
    public bool IsValid { get; set; } = true;
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Info { get; set; } = new();
    public PkgInfoManifest? Manifest { get; set; }
}

/// <summary>
/// Installation test result
/// </summary>
public class InstallationTestResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public List<string> Output { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public string? InstallerPath { get; set; }
    public string CommandLine { get; set; } = "";
    public TimeSpan Duration { get; set; }
    
    // Pre/post script results
    public ScriptExecutionResult? PreinstallResult { get; set; }
    public ScriptExecutionResult? PostinstallResult { get; set; }
}

/// <summary>
/// Script execution result (for pre/post install scripts)
/// </summary>
public class ScriptExecutionResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public string Errors { get; set; } = "";
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Uninstall test result
/// </summary>
public class UninstallTestResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public List<string> Output { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public string Method { get; set; } = ""; // MSI, EXE, Script, etc.
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Post-installation validation result
/// </summary>
public class PostInstallValidationResult
{
    public bool Success { get; set; }
    public int TotalChecks { get; set; }
    public int PassedChecks { get; set; }
    public int FailedChecks { get; set; }
    public List<string> Messages { get; set; } = new();
    public List<InstallVerification> Verifications { get; set; } = new();
}

/// <summary>
/// Individual install verification
/// </summary>
public class InstallVerification
{
    public string Path { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Found { get; set; }
    public string? ExpectedVersion { get; set; }
    public string? ActualVersion { get; set; }
    public bool VersionMatch { get; set; }
}

/// <summary>
/// Package build result
/// </summary>
public class PackageBuildResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public string? OutputPackagePath { get; set; }
    public long PackageSize { get; set; }
    public TimeSpan Duration { get; set; }

    /// <summary>Non-fatal build note (e.g. signing failed locally but the artifact built).</summary>
    public string? Warning { get; set; }
}

/// <summary>
/// Bulk operation result (for RepkgInstallers, CimiImportAll)
/// </summary>
public class BulkOperationResult
{
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public TimeSpan Duration { get; set; }
    public List<PackageOperationResult> Packages { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Individual package operation result
/// </summary>
public class PackageOperationResult
{
    public string PackageName { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Success { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Installer type check result
/// </summary>
public class InstallerTypeCheckResult
{
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Fixed { get; set; }
    public List<InstallerTypeIssue> Issues { get; set; } = new();
    public List<string> InstallerPackages { get; set; } = new();
}

/// <summary>
/// Issue found during installer type check
/// </summary>
public class InstallerTypeIssue
{
    public string Package { get; set; } = "";
    public string Path { get; set; } = "";
    public string Location { get; set; } = "";
    public string Issue { get; set; } = "";
    public List<string> InstallerFiles { get; set; } = new();
    public string? BuildInfoPath { get; set; }
}
