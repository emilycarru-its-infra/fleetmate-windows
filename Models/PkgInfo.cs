namespace FleetMate.Models;

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
    public InstallerInfo? Installer { get; set; }
    
    // Scripts
    public string? PreinstallScript { get; set; }
    public string? PostinstallScript { get; set; }
    public string? InstallcheckScript { get; set; }
    public string? UninstallcheckScript { get; set; }
    
    // Uninstaller
    public List<UninstallerInfo>? Uninstaller { get; set; }
    
    // Dependencies
    public List<string>? Requires { get; set; }
    public List<string>? UpdateFor { get; set; }
    
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
public class InstallerInfo
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
public class UninstallerInfo
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
public class ValidationIssue
{
    public ValidationSeverity Severity { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Field { get; set; }
    public string? Suggestion { get; set; }
}

public enum ValidationSeverity
{
    Info,
    Warning,
    Error
}
