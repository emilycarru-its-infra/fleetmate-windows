using System.Text.Json.Serialization;
using FleetMate.Core.Converters;

namespace FleetMate.Core.Models.Reporting;

/// <summary>
/// Represents an installation record from ReportMate /api/devices/installs endpoint
/// </summary>
public class InstallRecord
{
    public string Id { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    
    [JsonConverter(typeof(NullableDateTimeConverter))]
    public DateTime? LastSeen { get; set; }
    
    [JsonConverter(typeof(NullableDateTimeConverter))]
    public DateTime? CollectedAt { get; set; }
    
    public string ItemName { get; set; } = string.Empty;
    public string CurrentStatus { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public string InstalledVersion { get; set; } = string.Empty;
    
    [JsonConverter(typeof(NullableDateTimeConverter))]
    public DateTime? InstallDate { get; set; }
    
    public string Usage { get; set; } = string.Empty;
    public string Catalog { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    
    /// <summary>
    /// Raw nested object containing detailed error info
    /// </summary>
    public InstallRawInfo? Raw { get; set; }
    
    /// <summary>
    /// Convenience property to get the error message
    /// </summary>
    [JsonIgnore]
    public string? LastError => Raw?.LastError;
    
    /// <summary>
    /// Convenience property to get the warning message
    /// </summary>
    [JsonIgnore]
    public string? LastWarning => Raw?.LastWarning;
    
    /// <summary>
    /// Whether this record represents an error state
    /// </summary>
    [JsonIgnore]
    public bool IsError => CurrentStatus?.ToLowerInvariant() is "failed" or "error" or "needs_reinstall";
    
    /// <summary>
    /// Categorizes the error type for grouping/analysis
    /// </summary>
    [JsonIgnore]
    public ErrorCategory Category => CategorizeError();
    
    private ErrorCategory CategorizeError()
    {
        if (string.IsNullOrEmpty(LastError))
            return ErrorCategory.Unknown;
        
        var error = LastError.ToLowerInvariant();
        
        if (error.Contains("404") || error.Contains("file not found") || error.Contains("resource may have been moved"))
            return ErrorCategory.NotFound;
        
        if (error.Contains("hash validation failed"))
            return ErrorCategory.HashMismatch;
        
        if (error.Contains("action failed after") || error.Contains("download"))
            return ErrorCategory.DownloadFailed;
        
        if (error.Contains("msi installation failed") || error.Contains("exit code 1603"))
            return ErrorCategory.MsiFailure;
        
        if (error.Contains("not signed") || error.Contains("signature verification"))
            return ErrorCategory.SignatureRequired;
        
        if (error.Contains("not found in any catalog"))
            return ErrorCategory.CatalogMissing;
        
        if (error.Contains("sbin-installer") && error.Contains("choco"))
            return ErrorCategory.MissingChocolatey;
        
        if (error.Contains("sbin-installer not available") || error.Contains("require sbin-installer"))
            return ErrorCategory.MissingSbinInstaller;
        
        if (error.Contains("verification failed") || error.Contains("cancelled") || error.Contains("failed silently"))
            return ErrorCategory.InstallVerificationFailed;
        
        if (error.Contains("no installer location"))
            return ErrorCategory.MissingInstallerLocation;
        
        return ErrorCategory.Other;
    }
}

/// <summary>
/// Raw installation details from ReportMate API
/// </summary>
public class InstallRawInfo
{
    public string Id { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string ItemType { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    
    public string LastError { get; set; } = string.Empty;
    public string LastWarning { get; set; } = string.Empty;
    
    [JsonConverter(typeof(NullableDateTimeConverter))]
    public DateTime? LastUpdate { get; set; }
    
    [JsonConverter(typeof(NullableDateTimeConverter))]
    public DateTime? LastAttemptTime { get; set; }
    
    public string LastAttemptStatus { get; set; } = string.Empty;
    
    public string CurrentStatus { get; set; } = string.Empty;
    public string MappedStatus { get; set; } = string.Empty;
    public string InstallMethod { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public string InstalledVersion { get; set; } = string.Empty;
    
    public int UpdateCount { get; set; }
    public int InstallCount { get; set; }
    public int FailureCount { get; set; }
    public int WarningCount { get; set; }
    public int TotalSessions { get; set; }
    
    public bool HasInstallLoop { get; set; }
    public bool InstallLoopDetected { get; set; }
    
    [JsonConverter(typeof(NullableDateTimeConverter))]
    public DateTime? LastSeenInSession { get; set; }
}

/// <summary>
/// Categorization of error types for analysis and remediation
/// </summary>
public enum ErrorCategory
{
    Unknown,
    NotFound,               // 404 - package file missing from CDN
    HashMismatch,           // Downloaded file hash doesn't match pkginfo
    DownloadFailed,         // Generic download failure after retries
    MsiFailure,             // MSI exit code 1603 (often permissions or conflicts)
    SignatureRequired,      // Package not signed but signature required
    CatalogMissing,         // Item not found in any catalog
    MissingChocolatey,      // Chocolatey not installed for nupkg
    MissingSbinInstaller,   // sbin-installer binary not available
    InstallVerificationFailed, // postinstall verification failed
    MissingInstallerLocation,  // pkginfo missing installer_location
    Other                   // Uncategorized error
}

/// <summary>
/// Summary of errors grouped for display
/// </summary>
public class ErrorSummary
{
    public string ItemName { get; set; } = string.Empty;
    public int DeviceCount { get; set; }
    public ErrorCategory Category { get; set; }
    public string SampleError { get; set; } = string.Empty;
    public List<string> AffectedDevices { get; set; } = new();
}

/// <summary>
/// Summary of errors by device
/// </summary>
public class DeviceErrorSummary
{
    public string DeviceName { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public int ErrorCount { get; set; }
    public List<string> FailedItems { get; set; } = new();
    public DateTime? LastSeen { get; set; }
}
