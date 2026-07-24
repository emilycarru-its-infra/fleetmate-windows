using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using FleetMate.Core.Config;
using FleetMate.Core.Models;
using FleetMate.Core.Services;
using FleetMate.Core.Models.Devices;
using Serilog;
using System.Text.RegularExpressions;

namespace FleetMate.Core.Services.Devices;

/// <summary>
/// Service for understanding and interacting with the Cimian deployment system.
/// Provides knowledge of Cimian's structure for troubleshooting and fixing issues.
/// 
/// Key Cimian Concepts:
/// - pkgsinfo: YAML files describing packages (name, version, installer, installs array)
/// - catalogs: Generated from pkgsinfo via makecatalogs
/// - manifests: Define which packages go to which groups of devices
/// - pkgs: The actual installer files
/// 
/// On-Device Paths:
/// - C:\ProgramData\ManagedInstalls\Config.yaml - Device configuration
/// - C:\ProgramData\ManagedInstalls\cache - Downloaded installers
/// - C:\ProgramData\ManagedInstalls\logs - Installation logs
/// - HKLM\SOFTWARE\Cimian\Packages - Installed package versions
/// </summary>
public class CimianService
{
    private readonly FleetMateConfig _config;
    private readonly SecureShellService? _SecureShellService;
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;
    
    // Known browser packages that need preinstall scripts to close before update
    private static readonly HashSet<string> BrowserPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Chrome", "Firefox", "Edge", "Brave", "Opera", "Vivaldi"
    };
    
    // Packages that typically need silent install flags
    private static readonly Dictionary<string, string[]> CommonSilentFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        { "ZEDSDK", new[] { "S" } },  // ZED SDK uses /S
        { "Unity", new[] { "S" } },
        { "Blender", new[] { "S" } },
        { "OBS", new[] { "S" } },
    };

    public CimianService(FleetMateConfig config, SecureShellService? SecureShellService = null)
    {
        _config = config;
        _SecureShellService = SecureShellService;
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
    }

    #region Repo Discovery

    /// <summary>
    /// Get the Cimian repo path from local config or device config
    /// </summary>
    public async Task<string?> GetRepoPathAsync(string? deviceSerialNumber = null)
    {
        // First try local config
        if (!string.IsNullOrEmpty(_config.RepoRoot))
        {
            return _config.RepoRoot;
        }
        
        // Try to infer from current working directory
        var cwd = Directory.GetCurrentDirectory();
        var inferredRoot = FindRepoRoot(cwd);
        if (inferredRoot != null)
        {
            return inferredRoot;
        }
        
        // If device specified and SSH available, try to read device config
        if (!string.IsNullOrEmpty(deviceSerialNumber) && _SecureShellService != null)
        {
            try
            {
                var deviceConfig = await GetDeviceConfigAsync(deviceSerialNumber);
                if (deviceConfig != null && deviceConfig.TryGetValue("RepoPath", out var repoPath))
                {
                    return repoPath?.ToString();
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Failed to get repo path from device: {Error}", ex.Message);
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Find the repo root by looking for .git folder
    /// </summary>
    private static string? FindRepoRoot(string startPath)
    {
        var current = startPath;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, ".git")))
            {
                // Verify it's a Cimian repo by checking for deployment folder
                if (Directory.Exists(Path.Combine(current, "deployment")))
                {
                    return current;
                }
            }
            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }
        return null;
    }

    #endregion

    #region Device Configuration

    /// <summary>
    /// Get Cimian configuration from a device via SSH
    /// </summary>
    public async Task<Dictionary<string, object>?> GetDeviceConfigAsync(string deviceSerialNumber)
    {
        if (_SecureShellService == null)
        {
            throw new InvalidOperationException("SecureShell service not available");
        }
        
        var command = "powershell -c \"Get-Content 'C:\\ProgramData\\ManagedInstalls\\Config.yaml' -Raw\"";
        var result = await _SecureShellService.ExecuteAsync(deviceSerialNumber, command);
        
        if (result.ExitCode != 0 || string.IsNullOrEmpty(result.Stdout))
        {
            Log.Warning("Failed to read device config from {Device}", deviceSerialNumber);
            return null;
        }
        
        try
        {
            return _deserializer.Deserialize<Dictionary<string, object>>(result.Stdout);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to parse device config YAML from {Device}", deviceSerialNumber);
            return null;
        }
    }
    
    /// <summary>
    /// Get installed package versions from device registry
    /// </summary>
    public async Task<Dictionary<string, string>> GetInstalledPackagesAsync(string deviceSerialNumber)
    {
        if (_SecureShellService == null)
        {
            throw new InvalidOperationException("SecureShell service not available");
        }
        
        var command = @"powershell -c ""Get-ItemProperty 'HKLM:\SOFTWARE\Cimian\Packages\*' -ErrorAction SilentlyContinue | ForEach-Object { $_.PSChildName + '=' + $_.Version }""";
        var result = await _SecureShellService.ExecuteAsync(deviceSerialNumber, command);
        
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (result.ExitCode == 0 && !string.IsNullOrEmpty(result.Stdout))
        {
            foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split('=', 2);
                if (parts.Length == 2)
                {
                    packages[parts[0]] = parts[1];
                }
            }
        }
        
        return packages;
    }

    #endregion

    #region PkgInfo Analysis

    /// <summary>
    /// Analyze a pkginfo for common issues
    /// </summary>
    public CimianPackageAnalysis AnalyzePkgInfo(string pkgInfoPath)
    {
        var analysis = new CimianPackageAnalysis
        {
            PkgInfoPath = pkgInfoPath,
            Issues = new List<CimianIssue>()
        };
        
        try
        {
            var yaml = File.ReadAllText(pkgInfoPath);
            var data = _deserializer.Deserialize<Dictionary<string, object>>(yaml);
            
            analysis.Name = data.TryGetValue("name", out var name) ? name?.ToString() : null;
            analysis.Version = data.TryGetValue("version", out var version) ? version?.ToString() : null;
            
            // Check for browser packages without preinstall script
            if (analysis.Name != null && BrowserPackages.Contains(analysis.Name))
            {
                if (!data.ContainsKey("preinstall_script"))
                {
                    analysis.Issues.Add(new CimianIssue
                    {
                        Severity = CimianIssueSeverity.Warning,
                        Code = "BROWSER_NO_PREINSTALL",
                        Message = "Browser package without preinstall_script - updates may fail if browser is running",
                        Suggestion = "Add a preinstall_script to close the browser before update",
                        AutoFixAvailable = true
                    });
                }
            }
            
            // Check for EXE installers without silent flags
            if (data.TryGetValue("installer", out var installerObj) && 
                installerObj is Dictionary<object, object> installer)
            {
                var installerType = installer.TryGetValue("type", out var type) ? type?.ToString() : null;
                
                var hasSwitches = installer.ContainsKey("switches");
                
                if (installerType?.Equals("exe", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (!hasSwitches && analysis.Name != null && 
                        CommonSilentFlags.ContainsKey(analysis.Name))
                    {
                        var suggestedFlags = CommonSilentFlags[analysis.Name];
                        analysis.Issues.Add(new CimianIssue
                        {
                            Severity = CimianIssueSeverity.Error,
                            Code = "EXE_NO_SILENT_FLAGS",
                            Message = "EXE installer without silent flags - installation will likely fail",
                            Suggestion = $"Add installer.switches: [{string.Join(", ", suggestedFlags)}]",
                            AutoFixAvailable = true,
                            FixData = new Dictionary<string, object> { { "switches", suggestedFlags } }
                        });
                    }
                    else if (!hasSwitches)
                    {
                        analysis.Issues.Add(new CimianIssue
                        {
                            Severity = CimianIssueSeverity.Warning,
                            Code = "EXE_NO_SILENT_FLAGS",
                            Message = "EXE installer without silent flags - may require manual intervention",
                            Suggestion = "Consider adding installer.switches for silent installation"
                        });
                    }
                }
                
                // Check unattended_install flag consistency
                var hasUnattended = data.TryGetValue("unattended_install", out var unattended) && 
                                   unattended?.ToString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
                
                if (hasUnattended && !hasSwitches && installerType == "exe")
                {
                    analysis.Issues.Add(new CimianIssue
                    {
                        Severity = CimianIssueSeverity.Warning,
                        Code = "UNATTENDED_BUT_NO_SWITCHES",
                        Message = "unattended_install: true but no installer switches defined",
                        Suggestion = "Either add switches or verify the installer is truly unattended"
                    });
                }
            }
            
            // Check for missing installs array
            if (!data.ContainsKey("installs") && !data.ContainsKey("installcheck_script"))
            {
                analysis.Issues.Add(new CimianIssue
                {
                    Severity = CimianIssueSeverity.Warning,
                    Code = "NO_VERIFICATION",
                    Message = "No installs array or installcheck_script - installation cannot be verified",
                    Suggestion = "Add an installs array with file paths to verify installation"
                });
            }
        }
        catch (Exception ex)
        {
            analysis.Issues.Add(new CimianIssue
            {
                Severity = CimianIssueSeverity.Error,
                Code = "PARSE_ERROR",
                Message = $"Failed to parse pkginfo: {ex.Message}"
            });
        }
        
        return analysis;
    }
    
    /// <summary>
    /// Find all pkginfo files for a package name (all versions)
    /// </summary>
    public IEnumerable<string> FindAllVersions(string packageName, string? repoRoot = null)
    {
        var root = repoRoot ?? _config.RepoRoot ?? Directory.GetCurrentDirectory();
        var pkgsinfoPath = Path.Combine(root, "deployment", "pkgsinfo");
        
        if (!Directory.Exists(pkgsinfoPath))
        {
            yield break;
        }
        
        foreach (var file in Directory.EnumerateFiles(pkgsinfoPath, "*.yaml", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (fileName.Equals(packageName, StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith(packageName + "-", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    #endregion

    #region Auto-Fix Capabilities

    /// <summary>
    /// Generate a preinstall script to close a browser before update
    /// </summary>
    public string GenerateBrowserCloseScript(string browserName)
    {
        var processName = browserName.ToLowerInvariant() switch
        {
            "chrome" => "chrome",
            "firefox" => "firefox",
            "edge" => "msedge",
            "brave" => "brave",
            "opera" => "opera",
            "vivaldi" => "vivaldi",
            _ => browserName.ToLowerInvariant()
        };
        
        return $@"# Preinstall script to close {browserName} before update
# Generated by FleetMate

$processName = ""{processName}""
$process = Get-Process -Name $processName -ErrorAction SilentlyContinue

if ($process) {{
    Write-Host ""Closing $processName before update...""
    
    # Try graceful close first
    $process | ForEach-Object {{ $_.CloseMainWindow() | Out-Null }}
    Start-Sleep -Seconds 2
    
    # Force kill if still running
    $process = Get-Process -Name $processName -ErrorAction SilentlyContinue
    if ($process) {{
        Write-Host ""Force closing $processName...""
        $process | Stop-Process -Force
        Start-Sleep -Seconds 1
    }}
    
    Write-Host ""{browserName} closed successfully.""
}} else {{
    Write-Host ""{browserName} is not running.""
}}

exit 0
";
    }
    
    /// <summary>
    /// Add a preinstall script to a pkginfo file
    /// </summary>
    public async Task<bool> AddPreinstallScriptAsync(string pkgInfoPath, string script)
    {
        try
        {
            var yaml = await File.ReadAllTextAsync(pkgInfoPath);
            var data = _deserializer.Deserialize<Dictionary<string, object>>(yaml);
            
            if (data.ContainsKey("preinstall_script"))
            {
                Log.Warning("Pkginfo already has a preinstall_script: {Path}", pkgInfoPath);
                return false;
            }
            
            data["preinstall_script"] = script;
            
            var newYaml = _serializer.Serialize(data);
            await File.WriteAllTextAsync(pkgInfoPath, newYaml);
            
            Log.Information("Added preinstall_script to {Path}", pkgInfoPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to add preinstall script to {Path}", pkgInfoPath);
            return false;
        }
    }
    
    /// <summary>
    /// Add installer switches to a pkginfo file
    /// </summary>
    public async Task<bool> AddInstallerSwitchesAsync(string pkgInfoPath, string[] switches)
    {
        try
        {
            var yaml = await File.ReadAllTextAsync(pkgInfoPath);
            var lines = yaml.Split('\n').ToList();
            
            // Find the installer section and add switches
            var installerIndex = lines.FindIndex(l => l.TrimStart().StartsWith("installer:"));
            if (installerIndex == -1)
            {
                Log.Warning("No installer section found in {Path}", pkgInfoPath);
                return false;
            }
            
            // Find where to insert switches (after type or location)
            var insertIndex = installerIndex + 1;
            while (insertIndex < lines.Count)
            {
                var line = lines[insertIndex];
                if (!string.IsNullOrWhiteSpace(line) && 
                    !line.TrimStart().StartsWith("-") && 
                    char.IsLetter(line.TrimStart()[0]) &&
                    !line.TrimStart().StartsWith("  "))
                {
                    // Found next top-level key
                    break;
                }
                if (line.Contains("switches:"))
                {
                    Log.Warning("Pkginfo already has installer switches: {Path}", pkgInfoPath);
                    return false;
                }
                insertIndex++;
            }
            
            // Build switches YAML
            var switchesYaml = "  switches:";
            foreach (var sw in switches)
            {
                switchesYaml += $"\n    - {sw}";
            }
            
            lines.Insert(insertIndex, switchesYaml);
            
            await File.WriteAllTextAsync(pkgInfoPath, string.Join('\n', lines));
            Log.Information("Added installer switches to {Path}", pkgInfoPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to add installer switches to {Path}", pkgInfoPath);
            return false;
        }
    }

    #endregion

    #region Troubleshooting Helpers

    /// <summary>
    /// Get troubleshooting recommendations for a failed install
    /// </summary>
    public CimianTroubleshootingResult Troubleshoot(string packageName, string? errorMessage, string? repoRoot = null)
    {
        var result = new CimianTroubleshootingResult
        {
            PackageName = packageName,
            Recommendations = new List<string>(),
            AutoFixes = new List<CimianAutoFix>()
        };
        
        // Find the pkginfo
        var pkgInfoPath = FindAllVersions(packageName, repoRoot).OrderByDescending(p => p).FirstOrDefault();
        if (pkgInfoPath == null)
        {
            result.Recommendations.Add($"No pkginfo found for {packageName} in the repo");
            return result;
        }
        
        result.PkgInfoPath = pkgInfoPath;
        
        // Analyze the pkginfo
        var analysis = AnalyzePkgInfo(pkgInfoPath);
        
        // Add recommendations based on analysis
        foreach (var issue in analysis.Issues)
        {
            result.Recommendations.Add($"[{issue.Code}] {issue.Message}");
            if (issue.AutoFixAvailable)
            {
                result.AutoFixes.Add(new CimianAutoFix
                {
                    Description = issue.Suggestion ?? issue.Message,
                    IssueCode = issue.Code,
                    PkgInfoPath = pkgInfoPath
                });
            }
        }
        
        // Check error-specific recommendations
        if (!string.IsNullOrEmpty(errorMessage))
        {
            if (errorMessage.Contains("1603", StringComparison.OrdinalIgnoreCase))
            {
                result.Recommendations.Add("MSI Error 1603: Installation failed - often caused by:");
                result.Recommendations.Add("  - Application is running (add preinstall_script to close it)");
                result.Recommendations.Add("  - Pending reboot required");
                result.Recommendations.Add("  - Insufficient permissions");
                result.Recommendations.Add("  - Corrupted installer cache");
                
                if (BrowserPackages.Contains(packageName))
                {
                    result.AutoFixes.Add(new CimianAutoFix
                    {
                        Description = $"Add preinstall_script to close {packageName} before update",
                        IssueCode = "BROWSER_NO_PREINSTALL",
                        PkgInfoPath = pkgInfoPath
                    });
                }
            }
            else if (errorMessage.Contains("verification failed", StringComparison.OrdinalIgnoreCase))
            {
                result.Recommendations.Add("Installation verification failed:");
                result.Recommendations.Add("  - Installer may not be truly silent (check switches)");
                result.Recommendations.Add("  - Install path in 'installs' array may be wrong");
                result.Recommendations.Add("  - Installer may have been cancelled or timed out");
            }
        }
        
        return result;
    }

    /// <summary>
    /// Compare installed version on device with catalog version
    /// Returns: 
    ///   1 if device has newer version (catalog stale)
    ///   0 if versions match
    ///  -1 if device has older version (update available)
    /// </summary>
    public int CompareVersions(string? installedVersion, string? catalogVersion)
    {
        if (string.IsNullOrWhiteSpace(installedVersion) && string.IsNullOrWhiteSpace(catalogVersion))
            return 0;
        if (string.IsNullOrWhiteSpace(installedVersion))
            return -1;
        if (string.IsNullOrWhiteSpace(catalogVersion))
            return 1;
        
        // Normalize versions
        var installed = NormalizeVersion(installedVersion);
        var catalog = NormalizeVersion(catalogVersion);
        
        // Parse as Version objects
        if (Version.TryParse(installed, out var v1) && Version.TryParse(catalog, out var v2))
        {
            return v1.CompareTo(v2);
        }
        
        // Fallback to string comparison
        return string.Compare(installed, catalog, StringComparison.OrdinalIgnoreCase);
    }
    
    private static string NormalizeVersion(string version)
    {
        // Remove v prefix
        if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            version = version[1..];
        
        // Remove trailing .0 segments
        while (version.EndsWith(".0") && version.Count(c => c == '.') > 0)
        {
            version = version[..^2];
        }
        
        return version;
    }

    #endregion

    #region Push / Remote Trigger

    /// <summary>
    /// Result of a Cimian push operation on a single device
    /// </summary>
    public class CimianPushResult
    {
        public string DeviceIdentifier { get; set; } = string.Empty;
        public string? DeviceName { get; set; }
        public string Channel { get; set; } = string.Empty; // "SSH" or "Intune"
        public bool Success { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>
    /// Aggregate result of a batch push operation
    /// </summary>
    public class CimianPushBatchResult
    {
        public List<CimianPushResult> Results { get; set; } = new();
        public string Channel { get; set; } = string.Empty;
        public int SuccessCount => Results.Count(r => r.Success);
        public int FailedCount => Results.Count(r => !r.Success);
        public int TotalCount => Results.Count;
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Push a Cimian run to devices via SSH by creating the .cimian.headless trigger file.
    /// CimianWatcher polls every 10 seconds and will pick up the file to launch managedsoftwareupdate.
    /// </summary>
    public async Task<CimianPushBatchResult> PushViaSshAsync(IEnumerable<string> serialNumbers, string triggeredBy = "FleetMate SSH")
    {
        if (_SecureShellService == null)
        {
            throw new InvalidOperationException("SecureShell service not available");
        }

        var serials = serialNumbers.ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<CimianPushResult>();

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var triggerContent = $"Bootstrap triggered at: {timestamp}\\nMode: Headless\\nTriggered by: {triggeredBy}";
        var command = $"powershell -c \"$dir = 'C:\\ProgramData\\ManagedInstalls'; if (-not (Test-Path $dir)) {{ New-Item -ItemType Directory -Path $dir -Force | Out-Null }}; Set-Content -Path (Join-Path $dir '.cimian.headless') -Value '{triggerContent}' -Force; Write-Output 'OK'\"";

        Log.Information("Pushing Cimian run via SSH to {Count} device(s)", serials.Count);

        var batchResult = await _SecureShellService.ExecuteBatchAsync(serials, command);

        foreach (var sshResult in batchResult.Results)
        {
            var pushResult = new CimianPushResult
            {
                DeviceIdentifier = sshResult.Host,
                DeviceName = sshResult.DeviceName,
                Channel = "SSH",
                Success = sshResult.Success && sshResult.Stdout?.Trim() == "OK",
                Message = sshResult.Success
                    ? "Trigger file created, CimianWatcher will pick up within 10s"
                    : sshResult.Error?.Message ?? sshResult.Stderr ?? "SSH connection failed"
            };
            results.Add(pushResult);
        }

        sw.Stop();
        Log.Information("SSH push completed: {Success}/{Total} succeeded in {Duration}ms",
            results.Count(r => r.Success), results.Count, sw.ElapsedMilliseconds);

        return new CimianPushBatchResult
        {
            Results = results,
            Channel = "SSH",
            Duration = sw.Elapsed
        };
    }

    /// <summary>
    /// Push a Cimian run to devices via Intune proactive remediation.
    /// Deploys a remediation script that creates .cimian.headless on target devices.
    /// Optionally forces an Intune sync to expedite delivery.
    /// </summary>
    public async Task<CimianPushBatchResult> PushViaIntuneAsync(
        GraphService graphService,
        string groupNameOrId,
        bool forceSyncAfter = true)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<CimianPushResult>();

        Log.Information("Pushing Cimian run via Intune remediation to group '{Group}'", groupNameOrId);

        // Deploy the proactive remediation. This entry point is a deliberate,
        // explicitly-invoked push, so it confirms the destructive-action guard.
        var deployResult = await graphService.DeployCimianPushRemediationAsync(groupNameOrId, confirmed: true);

        if (!deployResult.Success)
        {
            sw.Stop();
            results.Add(new CimianPushResult
            {
                DeviceIdentifier = groupNameOrId,
                Channel = "Intune",
                Success = false,
                Message = $"Failed to deploy remediation: {deployResult.Message}"
            });
            return new CimianPushBatchResult { Results = results, Channel = "Intune", Duration = sw.Elapsed };
        }

        // Get group devices for reporting and optional sync
        var devices = await graphService.GetGroupDevicesAsync(groupNameOrId);

        if (devices.Count == 0)
        {
            Log.Warning("No managed devices found in group '{Group}'", groupNameOrId);
            results.Add(new CimianPushResult
            {
                DeviceIdentifier = groupNameOrId,
                Channel = "Intune",
                Success = true,
                Message = $"Remediation deployed ({deployResult.Message}) but no managed devices found in group"
            });
            sw.Stop();
            return new CimianPushBatchResult { Results = results, Channel = "Intune", Duration = sw.Elapsed };
        }

        // Force sync to expedite remediation pickup
        if (forceSyncAfter)
        {
            Log.Information("Forcing Intune sync on {Count} devices to expedite remediation", devices.Count);
            var syncResults = await graphService.SyncDevicesAsync(devices.Select(d => d.Id));

            foreach (var device in devices)
            {
                var syncResult = syncResults.FirstOrDefault(r => r.DeviceId == device.Id);
                results.Add(new CimianPushResult
                {
                    DeviceIdentifier = device.SerialNumber ?? device.Id,
                    DeviceName = device.DeviceName,
                    Channel = "Intune",
                    Success = syncResult?.Success ?? true,
                    Message = syncResult?.Success == true
                        ? "Remediation deployed + sync forced"
                        : $"Remediation deployed but sync failed: {syncResult?.Message}"
                });
            }
        }
        else
        {
            foreach (var device in devices)
            {
                results.Add(new CimianPushResult
                {
                    DeviceIdentifier = device.SerialNumber ?? device.Id,
                    DeviceName = device.DeviceName,
                    Channel = "Intune",
                    Success = true,
                    Message = "Remediation deployed, will execute at next Intune check-in"
                });
            }
        }

        sw.Stop();
        Log.Information("Intune push completed: {Count} devices targeted in {Duration}ms",
            devices.Count, sw.ElapsedMilliseconds);

        return new CimianPushBatchResult { Results = results, Channel = "Intune", Duration = sw.Elapsed };
    }

    /// <summary>
    /// Push a Cimian run to specific devices by serial number via Intune.
    /// Looks up each serial in Intune, then syncs those specific devices after deploying remediation.
    /// </summary>
    public async Task<CimianPushBatchResult> PushViaIntuneBySerialAsync(
        GraphService graphService,
        IEnumerable<string> serialNumbers,
        bool forceSyncAfter = true)
    {
        var serials = serialNumbers.ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<CimianPushResult>();

        Log.Information("Pushing Cimian run via Intune to {Count} device(s) by serial", serials.Count);

        // Resolve serial numbers to Intune device IDs
        var resolvedDevices = new List<IntuneDevice>();
        foreach (var serial in serials)
        {
            var device = await graphService.GetDeviceBySerialAsync(serial);
            if (device != null)
            {
                resolvedDevices.Add(device);
            }
            else
            {
                results.Add(new CimianPushResult
                {
                    DeviceIdentifier = serial,
                    Channel = "Intune",
                    Success = false,
                    Message = "Device not found in Intune"
                });
            }
        }

        if (resolvedDevices.Count == 0)
        {
            sw.Stop();
            return new CimianPushBatchResult { Results = results, Channel = "Intune", Duration = sw.Elapsed };
        }

        // For serial-targeted push, sync devices directly (remediation group targeting is optional)
        // The sync itself forces check-in; if a remediation is already deployed broadly, this triggers it
        if (forceSyncAfter)
        {
            var syncResults = await graphService.SyncDevicesAsync(resolvedDevices.Select(d => d.Id));

            foreach (var device in resolvedDevices)
            {
                var syncResult = syncResults.FirstOrDefault(r => r.DeviceId == device.Id);
                results.Add(new CimianPushResult
                {
                    DeviceIdentifier = device.SerialNumber ?? device.Id,
                    DeviceName = device.DeviceName,
                    Channel = "Intune",
                    Success = syncResult?.Success ?? true,
                    Message = syncResult?.Success == true
                        ? "Intune sync forced, remediation will execute on check-in"
                        : $"Sync failed: {syncResult?.Message}"
                });
            }
        }

        sw.Stop();
        return new CimianPushBatchResult { Results = results, Channel = "Intune", Duration = sw.Elapsed };
    }

    #endregion
}
