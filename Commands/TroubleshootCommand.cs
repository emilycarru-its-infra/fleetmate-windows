using System.CommandLine;
using FleetMate.Config;
using FleetMate.Models;
using FleetMate.Services;
using Spectre.Console;

namespace FleetMate.Commands;

public static class TroubleshootCommand
{
    public static Command Create(ReportMateService reportMate, PkgInfoService pkgInfo, FleetMateConfig config)
    {
        var command = new Command("troubleshoot", "Diagnose a specific installation failure");
        
        var deviceArg = new Argument<string>(
            name: "device",
            description: "Device serial number or name");
        
        var itemArg = new Argument<string>(
            name: "item",
            description: "Item name to troubleshoot");
        
        var showPkgInfoOption = new Option<bool>(
            aliases: new[] { "--pkginfo", "-p" },
            description: "Show full pkginfo content");
        
        command.AddArgument(deviceArg);
        command.AddArgument(itemArg);
        command.AddOption(showPkgInfoOption);
        
        command.SetHandler(async (device, item, showPkgInfo) =>
        {
            await ExecuteAsync(reportMate, pkgInfo, config, device, item, showPkgInfo);
        }, deviceArg, itemArg, showPkgInfoOption);
        
        return command;
    }
    
    private static async Task ExecuteAsync(
        ReportMateService reportMate,
        PkgInfoService pkgInfo,
        FleetMateConfig config,
        string device,
        string item,
        bool showPkgInfoContent)
    {
        // Find the install record
        var installs = await reportMate.GetInstallsAsync();
        var record = installs.FirstOrDefault(i =>
            (i.SerialNumber.Equals(device, StringComparison.OrdinalIgnoreCase) ||
             i.DeviceName.Equals(device, StringComparison.OrdinalIgnoreCase)) &&
            i.ItemName.Equals(item, StringComparison.OrdinalIgnoreCase));
        
        if (record == null)
        {
            AnsiConsole.MarkupLine($"[yellow]No install record found for {item} on {device}[/]");
            return;
        }
        
        // Header
        AnsiConsole.Write(new Rule($"[cyan]Troubleshooting: {item} on {record.DeviceName}[/]").LeftJustified());
        Console.WriteLine();
        
        // Device info
        AnsiConsole.MarkupLine($"[bold]Device:[/] {Markup.Escape(record.DeviceName)}");
        AnsiConsole.MarkupLine($"[bold]Serial:[/] {record.SerialNumber}");
        AnsiConsole.MarkupLine($"[bold]Location:[/] {record.Location}");
        AnsiConsole.MarkupLine($"[bold]Catalog:[/] {record.Catalog}");
        AnsiConsole.MarkupLine($"[bold]Last Seen:[/] {record.LastSeen?.ToString("g") ?? "Unknown"}");
        Console.WriteLine();
        
        // Status info
        AnsiConsole.MarkupLine($"[red]Status:[/] {record.CurrentStatus}");
        AnsiConsole.MarkupLine($"[bold]Latest Version:[/] {record.LatestVersion}");
        AnsiConsole.MarkupLine($"[bold]Installed Version:[/] {record.InstalledVersion ?? "(none)"}");
        Console.WriteLine();
        
        // Error info
        AnsiConsole.MarkupLine("[yellow]Error Message:[/]");
        AnsiConsole.MarkupLine($"  [red]{Markup.Escape(record.LastError ?? "(no error message)")}[/]");
        Console.WriteLine();
        AnsiConsole.MarkupLine($"[yellow]Error Category:[/] {record.Category}");
        Console.WriteLine();
        
        // Package analysis
        AnsiConsole.Write(new Rule("[cyan]Package Analysis[/]").LeftJustified());
        
        var pkgInfoPath = pkgInfo.FindPkgInfo(item);
        if (pkgInfoPath == null)
        {
            AnsiConsole.MarkupLine("[red]✗ No pkginfo file found[/]");
            AnsiConsole.MarkupLine("  This could indicate:");
            AnsiConsole.MarkupLine("  • Package was removed from deployment repo");
            AnsiConsole.MarkupLine("  • Item name doesn't match pkginfo name");
            AnsiConsole.MarkupLine("  • pkginfo was never created");
        }
        else
        {
            AnsiConsole.MarkupLine($"[green]✓ Found:[/] {pkgInfoPath}");
            
            var pkgData = pkgInfo.LoadPkgInfo(pkgInfoPath);
            if (pkgData != null)
            {
                AnalyzePkgInfo(pkgData, record, config);
                
                if (showPkgInfoContent)
                {
                    Console.WriteLine();
                    AnsiConsole.Write(new Rule("[dim]Full pkginfo content[/]").LeftJustified());
                    Console.WriteLine(File.ReadAllText(pkgInfoPath));
                }
            }
        }
        
        // Remediation
        Console.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Remediation Steps[/]").LeftJustified());
        ShowRemediation(record.Category, record.SerialNumber);
        
        Console.WriteLine();
        AnsiConsole.MarkupLine("[dim]After fixing, run 'fleetmate errors --refresh' to see updated status[/]");
    }
    
    private static void AnalyzePkgInfo(Dictionary<string, object> pkgData, InstallRecord record, FleetMateConfig config)
    {
        // Version check
        if (pkgData.TryGetValue("version", out var version))
        {
            var pkgVersion = version.ToString();
            if (pkgVersion != record.LatestVersion)
            {
                AnsiConsole.MarkupLine($"[yellow]⚠ Version mismatch:[/] pkginfo has {pkgVersion}, ReportMate expects {record.LatestVersion}");
            }
            else
            {
                AnsiConsole.MarkupLine($"[bold]Version:[/] {pkgVersion}");
            }
        }
        
        // Installer section
        if (pkgData.TryGetValue("installer", out var installerObj) && installerObj is Dictionary<object, object> installer)
        {
            AnsiConsole.MarkupLine($"[bold]Installer type:[/] {installer.GetValueOrDefault("type", "unknown")}");
            
            if (installer.TryGetValue("location", out var location))
            {
                AnsiConsole.MarkupLine($"[bold]Installer location:[/] {location}");
                
                // Check if file exists
                var locationStr = location?.ToString() ?? string.Empty;
                var pkgsPath = config.ResolvePath(config.PkgsPath);
                var fullPath = Path.Combine(pkgsPath, locationStr.TrimStart('\\', '/'));
                
                if (File.Exists(fullPath))
                {
                    AnsiConsole.MarkupLine("[green]✓ Installer file exists locally[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]✗ Installer file NOT FOUND locally[/]");
                }
            }
            else
            {
                AnsiConsole.MarkupLine("[red]✗ No installer location specified[/]");
            }
            
            if (installer.TryGetValue("hash", out var hash))
            {
                var hashStr = hash?.ToString() ?? string.Empty;
                var truncated = hashStr.Length > 16 ? hashStr[..16] + "..." : hashStr;
                AnsiConsole.MarkupLine($"[bold]Hash:[/] {truncated}");
            }
        }
        
        // Scripts
        var scripts = new[] { "preinstall_script", "postinstall_script", "installcheck_script", "uninstallcheck_script" };
        var hasScripts = scripts.Where(s => pkgData.ContainsKey(s)).ToList();
        if (hasScripts.Any())
        {
            AnsiConsole.MarkupLine($"[bold]Scripts:[/] {string.Join(", ", hasScripts.Select(s => s.Replace("_script", "")))}");
        }
        
        // Catalogs
        if (pkgData.TryGetValue("catalogs", out var catalogs))
        {
            var catalogList = catalogs switch
            {
                List<object> list => list.Select(c => c.ToString()).ToList(),
                _ => new List<string?> { catalogs.ToString() }
            };
            
            // Escape catalog names to prevent Spectre markup interpretation
            var catalogDisplay = string.Join(", ", catalogList.Select(c => Markup.Escape(c ?? "")));
            
            if (!catalogList.Any(c => c?.Equals(record.Catalog, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                AnsiConsole.MarkupLine($"[yellow]⚠ Device catalog '{Markup.Escape(record.Catalog ?? "")}' not in pkginfo catalogs:[/] {catalogDisplay}");
            }
            else
            {
                AnsiConsole.MarkupLine($"[bold]Catalogs:[/] {catalogDisplay}");
            }
        }
        
        // Architectures
        if (pkgData.TryGetValue("supported_architectures", out var archs))
        {
            var archList = archs switch
            {
                List<object> list => string.Join(", ", list.Select(a => a.ToString())),
                _ => archs?.ToString() ?? "any"
            };
            AnsiConsole.MarkupLine($"[bold]Architectures:[/] {archList}");
        }
    }
    
    private static void ShowRemediation(ErrorCategory category, string serial)
    {
        switch (category)
        {
            case ErrorCategory.MissingChocolatey:
                AnsiConsole.MarkupLine("  1. Chocolatey is required for .nupkg packages");
                AnsiConsole.MarkupLine("  2. Ensure 'Chocolatey' is in managed_installs before this item");
                AnsiConsole.MarkupLine("  3. Or convert the package to MSI/MSIX format");
                AnsiConsole.MarkupLine($"  4. Check device for Chocolatey:");
                AnsiConsole.MarkupLine($"     [dim]fleetmate exec {serial} 'Test-Path C:\\ProgramData\\chocolatey\\bin\\choco.exe'[/]");
                break;
                
            case ErrorCategory.MissingSbinInstaller:
                AnsiConsole.MarkupLine("  1. sbin-installer is required for .pkg packages");
                AnsiConsole.MarkupLine("  2. Ensure 'SbinInstaller' is in managed_installs before this item");
                AnsiConsole.MarkupLine($"  3. Check device for sbin-installer:");
                AnsiConsole.MarkupLine($"     [dim]fleetmate exec {serial} 'Test-Path \"C:\\Program Files\\sbin\\installer.exe\"'[/]");
                break;
                
            case ErrorCategory.DownloadFailed:
                AnsiConsole.MarkupLine("  1. Check network connectivity from device");
                AnsiConsole.MarkupLine("  2. Verify CDN health and firewall rules");
                AnsiConsole.MarkupLine("  3. Check if the package file is too large for reliable download");
                AnsiConsole.MarkupLine($"  4. Review device logs:");
                AnsiConsole.MarkupLine($"     [dim]fleetmate logs {serial} --tail 50[/]");
                break;
                
            case ErrorCategory.MsiFailure:
                AnsiConsole.MarkupLine("  1. Exit code 1603 typically indicates:");
                AnsiConsole.MarkupLine("      • Insufficient permissions");
                AnsiConsole.MarkupLine("      • Disk space issues");
                AnsiConsole.MarkupLine("      • Previous installation not cleaned up");
                AnsiConsole.MarkupLine("      • Conflicting process running");
                AnsiConsole.MarkupLine("  2. Add an uninstallcheck_script to clean up before install");
                AnsiConsole.MarkupLine("  3. Check for required reboots");
                AnsiConsole.MarkupLine($"  4. Review MSI logs:");
                AnsiConsole.MarkupLine($"     [dim]fleetmate exec {serial} 'Get-ChildItem C:\\Windows\\Temp\\MSI*.log | Sort LastWriteTime -Desc | Select -First 1 | Get-Content -Tail 50'[/]");
                break;
                
            case ErrorCategory.NotFound:
                AnsiConsole.MarkupLine("  1. Verify the package file exists in deployment/pkgs/");
                AnsiConsole.MarkupLine("  2. Check that installer.location in pkginfo matches the actual file path");
                AnsiConsole.MarkupLine("  3. Run: makecatalogs to regenerate catalogs");
                AnsiConsole.MarkupLine("  4. Verify the package is uploaded to the CDN");
                break;
                
            case ErrorCategory.HashMismatch:
                AnsiConsole.MarkupLine("  1. The downloaded file hash doesn't match pkginfo");
                AnsiConsole.MarkupLine("  2. Regenerate pkginfo: cimiimport --auto <package>");
                AnsiConsole.MarkupLine("  3. Or manually update the hash in the pkginfo file");
                AnsiConsole.MarkupLine("  4. Verify the correct file is in deployment/pkgs/");
                break;
                
            case ErrorCategory.CatalogMissing:
                AnsiConsole.MarkupLine("  1. Item not found in any catalog");
                AnsiConsole.MarkupLine("  2. Add the item to appropriate catalogs in pkginfo");
                AnsiConsole.MarkupLine("  3. Run: makecatalogs to regenerate");
                break;
                
            default:
                AnsiConsole.MarkupLine("  1. Review the error message above for specific details");
                AnsiConsole.MarkupLine("  2. Check device logs for more context");
                AnsiConsole.MarkupLine($"     [dim]fleetmate logs {serial} --tail 100[/]");
                break;
        }
    }
}
