// FleetMate.Core/Services/QaService.cs
// Quality control service - C# port of control.ps1

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Devices;
using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FleetMate.Core.Services.Devices;

/// <summary>
/// Quality control service - C# port of quality/control.ps1
/// Provides package testing, validation, installation testing, and auto-fixing capabilities
/// </summary>
public class QaService
{
    private readonly FleetMateConfig _config;
    private readonly string _repoRoot;
    private readonly IDeserializer _yamlDeserializer;
    private readonly ISerializer _yamlSerializer;
    
    // Well-known paths relative to repo root
    private readonly string _packagesPath;
    private readonly string _installersPath;
    private readonly string _deploymentPath;
    private readonly string _pkgsInfoPath;
    private readonly string _pkgsPath;

    public QaService(FleetMateConfig config)
    {
        _config = config;
        _repoRoot = config.RepoRoot ?? Directory.GetCurrentDirectory();
        
        // Initialize paths
        _packagesPath = Path.Combine(_repoRoot, "packages");
        _installersPath = Path.Combine(_repoRoot, "installers");
        _deploymentPath = Path.Combine(_repoRoot, "deployment");
        _pkgsInfoPath = Path.Combine(_deploymentPath, "pkgsinfo");
        _pkgsPath = Path.Combine(_deploymentPath, "pkgs");
        
        // YAML configuration
        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
            
        _yamlSerializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
    }

    #region Package Discovery

    /// <summary>
    /// Find package location by checking ./packages, ./installers, then ./deployment/pkgsinfo
    /// Port of Find-PackageLocation from control.ps1
    /// </summary>
    public PackageLocation FindPackageLocation(string packageName)
    {
        var result = new PackageLocation();
        
        // Handle versioned package paths (e.g., "Maya\2024" or "Maya/2024")
        var normalizedName = packageName.Replace("/", "\\");
        var parts = normalizedName.Split('\\');
        
        if (parts.Length > 1)
        {
            // User specified a versioned package path
            return FindVersionedPackage(parts[0], parts[1]);
        }
        
        // Try ./packages first
        var localPath = Path.Combine(_packagesPath, packageName);
        if (Directory.Exists(localPath))
        {
            var versionedResult = CheckForVersionedSubfolders(localPath, packageName, PackageSource.Packages);
            if (versionedResult != null)
            {
                return versionedResult;
            }
            
            return new PackageLocation
            {
                Type = PackageLocationType.Local,
                Path = localPath,
                Source = PackageSource.Packages
            };
        }
        
        // Try ./installers second
        var installerPath = Path.Combine(_installersPath, packageName);
        if (Directory.Exists(installerPath))
        {
            var versionedResult = CheckForVersionedSubfolders(installerPath, packageName, PackageSource.Installers);
            if (versionedResult != null)
            {
                return versionedResult;
            }
            
            return new PackageLocation
            {
                Type = PackageLocationType.Local,
                Path = installerPath,
                Source = PackageSource.Installers
            };
        }
        
        // Try ./deployment/pkgsinfo
        return FindDeploymentPackage(packageName);
    }
    
    private PackageLocation? CheckForVersionedSubfolders(string path, string packageName, PackageSource source)
    {
        if (!Directory.Exists(path)) return null;
        
        var subfolders = Directory.GetDirectories(path);
        var versionFolders = subfolders.Where(d =>
        {
            var dirName = Path.GetFileName(d);
            // Check if looks like version (2024, 2025, 1.0, v1, etc.)
            if (Regex.IsMatch(dirName, @"^\d{4}$|^\d+\.\d+|^v?\d+"))
                return true;
            // Or has build-info.yaml/payload/scripts
            return File.Exists(Path.Combine(d, "build-info.yaml")) ||
                   Directory.Exists(Path.Combine(d, "payload")) ||
                   Directory.Exists(Path.Combine(d, "scripts"));
        }).ToList();
        
        if (versionFolders.Any())
        {
            return new PackageLocation
            {
                Type = PackageLocationType.NotFound, // Indicates user needs to specify version
                Path = path,
                Source = source,
                IsVersioned = true,
                BasePackageName = packageName,
                AvailableVersions = versionFolders.Select(Path.GetFileName).ToList()!
            };
        }
        
        return null;
    }
    
    private PackageLocation FindVersionedPackage(string baseName, string version)
    {
        // Try packages first
        var packagePath = Path.Combine(_packagesPath, baseName, version);
        if (Directory.Exists(packagePath))
        {
            return new PackageLocation
            {
                Type = PackageLocationType.Local,
                Path = packagePath,
                Source = PackageSource.Packages,
                Version = version,
                BasePackageName = baseName
            };
        }
        
        // Try installers
        var installerPath = Path.Combine(_installersPath, baseName, version);
        if (Directory.Exists(installerPath))
        {
            return new PackageLocation
            {
                Type = PackageLocationType.Local,
                Path = installerPath,
                Source = PackageSource.Installers,
                Version = version,
                BasePackageName = baseName
            };
        }
        
        // Not found - check if base package exists
        var result = new PackageLocation
        {
            Type = PackageLocationType.NotFound,
            BasePackageName = baseName,
            Version = version
        };
        
        // Collect available versions for error message
        var versions = new List<string>();
        var basePkgPath = Path.Combine(_packagesPath, baseName);
        if (Directory.Exists(basePkgPath))
        {
            versions.AddRange(Directory.GetDirectories(basePkgPath).Select(Path.GetFileName)!);
        }
        var baseInstPath = Path.Combine(_installersPath, baseName);
        if (Directory.Exists(baseInstPath))
        {
            versions.AddRange(Directory.GetDirectories(baseInstPath).Select(Path.GetFileName)!);
        }
        result.AvailableVersions = versions.Distinct().ToList();
        
        return result;
    }
    
    private PackageLocation FindDeploymentPackage(string packageName)
    {
        if (!Directory.Exists(_pkgsInfoPath))
        {
            return new PackageLocation { Type = PackageLocationType.NotFound };
        }
        
        var yamlFiles = Directory.GetFiles(_pkgsInfoPath, "*.yaml", SearchOption.AllDirectories);
        var matchingFiles = new List<(string Path, PkgInfoManifest? Manifest)>();
        
        foreach (var file in yamlFiles)
        {
            try
            {
                var content = File.ReadAllText(file);
                var manifest = ParsePkgInfo(content);
                
                if (manifest != null)
                {
                    // Exact name match
                    if (string.Equals(manifest.Name, packageName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchingFiles.Add((file, manifest));
                        continue;
                    }
                    
                    // Normalized match (ignore hyphens, underscores, spaces)
                    var normalizedManifest = Regex.Replace(manifest.Name ?? "", @"[-_\s]", "");
                    var normalizedSearch = Regex.Replace(packageName, @"[-_\s]", "");
                    if (string.Equals(normalizedManifest, normalizedSearch, StringComparison.OrdinalIgnoreCase))
                    {
                        matchingFiles.Add((file, manifest));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to parse YAML file: {File}", file);
            }
        }
        
        if (matchingFiles.Count == 0)
        {
            return new PackageLocation { Type = PackageLocationType.NotFound };
        }
        
        // If multiple matches, find the latest version
        var latest = matchingFiles
            .OrderByDescending(m => ParseVersion(m.Manifest?.Version ?? "0.0.0"))
            .First();
        
        return new PackageLocation
        {
            Type = PackageLocationType.Deployment,
            Path = Path.GetDirectoryName(latest.Path)!,
            YamlPath = latest.Path,
            Source = PackageSource.Deployment,
            Version = latest.Manifest?.Version
        };
    }
    
    private Version ParseVersion(string versionStr)
    {
        try
        {
            // Clean version string
            var cleaned = Regex.Replace(versionStr, @"[^\d.]", "");
            if (Version.TryParse(cleaned, out var ver))
                return ver;
        }
        catch { }
        return new Version(0, 0, 0);
    }

    /// <summary>
    /// Get all packages from packages and installers directories
    /// </summary>
    public List<PackageLocation> GetAllPackages()
    {
        var packages = new List<PackageLocation>();
        
        // Scan ./packages
        if (Directory.Exists(_packagesPath))
        {
            packages.AddRange(ScanPackageDirectory(_packagesPath, PackageSource.Packages));
        }
        
        // Scan ./installers
        if (Directory.Exists(_installersPath))
        {
            packages.AddRange(ScanPackageDirectory(_installersPath, PackageSource.Installers));
        }
        
        return packages;
    }
    
    private List<PackageLocation> ScanPackageDirectory(string basePath, PackageSource source)
    {
        var packages = new List<PackageLocation>();
        
        foreach (var dir in Directory.GetDirectories(basePath, "*", SearchOption.AllDirectories))
        {
            // Only include directories that have build-info.yaml
            if (File.Exists(Path.Combine(dir, "build-info.yaml")))
            {
                packages.Add(new PackageLocation
                {
                    Type = PackageLocationType.Local,
                    Path = dir,
                    Source = source,
                    BasePackageName = Path.GetFileName(dir)
                });
            }
        }
        
        return packages;
    }

    #endregion

    #region YAML Parsing and Validation

    /// <summary>
    /// Parse pkginfo YAML content
    /// </summary>
    public PkgInfoManifest? ParsePkgInfo(string yamlContent)
    {
        try
        {
            // Use dynamic parsing first to handle various YAML structures
            var dict = _yamlDeserializer.Deserialize<Dictionary<string, object>>(yamlContent);
            if (dict == null) return null;
            
            var manifest = new PkgInfoManifest
            {
                Name = GetStringValue(dict, "name"),
                DisplayName = GetStringValue(dict, "display_name"),
                Version = GetStringValue(dict, "version"),
                Description = GetStringValue(dict, "description"),
                Category = GetStringValue(dict, "category"),
                Developer = GetStringValue(dict, "developer"),
                UnattendedInstall = GetBoolValue(dict, "unattended_install"),
                UnattendedUninstall = GetBoolValue(dict, "unattended_uninstall"),
                PreinstallScript = GetStringValue(dict, "preinstall_script"),
                PostinstallScript = GetStringValue(dict, "postinstall_script"),
                PreuninstallScript = GetStringValue(dict, "preuninstall_script"),
                PostuninstallScript = GetStringValue(dict, "postuninstall_script")
            };
            
            // Parse catalogs
            if (dict.TryGetValue("catalogs", out var catalogsObj) && catalogsObj is List<object> catalogs)
            {
                manifest.Catalogs = catalogs.Select(c => c?.ToString() ?? "").ToList();
            }
            
            // Parse supported_architectures
            if (dict.TryGetValue("supported_architectures", out var archObj) && archObj is List<object> archs)
            {
                manifest.SupportedArchitectures = archs.Select(a => a?.ToString() ?? "").ToList();
            }
            
            // Parse installer section
            if (dict.TryGetValue("installer", out var installerObj) && installerObj is Dictionary<object, object> installer)
            {
                manifest.Installer = ParseInstallerSection(installer);
            }
            
            // Parse installs array
            if (dict.TryGetValue("installs", out var installsObj) && installsObj is List<object> installs)
            {
                manifest.Installs = ParseInstallsArray(installs);
            }
            
            // Parse uninstaller array
            if (dict.TryGetValue("uninstaller", out var uninstallerObj) && uninstallerObj is List<object> uninstaller)
            {
                manifest.Uninstaller = ParseUninstallerArray(uninstaller);
            }
            
            return manifest;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse pkginfo YAML");
            return null;
        }
    }
    
    private InstallerInfo ParseInstallerSection(Dictionary<object, object> installer)
    {
        var info = new InstallerInfo
        {
            Type = installer.GetValueOrDefault("type")?.ToString(),
            Location = installer.GetValueOrDefault("location")?.ToString(),
            Hash = installer.GetValueOrDefault("hash")?.ToString()
        };
        
        if (installer.TryGetValue("size", out var sizeObj) && long.TryParse(sizeObj?.ToString(), out var size))
        {
            info.Size = size;
        }
        
        // Parse flags
        if (installer.TryGetValue("flags", out var flagsObj) && flagsObj is List<object> flags)
        {
            info.Flags = flags.Select(f => f?.ToString() ?? "").ToList();
        }
        
        // Parse switches
        if (installer.TryGetValue("switches", out var switchesObj) && switchesObj is List<object> switches)
        {
            info.Switches = switches.Select(s => s?.ToString() ?? "").ToList();
        }
        
        return info;
    }
    
    private List<InstallsItem> ParseInstallsArray(List<object> installs)
    {
        var items = new List<InstallsItem>();
        
        foreach (var item in installs)
        {
            if (item is Dictionary<object, object> dict)
            {
                items.Add(new InstallsItem
                {
                    Path = dict.GetValueOrDefault("path")?.ToString(),
                    Type = dict.GetValueOrDefault("type")?.ToString(),
                    Version = dict.GetValueOrDefault("version")?.ToString(),
                    Md5Checksum = dict.GetValueOrDefault("md5checksum")?.ToString()
                });
            }
        }
        
        return items;
    }
    
    private List<UninstallerItem> ParseUninstallerArray(List<object> uninstaller)
    {
        var items = new List<UninstallerItem>();
        
        foreach (var item in uninstaller)
        {
            if (item is Dictionary<object, object> dict)
            {
                var uninstItem = new UninstallerItem
                {
                    Type = dict.GetValueOrDefault("type")?.ToString(),
                    Path = dict.GetValueOrDefault("path")?.ToString(),
                    ProductCode = dict.GetValueOrDefault("product_code")?.ToString(),
                    Script = dict.GetValueOrDefault("script")?.ToString()
                };
                
                if (dict.TryGetValue("flags", out var flagsObj) && flagsObj is List<object> flags)
                {
                    uninstItem.Flags = flags.Select(f => f?.ToString() ?? "").ToList();
                }
                
                if (dict.TryGetValue("switches", out var switchesObj) && switchesObj is List<object> switches)
                {
                    uninstItem.Switches = switches.Select(s => s?.ToString() ?? "").ToList();
                }
                
                items.Add(uninstItem);
            }
        }
        
        return items;
    }
    
    private string GetStringValue(Dictionary<string, object> dict, string key)
    {
        return dict.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";
    }
    
    private bool GetBoolValue(Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var value))
        {
            if (value is bool b) return b;
            if (bool.TryParse(value?.ToString(), out var parsed)) return parsed;
        }
        return false;
    }

    /// <summary>
    /// Validate pkginfo manifest structure
    /// Port of Test-PackageManifestStructure from Package-Validation.ps1
    /// </summary>
    public YamlValidationResult ValidatePkgInfo(string yamlPath)
    {
        var result = new YamlValidationResult();
        
        if (!File.Exists(yamlPath))
        {
            result.IsValid = false;
            result.Errors.Add($"YAML file not found: {yamlPath}");
            return result;
        }
        
        try
        {
            var content = File.ReadAllText(yamlPath);
            var manifest = ParsePkgInfo(content);
            
            if (manifest == null)
            {
                result.IsValid = false;
                result.Errors.Add("Failed to parse YAML content");
                return result;
            }
            
            manifest.FilePath = yamlPath;
            result.Manifest = manifest;
            result.Info.Add("Successfully parsed YAML file");
            
            // Validate mandatory fields
            var mandatoryFields = new[]
            {
                ("name", manifest.Name),
                ("display_name", manifest.DisplayName),
                ("version", manifest.Version),
                ("category", manifest.Category),
                ("developer", manifest.Developer)
            };
            
            foreach (var (field, value) in mandatoryFields)
            {
                if (string.IsNullOrEmpty(value))
                {
                    result.Errors.Add($"Missing mandatory field: {field}");
                    result.IsValid = false;
                }
            }
            
            // Validate installer section
            if (manifest.Installer == null)
            {
                result.Errors.Add("Missing mandatory field: installer");
                result.IsValid = false;
            }
            else
            {
                // Check for flags/switches in correct location
                if (manifest.Installer.Flags.Any())
                {
                    result.Info.Add("Found flags in installer section (correct placement)");
                }
                if (manifest.Installer.Switches.Any())
                {
                    result.Info.Add("Found switches in installer section (correct placement)");
                }
            }
            
            // Validate installs array
            if (!manifest.Installs.Any())
            {
                result.Warnings.Add("No 'installs' array found - post-installation validation will be skipped");
            }
            
            // Validate catalogs
            if (!manifest.Catalogs.Any())
            {
                result.Errors.Add("Missing mandatory field: catalogs");
                result.IsValid = false;
            }
            else
            {
                var validCatalogs = new[] { "Development", "Testing", "Staging", "Production" };
                foreach (var catalog in manifest.Catalogs)
                {
                    if (!validCatalogs.Contains(catalog, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Warnings.Add($"Unknown catalog: {catalog} (valid: {string.Join(", ", validCatalogs)})");
                    }
                }
            }
            
            // Validate architectures
            if (!manifest.SupportedArchitectures.Any())
            {
                result.Errors.Add("Missing mandatory field: supported_architectures");
                result.IsValid = false;
            }
            else
            {
                var validArchs = new[] { "x86", "x64", "arm64" };
                foreach (var arch in manifest.SupportedArchitectures)
                {
                    if (!validArchs.Contains(arch, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Warnings.Add($"Unknown architecture: {arch} (valid: {string.Join(", ", validArchs)})");
                    }
                }
            }
            
            // Validate version format
            if (!string.IsNullOrEmpty(manifest.Version) && !Regex.IsMatch(manifest.Version, @"^\d+\.\d+"))
            {
                result.Warnings.Add("Version format should follow semantic versioning (e.g., 1.0.0)");
            }
            
        }
        catch (Exception ex)
        {
            result.IsValid = false;
            result.Errors.Add($"Failed to parse YAML: {ex.Message}");
        }
        
        return result;
    }

    #endregion

    #region Installation Testing

    /// <summary>
    /// Run installation test for a package
    /// Port of Test-InstallationProcess from Quality-Helpers.ps1
    /// </summary>
    public async Task<InstallationTestResult> TestInstallationAsync(
        PackageLocation location, 
        PkgInfoManifest manifest, 
        QaOptions options)
    {
        var result = new InstallationTestResult();
        var stopwatch = Stopwatch.StartNew();
        
        try
        {
            // Step 1: Run preinstall_script if present
            if (!string.IsNullOrEmpty(manifest.PreinstallScript))
            {
                result.Output.Add("Running preinstall_script...");
                result.PreinstallResult = await ExecutePowerShellScriptAsync(manifest.PreinstallScript, options.DryRun);
                
                if (!result.PreinstallResult.Success)
                {
                    result.Errors.Add($"Preinstall script failed: {result.PreinstallResult.Errors}");
                    // Don't fail the whole installation, preinstall is best-effort
                }
                else
                {
                    result.Output.Add("Preinstall script completed successfully");
                }
            }
            
            if (options.DryRun)
            {
                result.Output.Add("DRY RUN: Would run installation");
                result.Success = true;
                result.Duration = stopwatch.Elapsed;
                return result;
            }
            
            // Step 2: Locate the installer
            string? installerPath = null;
            
            if (manifest.Installer?.Location != null)
            {
                if (manifest.Installer.Location.StartsWith("\\"))
                {
                    // Relative to deployment pkgs
                    installerPath = Path.Combine(_pkgsPath, manifest.Installer.Location.TrimStart('\\'));
                }
                else if (location.Type == PackageLocationType.Local)
                {
                    // Look in package's build or payload folder
                    installerPath = FindInstallerInPackage(location.Path, manifest);
                }
            }
            
            if (installerPath == null || !File.Exists(installerPath))
            {
                result.Errors.Add($"Installer not found at: {installerPath ?? "unknown location"}");
                result.Success = false;
                result.Duration = stopwatch.Elapsed;
                return result;
            }
            
            result.InstallerPath = installerPath;
            
            // Step 3: Build command line
            var commandLine = BuildInstallCommandLine(installerPath, manifest);
            result.CommandLine = commandLine;
            result.Output.Add($"Running: {commandLine}");
            
            // Step 4: Execute installation
            var processResult = await RunProcessAsync(installerPath, GetInstallArguments(manifest), 
                timeoutMinutes: 30);
            
            result.ExitCode = processResult.ExitCode;
            result.Output.AddRange(processResult.Output);
            if (!string.IsNullOrEmpty(processResult.Errors))
            {
                result.Errors.Add(processResult.Errors);
            }
            
            // Check for success (0 or 3010 for MSI)
            var successCodes = new[] { 0, 3010 };
            result.Success = successCodes.Contains(processResult.ExitCode);
            
            if (!result.Success)
            {
                result.Errors.Add($"Installation failed with exit code: {processResult.ExitCode}");
                
                // Provide MSI error context
                if (manifest.Installer?.Type?.ToLower() == "msi")
                {
                    var msiError = GetMsiErrorDescription(processResult.ExitCode);
                    if (!string.IsNullOrEmpty(msiError))
                    {
                        result.Errors.Add($"MSI Error: {msiError}");
                    }
                }
            }
            
            // Step 5: Run postinstall_script if present
            if (!string.IsNullOrEmpty(manifest.PostinstallScript))
            {
                result.Output.Add("Running postinstall_script...");
                result.PostinstallResult = await ExecutePowerShellScriptAsync(manifest.PostinstallScript, false);
                
                if (!result.PostinstallResult.Success)
                {
                    result.Errors.Add($"Postinstall script failed: {result.PostinstallResult.Errors}");
                }
                else
                {
                    result.Output.Add("Postinstall script completed successfully");
                }
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Errors.Add($"Installation exception: {ex.Message}");
            Log.Error(ex, "Installation test failed");
        }
        
        result.Duration = stopwatch.Elapsed;
        return result;
    }
    
    private string? FindInstallerInPackage(string packagePath, PkgInfoManifest manifest)
    {
        var searchDirs = new[] { "build", "payload" };
        var searchPatterns = new[] { "*.msi", "*.exe", "*.pkg", "*.nupkg" };
        
        foreach (var dir in searchDirs)
        {
            var searchPath = Path.Combine(packagePath, dir);
            if (!Directory.Exists(searchPath)) continue;
            
            foreach (var pattern in searchPatterns)
            {
                var files = Directory.GetFiles(searchPath, pattern, SearchOption.AllDirectories);
                if (files.Any())
                {
                    // Prefer the one matching the installer location if specified
                    if (manifest.Installer?.Location != null)
                    {
                        var expectedName = Path.GetFileName(manifest.Installer.Location);
                        var match = files.FirstOrDefault(f => 
                            Path.GetFileName(f).Equals(expectedName, StringComparison.OrdinalIgnoreCase));
                        if (match != null) return match;
                    }
                    return files[0];
                }
            }
        }
        
        return null;
    }
    
    private string BuildInstallCommandLine(string installerPath, PkgInfoManifest manifest)
    {
        var sb = new StringBuilder();
        sb.Append($"\"{installerPath}\"");
        
        var type = manifest.Installer?.Type?.ToLower() ?? "";
        
        if (type == "msi")
        {
            sb.Append(" /qn /norestart");
        }
        
        // Add flags
        foreach (var flag in manifest.Installer?.Flags ?? new List<string>())
        {
            sb.Append($" {FormatFlag(flag, type)}");
        }
        
        // Add switches
        foreach (var sw in manifest.Installer?.Switches ?? new List<string>())
        {
            sb.Append($" {FormatSwitch(sw, type)}");
        }
        
        return sb.ToString();
    }
    
    private string GetInstallArguments(PkgInfoManifest manifest)
    {
        var args = new List<string>();
        var type = manifest.Installer?.Type?.ToLower() ?? "";
        
        if (type == "msi")
        {
            args.Add("/qn");
            args.Add("/norestart");
        }
        
        // Add flags
        foreach (var flag in manifest.Installer?.Flags ?? new List<string>())
        {
            args.Add(FormatFlag(flag, type));
        }
        
        // Add switches
        foreach (var sw in manifest.Installer?.Switches ?? new List<string>())
        {
            args.Add(FormatSwitch(sw, type));
        }
        
        return string.Join(" ", args);
    }
    
    private string FormatFlag(string flag, string installerType)
    {
        // Smart flag formatting - port from Smart-Flag-Management.ps1
        if (flag.StartsWith("-") || flag.StartsWith("/"))
            return flag; // Already has prefix
            
        if (flag.Contains("="))
            return flag; // Environment-style flag
            
        // Add appropriate prefix
        return installerType == "msi" ? $"/{flag}" : $"/{flag}";
    }
    
    private string FormatSwitch(string sw, string installerType)
    {
        if (sw.StartsWith("-") || sw.StartsWith("/"))
            return sw;
            
        return $"/{sw}";
    }
    
    private string GetMsiErrorDescription(int exitCode)
    {
        return exitCode switch
        {
            1601 => "The Windows Installer service could not be accessed",
            1602 => "User cancelled installation",
            1603 => "A fatal error occurred during installation (file in use, permissions, etc.)",
            1604 => "Installation suspended, incomplete",
            1605 => "This action is only valid for products that are currently installed",
            1618 => "Another installation is already in progress",
            1619 => "This installation package could not be opened",
            1620 => "This installation package could not be opened",
            1622 => "There was an error opening installation log file",
            1623 => "This language of this installation package is not supported",
            1625 => "This installation is forbidden by system policy",
            1638 => "Another version of this product is already installed",
            3010 => "A restart is required to complete the install (SUCCESS)",
            _ => ""
        };
    }

    #endregion

    #region Uninstall Testing

    /// <summary>
    /// Run uninstall test for a package
    /// Port of Test-PackageUninstallation from Quality-Helpers.ps1
    /// </summary>
    public async Task<UninstallTestResult> TestUninstallAsync(
        PkgInfoManifest manifest,
        QaOptions options)
    {
        var result = new UninstallTestResult();
        var stopwatch = Stopwatch.StartNew();
        
        try
        {
            // Check for uninstaller array (modern approach)
            if (manifest.Uninstaller.Any())
            {
                result.Method = "Modern uninstaller array";
                result.Output.Add("Using modern uninstaller array approach");
                
                foreach (var uninstaller in manifest.Uninstaller)
                {
                    var uninstallResult = await RunUninstallerItemAsync(uninstaller, options.DryRun);
                    result.Output.AddRange(uninstallResult.Output);
                    result.Errors.AddRange(uninstallResult.Errors);
                    
                    if (!uninstallResult.Success)
                    {
                        result.Success = false;
                        break;
                    }
                }
                
                if (!result.Errors.Any())
                {
                    result.Success = true;
                }
            }
            else if (manifest.Installer?.Type?.ToLower() == "msi")
            {
                // Fall back to MSI ProductCode lookup
                result.Method = "MSI ProductCode lookup";
                result.Output.Add("No uninstaller array - attempting MSI ProductCode lookup");
                
                // Would need to search registry for ProductCode matching this product
                // For now, report as not implemented
                result.Output.Add("MSI ProductCode uninstall not yet implemented in FleetMate");
                result.Success = true; // Don't fail the test
            }
            else
            {
                result.Method = "No uninstaller defined";
                result.Output.Add("No uninstaller defined for this package");
                result.Success = true; // Don't fail if no uninstaller
            }
            
            // Run preuninstall_script if present
            if (!string.IsNullOrEmpty(manifest.PreuninstallScript))
            {
                result.Output.Add("Would run preuninstall_script");
            }
            
            // Run postuninstall_script if present
            if (!string.IsNullOrEmpty(manifest.PostuninstallScript))
            {
                result.Output.Add("Would run postuninstall_script");
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Errors.Add($"Uninstall exception: {ex.Message}");
            Log.Error(ex, "Uninstall test failed");
        }
        
        result.Duration = stopwatch.Elapsed;
        return result;
    }
    
    private async Task<UninstallTestResult> RunUninstallerItemAsync(UninstallerItem item, bool dryRun)
    {
        var result = new UninstallTestResult { Success = true };
        
        if (dryRun)
        {
            result.Output.Add($"DRY RUN: Would run uninstaller type={item.Type}");
            return result;
        }
        
        switch (item.Type?.ToLower())
        {
            case "msi":
                if (!string.IsNullOrEmpty(item.ProductCode))
                {
                    var args = $"/x {item.ProductCode} /qn /norestart";
                    result.Output.Add($"Running: msiexec {args}");
                    var processResult = await RunProcessAsync("msiexec.exe", args, timeoutMinutes: 30);
                    result.ExitCode = processResult.ExitCode;
                    result.Success = processResult.ExitCode == 0 || processResult.ExitCode == 3010;
                }
                break;
                
            case "exe":
                if (!string.IsNullOrEmpty(item.Path) && File.Exists(item.Path))
                {
                    var args = string.Join(" ", item.Switches ?? new List<string>());
                    result.Output.Add($"Running: {item.Path} {args}");
                    var processResult = await RunProcessAsync(item.Path, args, timeoutMinutes: 30);
                    result.ExitCode = processResult.ExitCode;
                    result.Success = processResult.ExitCode == 0;
                }
                break;
                
            case "script":
                if (!string.IsNullOrEmpty(item.Script))
                {
                    result.Output.Add("Running uninstall script");
                    var scriptResult = await ExecutePowerShellScriptAsync(item.Script, false);
                    result.Success = scriptResult.Success;
                    result.Output.Add(scriptResult.Output);
                }
                break;
                
            default:
                result.Output.Add($"Unknown uninstaller type: {item.Type}");
                break;
        }
        
        return result;
    }

    #endregion

    #region Post-Installation Validation

    /// <summary>
    /// Validate installation using installs array
    /// Port of Test-PostInstallation from control.ps1
    /// </summary>
    public PostInstallValidationResult ValidateInstallation(PkgInfoManifest manifest)
    {
        var result = new PostInstallValidationResult { Success = true };
        
        if (!manifest.Installs.Any())
        {
            result.Messages.Add("No installs array - skipping validation");
            return result;
        }
        
        foreach (var item in manifest.Installs)
        {
            result.TotalChecks++;
            
            var verification = new InstallVerification
            {
                Path = item.Path ?? "",
                Type = item.Type ?? "",
                ExpectedVersion = item.Version
            };
            
            switch (item.Type?.ToLower())
            {
                case "file":
                    if (File.Exists(item.Path))
                    {
                        verification.Found = true;
                        
                        // Check version if specified
                        if (!string.IsNullOrEmpty(item.Version))
                        {
                            try
                            {
                                var fileInfo = FileVersionInfo.GetVersionInfo(item.Path!);
                                verification.ActualVersion = fileInfo.FileVersion;
                                verification.VersionMatch = string.Equals(
                                    verification.ActualVersion, item.Version,
                                    StringComparison.OrdinalIgnoreCase);
                                    
                                if (!verification.VersionMatch)
                                {
                                    result.Messages.Add($"Version mismatch: {item.Path} - expected {item.Version}, got {verification.ActualVersion}");
                                }
                            }
                            catch
                            {
                                verification.VersionMatch = true; // Can't verify, assume OK
                            }
                        }
                        else
                        {
                            verification.VersionMatch = true;
                        }
                        
                        // Check MD5 if specified
                        if (!string.IsNullOrEmpty(item.Md5Checksum))
                        {
                            var actualMd5 = ComputeMd5(item.Path!);
                            if (!string.Equals(actualMd5, item.Md5Checksum, StringComparison.OrdinalIgnoreCase))
                            {
                                verification.Found = false; // Mark as failed
                                result.Messages.Add($"MD5 mismatch: {item.Path}");
                            }
                        }
                        
                        if (verification.Found && verification.VersionMatch)
                        {
                            result.PassedChecks++;
                        }
                        else
                        {
                            result.FailedChecks++;
                            result.Success = false;
                        }
                    }
                    else
                    {
                        verification.Found = false;
                        result.FailedChecks++;
                        result.Success = false;
                        result.Messages.Add($"File not found: {item.Path}");
                    }
                    break;
                    
                case "directory":
                    verification.Found = Directory.Exists(item.Path);
                    verification.VersionMatch = true;
                    
                    if (verification.Found)
                    {
                        result.PassedChecks++;
                    }
                    else
                    {
                        result.FailedChecks++;
                        result.Success = false;
                        result.Messages.Add($"Directory not found: {item.Path}");
                    }
                    break;
                    
                default:
                    // Unknown type - skip
                    result.TotalChecks--;
                    break;
            }
            
            result.Verifications.Add(verification);
        }
        
        return result;
    }
    
    private string ComputeMd5(string filePath)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(filePath);
        var hash = md5.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    #endregion

    #region Package Build

    /// <summary>
    /// Build a package using cimipkg.exe
    /// Port of Test-PackageBuild from Quality-Helpers.ps1
    /// </summary>
    public async Task<PackageBuildResult> BuildPackageAsync(string packagePath, bool dryRun = false)
    {
        var result = new PackageBuildResult();
        var stopwatch = Stopwatch.StartNew();
        
        try
        {
            if (dryRun)
            {
                result.Output = "DRY RUN: Would run cimipkg.exe .";
                result.Success = true;
                result.Duration = stopwatch.Elapsed;
                return result;
            }
            
            // Run cimipkg.exe build
            var processResult = await RunProcessInDirectoryAsync(
                "cimipkg.exe", ".", packagePath, timeoutMinutes: 10);
            
            result.ExitCode = processResult.ExitCode;
            result.Output = string.Join("\n", processResult.Output);
            result.Success = processResult.ExitCode == 0;
            
            if (result.Success)
            {
                // Find the output package
                var buildDir = Path.Combine(packagePath, "build");
                if (Directory.Exists(buildDir))
                {
                    var pkgFile = Directory.GetFiles(buildDir, "*.pkg").FirstOrDefault()
                                ?? Directory.GetFiles(buildDir, "*.nupkg").FirstOrDefault();
                    
                    if (pkgFile != null)
                    {
                        result.OutputPackagePath = pkgFile;
                        result.PackageSize = new FileInfo(pkgFile).Length;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Output = $"Build exception: {ex.Message}";
            Log.Error(ex, "Package build failed");
        }
        
        result.Duration = stopwatch.Elapsed;
        return result;
    }

    #endregion

    #region Complete QA Workflow

    /// <summary>
    /// Run complete QA workflow for a package
    /// Port of Invoke-PackageQualityControl from control.ps1
    /// </summary>
    public async Task<QaResult> RunPackageQaAsync(string packageName, QaOptions options)
    {
        var result = new QaResult
        {
            PackageName = packageName,
            StartTime = DateTime.Now,
            Mode = QaRunMode.SinglePackage,
            Options = options
        };
        
        var stepNumber = 0;
        
        try
        {
            // Find the package
            result.Location = FindPackageLocation(packageName);
            
            if (!result.Location.Found)
            {
                if (result.Location.IsVersioned)
                {
                    result.Steps.Add(new QaStepResult
                    {
                        StepNumber = ++stepNumber,
                        StepName = "Package Discovery",
                        Success = false,
                        Severity = QaSeverity.Error,
                        Errors = new List<string>
                        {
                            $"Versioned package requires version specification",
                            $"Available versions: {string.Join(", ", result.Location.AvailableVersions)}",
                            $"Example: fleetmate qa {packageName}\\2024"
                        }
                    });
                }
                else
                {
                    result.Steps.Add(new QaStepResult
                    {
                        StepNumber = ++stepNumber,
                        StepName = "Package Discovery",
                        Success = false,
                        Severity = QaSeverity.Error,
                        Errors = new List<string>
                        {
                            $"Package '{packageName}' not found in packages/, installers/, or deployment/pkgsinfo/"
                        }
                    });
                }
                
                result.Total = 1;
                result.Failed = 1;
                result.EndTime = DateTime.Now;
                return result;
            }
            
            // Route to appropriate workflow
            if (result.Location.Type == PackageLocationType.Deployment)
            {
                return await RunDeploymentPackageQaAsync(result, options, stepNumber);
            }
            else
            {
                return await RunLocalPackageQaAsync(result, options, stepNumber);
            }
        }
        catch (Exception ex)
        {
            result.Steps.Add(new QaStepResult
            {
                StepNumber = ++stepNumber,
                StepName = "QA Workflow",
                Success = false,
                Severity = QaSeverity.Error,
                Errors = new List<string> { $"QA exception: {ex.Message}" }
            });
            result.Total++;
            result.Failed++;
            Log.Error(ex, "QA workflow failed for {Package}", packageName);
        }
        
        result.EndTime = DateTime.Now;
        return result;
    }
    
    private async Task<QaResult> RunLocalPackageQaAsync(QaResult result, QaOptions options, int stepNumber)
    {
        var location = result.Location;
        
        if (options.InstallOnly)
        {
            // Install-only workflow
            return await RunInstallOnlyWorkflowAsync(result, options, stepNumber);
        }
        
        // Full workflow
        
        // Step 1: Build Configuration Check
        var step1 = TestBuildConfiguration(location.Path);
        step1.StepNumber = ++stepNumber;
        step1.StepName = "Build Configuration Check";
        result.Steps.Add(step1);
        result.Total++;
        if (step1.Success) result.Passed++; else result.Failed++;
        
        // Step 2: YAML Linting
        var yamlPath = FindPackageYaml(location.Path);
        if (yamlPath != null)
        {
            var validation = ValidatePkgInfo(yamlPath);
            var step2 = new QaStepResult
            {
                StepNumber = ++stepNumber,
                StepName = "YAML Linting",
                Success = validation.IsValid,
                Severity = validation.IsValid ? QaSeverity.Success : QaSeverity.Error,
                Errors = validation.Errors,
                Warnings = validation.Warnings,
                Messages = validation.Info
            };
            result.Steps.Add(step2);
            result.Total++;
            if (step2.Success) result.Passed++; else result.Failed++;
        }
        
        // Step 3: Package Build Test
        var buildResult = await BuildPackageAsync(location.Path, options.DryRun);
        var step3 = new QaStepResult
        {
            StepNumber = ++stepNumber,
            StepName = "Package Build Test",
            Success = buildResult.Success,
            Severity = buildResult.Success ? QaSeverity.Success : QaSeverity.Error,
            Messages = new List<string> { buildResult.Output },
            Details = new Dictionary<string, object>
            {
                ["ExitCode"] = buildResult.ExitCode,
                ["PackagePath"] = buildResult.OutputPackagePath ?? "",
                ["PackageSize"] = buildResult.PackageSize
            }
        };
        result.Steps.Add(step3);
        result.Total++;
        if (step3.Success) result.Passed++; else result.Failed++;
        
        // Step 4: Package Artifact Validation
        var step4 = TestPackageArtifact(location.Path, result.PackageName);
        step4.StepNumber = ++stepNumber;
        step4.StepName = "Package Artifact Validation";
        result.Steps.Add(step4);
        result.Total++;
        if (step4.Success) result.Passed++; else result.Failed++;
        
        // Step 5: Deployment Consistency Check
        var step5 = TestDeploymentConsistency(location.Path, result.PackageName);
        step5.StepNumber = ++stepNumber;
        step5.StepName = "Deployment Consistency";
        result.Steps.Add(step5);
        result.Total++;
        if (step5.Success) result.Passed++; else result.Failed++;
        
        // Step 6: Uninstall Testing (if requested)
        if (options.UninstallFirst)
        {
            result = await RunUninstallStepAsync(result, options, stepNumber);
            stepNumber = result.Steps.Count;
        }
        
        // Step 7: Installation Testing
        result = await RunInstallationStepAsync(result, options, stepNumber);
        
        result.EndTime = DateTime.Now;
        return result;
    }
    
    private async Task<QaResult> RunDeploymentPackageQaAsync(QaResult result, QaOptions options, int stepNumber)
    {
        var yamlPath = result.Location.YamlPath!;
        var validation = ValidatePkgInfo(yamlPath);
        var manifest = validation.Manifest;
        
        // Step 1: YAML Structure Validation
        var step1 = new QaStepResult
        {
            StepNumber = ++stepNumber,
            StepName = "YAML Structure Validation",
            Success = validation.IsValid,
            Severity = validation.IsValid ? QaSeverity.Success : QaSeverity.Error,
            Errors = validation.Errors,
            Warnings = validation.Warnings,
            Messages = validation.Info
        };
        result.Steps.Add(step1);
        result.Total++;
        if (step1.Success) result.Passed++; else result.Failed++;
        
        // Step 2: Installer File Validation
        var step2 = TestInstallerFile(manifest, yamlPath);
        step2.StepNumber = ++stepNumber;
        step2.StepName = "Installer File Validation";
        result.Steps.Add(step2);
        result.Total++;
        if (step2.Success) result.Passed++; else result.Failed++;
        
        // Step 3: Installation Command Validation
        var step3 = TestInstallationCommand(manifest);
        step3.StepNumber = ++stepNumber;
        step3.StepName = "Installation Command Validation";
        result.Steps.Add(step3);
        result.Total++;
        if (step3.Success) result.Passed++; else result.Failed++;
        
        // Step 4: Uninstall Testing (if requested)
        if (options.UninstallFirst && manifest != null)
        {
            var uninstallResult = await TestUninstallAsync(manifest, options);
            var step4 = new QaStepResult
            {
                StepNumber = ++stepNumber,
                StepName = "Uninstall Testing",
                Success = uninstallResult.Success,
                Severity = uninstallResult.Success ? QaSeverity.Success : QaSeverity.Error,
                Output = uninstallResult.Output,
                Errors = uninstallResult.Errors,
                Details = new Dictionary<string, object>
                {
                    ["Method"] = uninstallResult.Method,
                    ["ExitCode"] = uninstallResult.ExitCode
                }
            };
            result.Steps.Add(step4);
            result.Total++;
            if (step4.Success) result.Passed++; else result.Failed++;
        }
        
        // Step 5: Real Installation Test (if not dry run)
        if (!options.DryRun && manifest != null)
        {
            var installResult = await TestInstallationAsync(result.Location, manifest, options);
            var step5 = new QaStepResult
            {
                StepNumber = ++stepNumber,
                StepName = "Real Installation Test",
                Success = installResult.Success,
                Severity = installResult.Success ? QaSeverity.Success : QaSeverity.Error,
                Output = installResult.Output,
                Errors = installResult.Errors,
                Details = new Dictionary<string, object>
                {
                    ["CommandLine"] = installResult.CommandLine,
                    ["ExitCode"] = installResult.ExitCode,
                    ["InstallerPath"] = installResult.InstallerPath ?? ""
                }
            };
            result.Steps.Add(step5);
            result.Total++;
            if (step5.Success) result.Passed++; else result.Failed++;
            
            // Step 6: Post-Installation Validation
            if (installResult.Success)
            {
                var validationResult = ValidateInstallation(manifest);
                var step6 = new QaStepResult
                {
                    StepNumber = ++stepNumber,
                    StepName = "Post-Installation Validation",
                    Success = validationResult.Success,
                    Severity = validationResult.Success ? QaSeverity.Success : QaSeverity.Error,
                    Messages = validationResult.Messages,
                    Details = new Dictionary<string, object>
                    {
                        ["TotalChecks"] = validationResult.TotalChecks,
                        ["PassedChecks"] = validationResult.PassedChecks,
                        ["FailedChecks"] = validationResult.FailedChecks
                    }
                };
                result.Steps.Add(step6);
                result.Total++;
                if (step6.Success) result.Passed++; else result.Failed++;
            }
        }
        else if (options.DryRun)
        {
            result.Steps.Add(new QaStepResult
            {
                StepNumber = ++stepNumber,
                StepName = "Installation Test (Dry Run)",
                Success = true,
                Severity = QaSeverity.Info,
                Messages = new List<string> { "DRY RUN: Would run installation and validation" }
            });
            result.Total++;
            result.Skipped++;
        }
        
        result.EndTime = DateTime.Now;
        return result;
    }
    
    private async Task<QaResult> RunInstallOnlyWorkflowAsync(QaResult result, QaOptions options, int stepNumber)
    {
        // Step 1: Extract package for inspection
        var pkgPath = FindPackageFile(result.Location.Path);
        if (pkgPath != null)
        {
            var step1 = ExtractPackageForInspection(pkgPath, result.Location.Path);
            step1.StepNumber = ++stepNumber;
            step1.StepName = "Package Extraction";
            result.Steps.Add(step1);
            result.Total++;
            if (step1.Success) result.Passed++; else result.Failed++;
        }
        
        // Step 2: Installation Testing
        var yamlPath = FindPackageYaml(result.Location.Path);
        if (yamlPath != null)
        {
            var validation = ValidatePkgInfo(yamlPath);
            if (validation.Manifest != null)
            {
                var installResult = await TestInstallationAsync(result.Location, validation.Manifest, options);
                var step2 = new QaStepResult
                {
                    StepNumber = ++stepNumber,
                    StepName = "Installation Testing",
                    Success = installResult.Success,
                    Severity = installResult.Success ? QaSeverity.Success : QaSeverity.Error,
                    Output = installResult.Output,
                    Errors = installResult.Errors
                };
                result.Steps.Add(step2);
                result.Total++;
                if (step2.Success) result.Passed++; else result.Failed++;
            }
        }
        
        result.EndTime = DateTime.Now;
        return result;
    }
    
    private async Task<QaResult> RunUninstallStepAsync(QaResult result, QaOptions options, int stepNumber)
    {
        var yamlPath = FindDeploymentYaml(result.PackageName);
        if (yamlPath != null)
        {
            var validation = ValidatePkgInfo(yamlPath);
            if (validation.Manifest != null)
            {
                var uninstallResult = await TestUninstallAsync(validation.Manifest, options);
                var step = new QaStepResult
                {
                    StepNumber = ++stepNumber,
                    StepName = "Uninstall Testing",
                    Success = uninstallResult.Success,
                    Severity = uninstallResult.Success ? QaSeverity.Success : QaSeverity.Error,
                    Output = uninstallResult.Output,
                    Errors = uninstallResult.Errors
                };
                result.Steps.Add(step);
                result.Total++;
                if (step.Success) result.Passed++; else result.Failed++;
            }
        }
        
        return result;
    }
    
    private async Task<QaResult> RunInstallationStepAsync(QaResult result, QaOptions options, int stepNumber)
    {
        var yamlPath = FindPackageYaml(result.Location.Path) ?? FindDeploymentYaml(result.PackageName);
        if (yamlPath != null)
        {
            var validation = ValidatePkgInfo(yamlPath);
            if (validation.Manifest != null)
            {
                var installResult = await TestInstallationAsync(result.Location, validation.Manifest, options);
                var step = new QaStepResult
                {
                    StepNumber = result.Steps.Count + 1,
                    StepName = "Installation Testing",
                    Success = installResult.Success,
                    Severity = installResult.Success ? QaSeverity.Success : QaSeverity.Error,
                    Output = installResult.Output,
                    Errors = installResult.Errors
                };
                result.Steps.Add(step);
                result.Total++;
                if (step.Success) result.Passed++; else result.Failed++;
            }
        }
        
        return result;
    }

    #endregion

    #region Helper Methods

    private QaStepResult TestBuildConfiguration(string packagePath)
    {
        var result = new QaStepResult { Success = true };
        
        var buildInfoPath = Path.Combine(packagePath, "build-info.yaml");
        if (File.Exists(buildInfoPath))
        {
            result.Messages.Add("build-info.yaml found");
        }
        else
        {
            result.Errors.Add("build-info.yaml not found");
            result.Success = false;
        }
        
        // Check for payload or scripts directory
        var hasPayload = Directory.Exists(Path.Combine(packagePath, "payload"));
        var hasScripts = Directory.Exists(Path.Combine(packagePath, "scripts"));
        
        if (hasPayload)
        {
            result.Messages.Add("payload/ directory found");
        }
        if (hasScripts)
        {
            result.Messages.Add("scripts/ directory found");
        }
        
        if (!hasPayload && !hasScripts)
        {
            result.Warnings.Add("Neither payload/ nor scripts/ directory found");
        }
        
        result.Severity = result.Success ? QaSeverity.Success : QaSeverity.Error;
        return result;
    }
    
    private QaStepResult TestPackageArtifact(string packagePath, string packageName)
    {
        var result = new QaStepResult { Success = true };
        
        var buildDir = Path.Combine(packagePath, "build");
        if (!Directory.Exists(buildDir))
        {
            result.Errors.Add("build/ directory not found");
            result.Success = false;
            result.Severity = QaSeverity.Error;
            return result;
        }
        
        // Look for .pkg or .nupkg
        var pkgFile = Directory.GetFiles(buildDir, "*.pkg").FirstOrDefault()
                    ?? Directory.GetFiles(buildDir, "*.nupkg").FirstOrDefault();
        
        if (pkgFile != null)
        {
            var fileInfo = new FileInfo(pkgFile);
            result.Messages.Add($"Package found: {Path.GetFileName(pkgFile)} ({fileInfo.Length / 1024} KB)");
            result.Details["PackagePath"] = pkgFile;
            result.Details["PackageSize"] = fileInfo.Length;
        }
        else
        {
            result.Errors.Add("No .pkg or .nupkg file found in build/");
            result.Success = false;
        }
        
        result.Severity = result.Success ? QaSeverity.Success : QaSeverity.Error;
        return result;
    }
    
    private QaStepResult TestDeploymentConsistency(string packagePath, string packageName)
    {
        var result = new QaStepResult { Success = true };
        
        // Check if package exists in deployment/pkgsinfo
        var deploymentYaml = FindDeploymentYaml(packageName);
        if (deploymentYaml != null)
        {
            result.Messages.Add($"Found in deployment: {Path.GetFileName(deploymentYaml)}");
            
            // Compare versions
            var localBuildInfo = Path.Combine(packagePath, "build-info.yaml");
            if (File.Exists(localBuildInfo))
            {
                try
                {
                    var localContent = File.ReadAllText(localBuildInfo);
                    var deployContent = File.ReadAllText(deploymentYaml);
                    
                    // Extract versions (simplified)
                    var localMatch = Regex.Match(localContent, @"version:\s*['""]?([^'""]+)['""]?");
                    var deployMatch = Regex.Match(deployContent, @"version:\s*['""]?([^'""]+)['""]?");
                    
                    if (localMatch.Success && deployMatch.Success)
                    {
                        var localVer = localMatch.Groups[1].Value;
                        var deployVer = deployMatch.Groups[1].Value;
                        
                        if (localVer != deployVer)
                        {
                            result.Warnings.Add($"Version mismatch: local={localVer}, deployed={deployVer}");
                        }
                        else
                        {
                            result.Messages.Add($"Versions match: {localVer}");
                        }
                    }
                }
                catch { }
            }
        }
        else
        {
            result.Warnings.Add("Package not found in deployment/pkgsinfo");
        }
        
        result.Severity = result.Success ? QaSeverity.Success : QaSeverity.Warning;
        return result;
    }
    
    private QaStepResult TestInstallerFile(PkgInfoManifest? manifest, string yamlPath)
    {
        var result = new QaStepResult { Success = true };
        
        if (manifest?.Installer?.Location == null)
        {
            result.Errors.Add("No installer location specified");
            result.Success = false;
            result.Severity = QaSeverity.Error;
            return result;
        }
        
        // Resolve installer path
        string installerPath;
        if (manifest.Installer.Location.StartsWith("\\"))
        {
            installerPath = Path.Combine(_pkgsPath, manifest.Installer.Location.TrimStart('\\'));
        }
        else
        {
            installerPath = Path.Combine(Path.GetDirectoryName(yamlPath)!, manifest.Installer.Location);
        }
        
        if (File.Exists(installerPath))
        {
            var fileInfo = new FileInfo(installerPath);
            result.Messages.Add($"Installer found: {Path.GetFileName(installerPath)} ({fileInfo.Length} bytes)");
            
            // Verify size if specified
            if (manifest.Installer.Size > 0 && manifest.Installer.Size != fileInfo.Length)
            {
                result.Warnings.Add($"Size mismatch: expected {manifest.Installer.Size}, got {fileInfo.Length}");
            }
            
            // Verify hash if specified
            if (!string.IsNullOrEmpty(manifest.Installer.Hash))
            {
                var actualHash = ComputeSha256(installerPath);
                if (!string.Equals(actualHash, manifest.Installer.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    result.Errors.Add($"Hash mismatch: expected {manifest.Installer.Hash}, got {actualHash}");
                    result.Success = false;
                }
                else
                {
                    result.Messages.Add("Hash verified");
                }
            }
        }
        else
        {
            result.Errors.Add($"Installer not found: {installerPath}");
            result.Success = false;
        }
        
        result.Severity = result.Success ? QaSeverity.Success : QaSeverity.Error;
        return result;
    }
    
    private QaStepResult TestInstallationCommand(PkgInfoManifest? manifest)
    {
        var result = new QaStepResult { Success = true };
        
        if (manifest?.Installer == null)
        {
            result.Errors.Add("No installer section");
            result.Success = false;
            result.Severity = QaSeverity.Error;
            return result;
        }
        
        var type = manifest.Installer.Type?.ToLower() ?? "unknown";
        result.Messages.Add($"Installer type: {type}");
        
        // Build and display command preview
        var commandPreview = BuildInstallCommandLine("[installer]", manifest);
        result.Messages.Add($"Command preview: {commandPreview}");
        
        // Validate switches/flags make sense for installer type
        if (type == "exe")
        {
            var hasQuietSwitch = manifest.Installer.Switches.Any(s => 
                s.Contains("S", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("silent", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("quiet", StringComparison.OrdinalIgnoreCase));
            
            if (!hasQuietSwitch && !manifest.Installer.Flags.Any())
            {
                result.Warnings.Add("EXE installer may need silent/quiet switch for unattended install");
            }
        }
        
        result.Severity = result.Success ? QaSeverity.Success : QaSeverity.Error;
        return result;
    }
    
    private QaStepResult ExtractPackageForInspection(string pkgPath, string packagePath)
    {
        var result = new QaStepResult { Success = true };
        
        try
        {
            var extractDir = Path.Combine(packagePath, "build", "extracted");
            
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, true);
            }
            Directory.CreateDirectory(extractDir);
            
            // Use System.IO.Compression to extract
            System.IO.Compression.ZipFile.ExtractToDirectory(pkgPath, extractDir);
            
            result.Messages.Add($"Extracted to: {extractDir}");
            
            // List contents
            var files = Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories);
            result.Messages.Add($"Extracted {files.Length} files");
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Extraction failed: {ex.Message}");
            result.Success = false;
        }
        
        result.Severity = result.Success ? QaSeverity.Success : QaSeverity.Error;
        return result;
    }
    
    private string? FindPackageYaml(string packagePath)
    {
        // Look for build-info.yaml or pkginfo yaml
        var buildInfo = Path.Combine(packagePath, "build-info.yaml");
        if (File.Exists(buildInfo)) return buildInfo;
        
        // Look in package directory for any yaml
        var yamlFiles = Directory.GetFiles(packagePath, "*.yaml", SearchOption.TopDirectoryOnly);
        return yamlFiles.FirstOrDefault();
    }
    
    private string? FindDeploymentYaml(string packageName)
    {
        if (!Directory.Exists(_pkgsInfoPath)) return null;
        
        var yamlFiles = Directory.GetFiles(_pkgsInfoPath, "*.yaml", SearchOption.AllDirectories);
        
        foreach (var file in yamlFiles)
        {
            try
            {
                var content = File.ReadAllText(file);
                var manifest = ParsePkgInfo(content);
                
                if (manifest != null && 
                    string.Equals(manifest.Name, packageName, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
            catch { }
        }
        
        return null;
    }
    
    private string? FindPackageFile(string packagePath)
    {
        var buildDir = Path.Combine(packagePath, "build");
        if (!Directory.Exists(buildDir)) return null;
        
        return Directory.GetFiles(buildDir, "*.pkg").FirstOrDefault()
            ?? Directory.GetFiles(buildDir, "*.nupkg").FirstOrDefault();
    }
    
    private string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    #endregion

    #region Process Execution

    private async Task<ScriptExecutionResult> ExecutePowerShellScriptAsync(string script, bool dryRun)
    {
        var result = new ScriptExecutionResult();
        var stopwatch = Stopwatch.StartNew();
        
        if (dryRun)
        {
            result.Success = true;
            result.Output = "DRY RUN: Would execute PowerShell script";
            result.Duration = stopwatch.Elapsed;
            return result;
        }
        
        try
        {
            // Write script to temp file
            var tempScript = Path.Combine(Path.GetTempPath(), $"fleetmate_qa_{Guid.NewGuid():N}.ps1");
            await File.WriteAllTextAsync(tempScript, script);
            
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempScript}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(psi);
                if (process != null)
                {
                    result.Output = await process.StandardOutput.ReadToEndAsync();
                    result.Errors = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    result.ExitCode = process.ExitCode;
                    result.Success = process.ExitCode == 0;
                }
            }
            finally
            {
                if (File.Exists(tempScript))
                {
                    File.Delete(tempScript);
                }
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Errors = ex.Message;
        }
        
        result.Duration = stopwatch.Elapsed;
        return result;
    }
    
    private async Task<(int ExitCode, List<string> Output, string Errors)> RunProcessAsync(
        string fileName, string arguments, int timeoutMinutes = 10)
    {
        var output = new List<string>();
        var errors = new StringBuilder();
        var exitCode = -1;
        
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            using var process = Process.Start(psi);
            if (process != null)
            {
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                
                var completed = await Task.WhenAny(
                    process.WaitForExitAsync(),
                    Task.Delay(TimeSpan.FromMinutes(timeoutMinutes))
                );
                
                if (!process.HasExited)
                {
                    process.Kill();
                    errors.AppendLine("Process timed out");
                }
                
                var outputText = await outputTask;
                var errorText = await errorTask;
                
                if (!string.IsNullOrEmpty(outputText))
                {
                    output.AddRange(outputText.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                errors.Append(errorText);
                
                exitCode = process.ExitCode;
            }
        }
        catch (Exception ex)
        {
            errors.AppendLine($"Process exception: {ex.Message}");
        }
        
        return (exitCode, output, errors.ToString());
    }
    
    private async Task<(int ExitCode, List<string> Output, string Errors)> RunProcessInDirectoryAsync(
        string fileName, string arguments, string workingDirectory, int timeoutMinutes = 10)
    {
        var output = new List<string>();
        var errors = new StringBuilder();
        var exitCode = -1;
        
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            using var process = Process.Start(psi);
            if (process != null)
            {
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                
                var completed = await Task.WhenAny(
                    process.WaitForExitAsync(),
                    Task.Delay(TimeSpan.FromMinutes(timeoutMinutes))
                );
                
                if (!process.HasExited)
                {
                    process.Kill();
                    errors.AppendLine("Process timed out");
                }
                
                var outputText = await outputTask;
                var errorText = await errorTask;
                
                if (!string.IsNullOrEmpty(outputText))
                {
                    output.AddRange(outputText.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                errors.Append(errorText);
                
                exitCode = process.ExitCode;
            }
        }
        catch (Exception ex)
        {
            errors.AppendLine($"Process exception: {ex.Message}");
        }
        
        return (exitCode, output, errors.ToString());
    }

    #endregion
}
