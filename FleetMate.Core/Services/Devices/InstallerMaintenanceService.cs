using System.Diagnostics;
using System.Text.RegularExpressions;
using FleetMate.Core.Config;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FleetMate.Core.Services.Devices;

/// <summary>One installer-type package's install_location finding.</summary>
public sealed class InstallerTypeIssue
{
    public string Package { get; init; } = "";
    public string Location { get; init; } = ""; // "packages" | "installers"
    public string Issue { get; init; } = "";
    public bool Fixed { get; set; }
}

public sealed class InstallerTypeResult
{
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Fixed { get; set; }
    public List<InstallerTypeIssue> Issues { get; } = new();
    public int Failed => Issues.Count(i => !i.Fixed);
}

public sealed class BulkOpItem
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public List<string> Output { get; init; } = new();
}

public sealed class BulkOpResult
{
    public bool DryRun { get; set; }
    public List<BulkOpItem> Items { get; } = new();
    public int Succeeded => Items.Count(i => i.Success);
    public int Failed => Items.Count(i => !i.Success && !DryRun);
}

/// <summary>
/// Native port of control.ps1's installer-maintenance sub-modes:
/// Test-InstallerTypePackages (--check-installer-type), Invoke-RepkgInstallers
/// (--repkg-installers), Invoke-CimiImportAll (--import-all). No PowerShell.
/// </summary>
public sealed class InstallerMaintenanceService
{
    private readonly string _packagesRoot;
    private readonly string _installersRoot;
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();

    public InstallerMaintenanceService(FleetMateConfig config)
    {
        _packagesRoot = config.ResolvePath(config.PackagesPath);
        _installersRoot = config.ResolvePath(config.InstallersPath);
    }

    /// <summary>Installer-type packages (MSI/EXE in payload/) must have an EMPTY install_location.
    /// Reports (and optionally fixes by emptying it) any that are missing or non-empty.</summary>
    public InstallerTypeResult CheckInstallerTypes(bool fix)
    {
        var result = new InstallerTypeResult();
        foreach (var (dir, location) in EnumerateBuildInfoDirs())
        {
            var payload = Path.Combine(dir, "payload");
            if (!Directory.Exists(payload)) continue;
            var hasInstaller = Directory.EnumerateFiles(payload, "*.*", SearchOption.AllDirectories)
                .Any(f => f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (!hasInstaller) continue;

            result.Total++;
            var buildInfoPath = Path.Combine(dir, "build-info.yaml");
            var name = new DirectoryInfo(dir).Name;

            var (hasField, value) = ReadInstallLocation(buildInfoPath);
            var isEmpty = hasField && string.IsNullOrWhiteSpace(value);
            if (hasField && isEmpty) { result.Passed++; continue; }

            var issue = new InstallerTypeIssue
            {
                Package = name,
                Location = location,
                Issue = !hasField ? "Missing install_location field" : "install_location should be empty for installer-type packages",
            };
            if (fix && hasField)
            {
                issue.Fixed = EmptyInstallLocation(buildInfoPath);
                if (issue.Fixed) result.Fixed++;
            }
            result.Issues.Add(issue);
        }
        return result;
    }

    /// <summary>Run `cimipkg .` in every installers/ package dir that has a build-info.yaml.</summary>
    public async Task<BulkOpResult> RepkgInstallersAsync(bool dryRun)
    {
        var result = new BulkOpResult { DryRun = dryRun };
        if (!Directory.Exists(_installersRoot)) return result;

        foreach (var dir in Directory.EnumerateDirectories(_installersRoot, "*", SearchOption.AllDirectories)
                     .Where(d => File.Exists(Path.Combine(d, "build-info.yaml"))))
        {
            var item = new BulkOpItem { Name = new DirectoryInfo(dir).Name, Path = dir };
            if (dryRun) { item.Success = true; result.Items.Add(item); continue; }
            var (code, outp, err) = await RunAsync("cimipkg.exe", ".", dir);
            item.ExitCode = code;
            item.Success = code == 0;
            item.Output.AddRange(outp);
            if (!string.IsNullOrEmpty(err)) item.Output.Add(err);
            result.Items.Add(item);
        }
        return result;
    }

    /// <summary>Run `cimiimport --auto` on every build/*.pkg|*.nupkg under installers/.</summary>
    public async Task<BulkOpResult> CimiImportAllAsync(bool dryRun)
    {
        var result = new BulkOpResult { DryRun = dryRun };
        if (!Directory.Exists(_installersRoot)) return result;

        var files = Directory.EnumerateFiles(_installersRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
                        && string.Equals(Path.GetFileName(Path.GetDirectoryName(f)), "build", StringComparison.OrdinalIgnoreCase));

        foreach (var file in files)
        {
            var item = new BulkOpItem { Name = Path.GetFileName(file), Path = file };
            if (dryRun) { item.Success = true; result.Items.Add(item); continue; }
            var (code, outp, err) = await RunAsync("cimiimport.exe", $"--auto \"{file}\"", Path.GetDirectoryName(file));
            item.ExitCode = code;
            // cimiimport returns >0 when the item is already imported; treat only run failures as failure
            item.Success = code == 0;
            item.Output.AddRange(outp);
            if (!string.IsNullOrEmpty(err)) item.Output.Add(err);
            result.Items.Add(item);
        }
        return result;
    }

    private IEnumerable<(string Dir, string Location)> EnumerateBuildInfoDirs()
    {
        foreach (var (root, label) in new[] { (_packagesRoot, "packages"), (_installersRoot, "installers") })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .Where(d => File.Exists(Path.Combine(d, "build-info.yaml"))))
                yield return (dir, label);
        }
    }

    private static (bool HasField, string? Value) ReadInstallLocation(string buildInfoPath)
    {
        try
        {
            var data = Yaml.Deserialize<Dictionary<string, object>>(File.ReadAllText(buildInfoPath));
            if (data == null) return (false, null);
            if (data.TryGetValue("product", out var p) && p is IDictionary<object, object> prod
                && prod.TryGetValue("install_location", out var nested))
                return (true, nested?.ToString());
            if (data.TryGetValue("install_location", out var flat))
                return (true, flat?.ToString());
        }
        catch { /* unparseable build-info -> treated as no field */ }
        return (false, null);
    }

    /// <summary>Empty the install_location value in-place, preserving formatting (text edit).</summary>
    private static bool EmptyInstallLocation(string buildInfoPath)
    {
        try
        {
            var text = File.ReadAllText(buildInfoPath);
            // Match `install_location:` (optionally indented, nested or flat) with any value, keep the key + indent.
            var updated = Regex.Replace(text,
                @"(?m)^(\s*install_location:)[^\r\n]*$",
                "$1 ");
            if (updated == text) return false;
            File.WriteAllText(buildInfoPath, updated);
            return true;
        }
        catch { return false; }
    }

    private static async Task<(int Code, List<string> Output, string Err)> RunAsync(string fileName, string arguments, string? workingDir)
    {
        var output = new List<string>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDir ?? Directory.GetCurrentDirectory(),
            };
            using var p = Process.Start(psi);
            if (p == null) return (-1, output, "Failed to start process");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var o = await outTask;
            if (!string.IsNullOrEmpty(o)) output.AddRange(o.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            return (p.ExitCode, output, (await errTask).Trim());
        }
        catch (Exception ex)
        {
            return (-1, output, ex.Message);
        }
    }
}
