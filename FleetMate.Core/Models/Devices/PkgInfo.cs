namespace FleetMate.Core.Models.Devices;

/// <summary>
/// Represents a parsed pkginfo YAML file
/// </summary>
public class PkgInfo
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Catalogs { get; set; } = new();
    public List<string> SupportedArchitectures { get; set; } = new();
    
    // Installer info
    public PkgInstallerInfo? Installer { get; set; }
    
    // Scripts
    public string? PreinstallScript { get; set; }
    public string? PostinstallScript { get; set; }
    public string? InstallcheckScript { get; set; }
    public string? UninstallcheckScript { get; set; }
    
    // Uninstaller
    public List<PkgUninstallerInfo>? Uninstaller { get; set; }
    
    // Dependencies
    public List<string>? Requires { get; set; }
    public List<string>? UpdateFor { get; set; }
    public List<string>? BlockingApplications { get; set; }
    
    // Metadata
    public string? Category { get; set; }
    public string? Developer { get; set; }
    public bool? AutoRemove { get; set; }
    public bool? Unattended { get; set; }
    public bool? ForceInstallAfterDate { get; set; }
    
    // File path where this pkginfo was loaded from
    public string? FilePath { get; set; }
}

/// <summary>
/// Installer section of pkginfo
/// </summary>
public class PkgInstallerInfo
{
    public string Type { get; set; } = string.Empty;  // msi, exe, nupkg, pkg, etc.
    public string? Location { get; set; }
    public string? Hash { get; set; }
    public long? Size { get; set; }
    public List<string>? Arguments { get; set; }
    public List<string>? Flags { get; set; }
    public List<string>? Switches { get; set; }
}

/// <summary>
/// Uninstaller entry in pkginfo
/// </summary>
public class PkgUninstallerInfo
{
    public string Type { get; set; } = string.Empty;  // msi, exe, script, etc.
    public string? ProductCode { get; set; }
    public string? Path { get; set; }
    public List<string>? Arguments { get; set; }
    public string? Script { get; set; }
}

/// <summary>
/// Result of pkginfo validation
/// </summary>
public class PkgInfoValidation
{
    public string FilePath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public List<ValidationIssue> Issues { get; set; } = new();
}

/// <summary>
/// A validation issue found in pkginfo
/// </summary>
public class PkgValidationIssue
{
    public PkgValidationSeverity Severity { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Field { get; set; }
    public string? Suggestion { get; set; }
}

/// <summary>
/// Validation issue for PkgInfoService compatibility
/// </summary>
public class ValidationIssue
{
    public ValidationSeverity Severity { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Field { get; set; }
    public string? Suggestion { get; set; }
}

public enum PkgValidationSeverity
{
    Info,
    Warning,
    Error
}

public enum ValidationSeverity
{
    Info,
    Warning,
    Error
}
