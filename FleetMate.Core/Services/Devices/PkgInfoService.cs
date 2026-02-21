using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Devices;
using Serilog;

namespace FleetMate.Core.Services.Devices;

/// <summary>
/// Service for finding and analyzing pkginfo files
/// </summary>
public class PkgInfoService
{
    private readonly FleetMateConfig _config;
    private readonly IDeserializer _deserializer;
    
    // Cache of loaded pkginfos (for future use)
#pragma warning disable CS0169
    private Dictionary<string, Dictionary<string, object>>? _pkginfoCache;
#pragma warning restore CS0169
    
    public PkgInfoService(FleetMateConfig config)
    {
        _config = config;
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }
    
    /// <summary>
    /// Find a pkginfo file by item name
    /// </summary>
    public string? FindPkgInfo(string itemName)
    {
        var pkgsinfoPath = _config.ResolvePath(_config.PkgsinfoPath);
        
        if (!Directory.Exists(pkgsinfoPath))
        {
            Log.Warning("Pkgsinfo path not found: {Path}", pkgsinfoPath);
            return null;
        }
        
        try
        {
            var files = Directory.GetFiles(pkgsinfoPath, "*.yaml", SearchOption.AllDirectories);
            
            // Try exact match first (name-version.yaml or name.yaml)
            var exactMatch = files.FirstOrDefault(f =>
            {
                var fileName = Path.GetFileNameWithoutExtension(f);
                return fileName.Equals(itemName, StringComparison.OrdinalIgnoreCase) ||
                       fileName.StartsWith(itemName + "-", StringComparison.OrdinalIgnoreCase);
            });
            
            if (exactMatch != null)
                return exactMatch;
            
            // Try finding by 'name' field inside yaml files
            foreach (var file in files)
            {
                try
                {
                    var content = File.ReadAllText(file);
                    if (content.Contains($"name: {itemName}", StringComparison.OrdinalIgnoreCase) ||
                        content.Contains($"name: '{itemName}'", StringComparison.OrdinalIgnoreCase) ||
                        content.Contains($"name: \"{itemName}\"", StringComparison.OrdinalIgnoreCase))
                    {
                        return file;
                    }
                }
                catch { /* Skip unreadable files */ }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error searching for pkginfo: {ItemName}", itemName);
        }
        
        return null;
    }
    
    /// <summary>
    /// Load a pkginfo file as a dictionary
    /// </summary>
    public Dictionary<string, object>? LoadPkgInfo(string path)
    {
        try
        {
            var yaml = File.ReadAllText(path);
            return _deserializer.Deserialize<Dictionary<string, object>>(yaml);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to parse pkginfo: {Path}", path);
            return null;
        }
    }
    
    /// <summary>
    /// Get all pkginfo files in the deployment repo
    /// </summary>
    public IEnumerable<string> GetAllPkgInfoPaths()
    {
        var pkgsinfoPath = _config.ResolvePath(_config.PkgsinfoPath);
        
        if (!Directory.Exists(pkgsinfoPath))
        {
            Log.Warning("Pkgsinfo path not found: {Path}", pkgsinfoPath);
            yield break;
        }
        
        foreach (var file in Directory.EnumerateFiles(pkgsinfoPath, "*.yaml", SearchOption.AllDirectories))
        {
            yield return file;
        }
    }
    
    /// <summary>
    /// Validate a pkginfo file
    /// </summary>
    public PkgInfoValidation ValidatePkgInfo(string path)
    {
        var result = new PkgInfoValidation
        {
            FilePath = path,
            IsValid = true
        };
        
        var data = LoadPkgInfo(path);
        if (data == null)
        {
            result.IsValid = false;
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Error,
                Message = "Failed to parse YAML file"
            });
            return result;
        }
        
        // Check required fields
        if (!data.TryGetValue("name", out var name) || string.IsNullOrEmpty(name?.ToString()))
        {
            result.IsValid = false;
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Error,
                Field = "name",
                Message = "Missing required field: name"
            });
        }
        else
        {
            result.Name = name.ToString()!;
        }
        
        if (!data.TryGetValue("version", out var version) || string.IsNullOrEmpty(version?.ToString()))
        {
            result.IsValid = false;
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Error,
                Field = "version",
                Message = "Missing required field: version"
            });
        }
        else
        {
            result.Version = version.ToString()!;
        }
        
        // Check catalogs
        if (!data.ContainsKey("catalogs"))
        {
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Warning,
                Field = "catalogs",
                Message = "No catalogs specified - package won't be deployed"
            });
        }
        
        // Check installer section
        if (data.TryGetValue("installer", out var installerObj) && installerObj is Dictionary<object, object> installer)
        {
            if (!installer.ContainsKey("type"))
            {
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Field = "installer.type",
                    Message = "Installer type not specified"
                });
            }
            
            if (installer.TryGetValue("location", out var location))
            {
                var locationStr = location?.ToString();
                if (!string.IsNullOrEmpty(locationStr))
                {
                    var pkgsPath = _config.ResolvePath(_config.PkgsPath);
                    var fullPath = Path.Combine(pkgsPath, locationStr.TrimStart('\\', '/'));
                    
                    if (!File.Exists(fullPath))
                    {
                        result.Issues.Add(new ValidationIssue
                        {
                            Severity = ValidationSeverity.Error,
                            Field = "installer.location",
                            Message = $"Installer file not found: {locationStr}",
                            Suggestion = "Run makecatalogs to sync, or check the file path"
                        });
                    }
                }
            }
            
            // Hash validation
            if (!installer.ContainsKey("hash"))
            {
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Field = "installer.hash",
                    Message = "No hash specified - integrity cannot be verified"
                });
            }
        }
        else if (!data.ContainsKey("installcheck_script"))
        {
            // No installer and no installcheck_script
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Warning,
                Message = "No installer section and no installcheck_script"
            });
        }
        
        // Check for common issues
        if (data.ContainsKey("installs") && data.ContainsKey("installcheck_script"))
        {
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Info,
                Message = "Both 'installs' array and 'installcheck_script' present - script takes precedence"
            });
        }
        
        // Validate supported_architectures
        if (data.TryGetValue("supported_architectures", out var archs))
        {
            var archList = archs switch
            {
                List<object> list => list.Select(a => a.ToString()?.ToLowerInvariant()).ToList(),
                _ => new List<string?> { archs.ToString()?.ToLowerInvariant() }
            };
            
            var validArchs = new[] { "x64", "arm64", "x86" };
            foreach (var arch in archList)
            {
                if (!string.IsNullOrEmpty(arch) && !validArchs.Contains(arch))
                {
                    result.Issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Warning,
                        Field = "supported_architectures",
                        Message = $"Unknown architecture: {arch}",
                        Suggestion = "Valid values: x64, arm64, x86"
                    });
                }
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Find a package location (packages, installers, or deployment)
    /// </summary>
    public PackageLocation? FindPackage(string packageName)
    {
        // Check ./packages first
        var packagesPath = _config.ResolvePath(_config.PackagesPath);
        var packagePath = Path.Combine(packagesPath, packageName);
        
        if (Directory.Exists(packagePath))
        {
            // Check for versioned subfolders
            var subDirs = Directory.GetDirectories(packagePath);
            var hasVersions = subDirs.Any(d => 
            {
                var name = Path.GetFileName(d);
                return name.All(c => char.IsDigit(c) || c == '.');
            });
            
            if (hasVersions)
            {
                return new PackageLocation
                {
                    Name = packageName,
                    Source = PackageSource.Packages,
                    Path = packagePath,
                    IsVersioned = true,
                    AvailableVersions = subDirs.Select(Path.GetFileName).ToList()!
                };
            }
            
            return new PackageLocation
            {
                Name = packageName,
                Source = PackageSource.Packages,
                Path = packagePath
            };
        }
        
        // Check ./installers
        var installersPath = _config.ResolvePath(_config.InstallersPath);
        var installerPath = Path.Combine(installersPath, packageName);
        
        if (Directory.Exists(installerPath))
        {
            var subDirs = Directory.GetDirectories(installerPath);
            var hasVersions = subDirs.Any(d => 
            {
                var name = Path.GetFileName(d);
                return name.All(c => char.IsDigit(c) || c == '.');
            });
            
            if (hasVersions)
            {
                return new PackageLocation
                {
                    Name = packageName,
                    Source = PackageSource.Installers,
                    Path = installerPath,
                    IsVersioned = true,
                    AvailableVersions = subDirs.Select(Path.GetFileName).ToList()!
                };
            }
            
            return new PackageLocation
            {
                Name = packageName,
                Source = PackageSource.Installers,
                Path = installerPath
            };
        }
        
        // Check deployment/pkgsinfo
        var pkginfoPath = FindPkgInfo(packageName);
        if (pkginfoPath != null)
        {
            return new PackageLocation
            {
                Name = packageName,
                Source = PackageSource.Deployment,
                Path = Path.GetDirectoryName(pkginfoPath)!,
                PkgInfoPath = pkginfoPath
            };
        }
        
        return null;
    }
}
