using System.CommandLine;
using FleetMate.Core.Models.Reporting;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Services.Tickets;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Reporting;
using Spectre.Console;

namespace FleetMate.Commands.Devices;

public static class ErrorsCommand
{
    public static Command Create(ReportMateService reportMate, PkgInfoService pkgInfo)
    {
        var command = new Command("errors", "List installation errors from the fleet");
        
        var byItemOption = new Option<bool>(
            aliases: new[] { "--by-item", "-i" },
            description: "Group errors by item name (default)");
        
        var byDeviceOption = new Option<bool>(
            aliases: new[] { "--by-device", "-d" },
            description: "Group errors by device");
        
        var categoryOption = new Option<string?>(
            aliases: new[] { "--category", "-c" },
            description: "Filter by error category (notfound, hash, download, msi, signature, catalog, choco, sbin, verify, other)");
        
        var itemOption = new Option<string?>(
            aliases: new[] { "--item" },
            description: "Show details for a specific item");
        
        var deviceOption = new Option<string?>(
            aliases: new[] { "--device" },
            description: "Show errors for a specific device");
        
        var limitOption = new Option<int>(
            aliases: new[] { "--limit", "-n" },
            getDefaultValue: () => 20,
            description: "Maximum number of results to show");
        
        var refreshOption = new Option<bool>(
            aliases: new[] { "--refresh", "-r" },
            description: "Force refresh from API (ignore cache)");
        
        command.AddOption(byItemOption);
        command.AddOption(byDeviceOption);
        command.AddOption(categoryOption);
        command.AddOption(itemOption);
        command.AddOption(deviceOption);
        command.AddOption(limitOption);
        command.AddOption(refreshOption);
        
        command.SetHandler(async (byItem, byDevice, category, item, device, limit, refresh) =>
        {
            await ExecuteAsync(reportMate, pkgInfo, byItem, byDevice, category, item, device, limit, refresh);
        }, byItemOption, byDeviceOption, categoryOption, itemOption, deviceOption, limitOption, refreshOption);
        
        return command;
    }
    
    private static async Task ExecuteAsync(
        ReportMateService reportMate,
        PkgInfoService pkgInfo,
        bool byItem,
        bool byDevice,
        string? category,
        string? item,
        string? device,
        int limit,
        bool refresh)
    {
        // Parse category filter
        ErrorCategory? categoryFilter = null;
        if (!string.IsNullOrEmpty(category))
        {
            categoryFilter = category.ToLowerInvariant() switch
            {
                "notfound" or "404" => ErrorCategory.NotFound,
                "hash" => ErrorCategory.HashMismatch,
                "download" => ErrorCategory.DownloadFailed,
                "msi" or "1603" => ErrorCategory.MsiFailure,
                "signature" or "sign" => ErrorCategory.SignatureRequired,
                "catalog" => ErrorCategory.CatalogMissing,
                "choco" or "chocolatey" => ErrorCategory.MissingChocolatey,
                "sbin" => ErrorCategory.MissingSbinInstaller,
                "verify" => ErrorCategory.InstallVerificationFailed,
                "other" => ErrorCategory.Other,
                _ => null
            };
            
            if (categoryFilter == null)
            {
                AnsiConsole.MarkupLine($"[red]Unknown category:[/] {category}");
                AnsiConsole.MarkupLine("[dim]Valid categories: notfound, hash, download, msi, signature, catalog, choco, sbin, verify, other[/]");
                return;
            }
        }
        
        // Fetch errors
        var errors = await reportMate.GetErrorsAsync(refresh);
        
        if (errors.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No installation errors found![/]");
            return;
        }
        
        // Apply category filter
        if (categoryFilter.HasValue)
        {
            errors = errors.Where(e => e.Category == categoryFilter.Value).ToList();
        }
        
        // Handle specific item view
        if (!string.IsNullOrEmpty(item))
        {
            ShowItemDetails(errors, item, pkgInfo);
            return;
        }
        
        // Handle specific device view
        if (!string.IsNullOrEmpty(device))
        {
            ShowDeviceDetails(errors, device);
            return;
        }
        
        // Show grouped view
        if (byDevice)
        {
            ShowByDevice(errors, limit);
        }
        else
        {
            ShowByItem(errors, limit);
        }
    }
    
    private static void ShowByItem(List<InstallRecord> errors, int limit)
    {
        var grouped = errors
            .GroupBy(e => e.ItemName)
            .Select(g => new
            {
                ItemName = g.Key,
                Count = g.Count(),
                Category = g.First().Category,
                SampleError = g.First().LastError ?? string.Empty,
                Devices = g.Select(e => (e.DeviceName, e.SerialNumber)).ToList()
            })
            .OrderByDescending(g => g.Count)
            .Take(limit)
            .ToList();
        
        AnsiConsole.MarkupLine($"[cyan]Installation Errors by Item[/] ({errors.Count} total errors)\n");
        
        foreach (var item in grouped)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(item.ItemName)}[/] - {item.Count} device(s) [dim]({item.Category})[/]");
            
            var truncatedError = item.SampleError.Length > 80 
                ? item.SampleError[..77] + "..." 
                : item.SampleError;
            AnsiConsole.MarkupLine($"  [dim]└─ {Markup.Escape(truncatedError)}[/]");
            
            // Show first few devices
            foreach (var (name, serial) in item.Devices.Take(3))
            {
                AnsiConsole.MarkupLine($"      [dim]{Markup.Escape(name)} ({serial})[/]");
            }
            
            if (item.Devices.Count > 3)
            {
                AnsiConsole.MarkupLine($"     [dim]... and {item.Devices.Count - 3} more[/]");
            }
            
            Console.WriteLine();
        }
        
        AnsiConsole.MarkupLine("[dim]Use 'fleetmate errors --item <name>' for details on a specific item[/]");
    }
    
    private static void ShowByDevice(List<InstallRecord> errors, int limit)
    {
        var grouped = errors
            .GroupBy(e => e.SerialNumber)
            .Select(g => new
            {
                DeviceName = g.First().DeviceName,
                SerialNumber = g.Key,
                Location = g.First().Location,
                Count = g.Count(),
                Items = g.Select(e => e.ItemName).ToList()
            })
            .OrderByDescending(g => g.Count)
            .Take(limit)
            .ToList();
        
        AnsiConsole.MarkupLine($"[cyan]Devices with Installation Errors[/] ({grouped.Count} devices)\n");
        
        foreach (var device in grouped)
        {
            var location = !string.IsNullOrEmpty(device.Location) ? $" @ {device.Location}" : "";
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(device.DeviceName)}[/] ({device.SerialNumber}){location} - {device.Count} error(s)");
            
            foreach (var item in device.Items.Take(5))
            {
                AnsiConsole.MarkupLine($"  [dim]└─ {Markup.Escape(item)}[/]");
            }
            
            if (device.Items.Count > 5)
            {
                AnsiConsole.MarkupLine($"  [dim]└─ ... and {device.Items.Count - 5} more[/]");
            }
            
            Console.WriteLine();
        }
        
        AnsiConsole.MarkupLine("[dim]Use 'fleetmate errors --device <serial>' for details on a specific device[/]");
    }
    
    private static void ShowItemDetails(List<InstallRecord> errors, string itemName, PkgInfoService pkgInfo)
    {
        var itemErrors = errors
            .Where(e => e.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        
        if (itemErrors.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No errors found for item:[/] {itemName}");
            return;
        }
        
        // Get all installs for this item (not just errors)
        var allInstalls = itemErrors.Count; // We'd need full installs data for total count
        
        AnsiConsole.MarkupLine($"[cyan]Item:[/] {itemName}");
        AnsiConsole.MarkupLine($"[cyan]Errors:[/] {itemErrors.Count}\n");
        
        // Group by error category
        var byCategory = itemErrors
            .GroupBy(e => e.Category)
            .OrderByDescending(g => g.Count())
            .ToList();
        
        foreach (var catGroup in byCategory)
        {
            AnsiConsole.MarkupLine($"[yellow]{catGroup.Key}[/] ({catGroup.Count()} devices):");
            AnsiConsole.MarkupLine($"  [red]Error:[/] {Markup.Escape(catGroup.First().LastError ?? "(no error message)")}");
            AnsiConsole.MarkupLine("  [dim]Affected devices:[/]");
            
            foreach (var error in catGroup.Take(10))
            {
                var location = !string.IsNullOrEmpty(error.Location) ? $" @ {error.Location}" : "";
                AnsiConsole.MarkupLine($"     {Markup.Escape(error.DeviceName)} ({error.SerialNumber}){location}");
            }
            
            if (catGroup.Count() > 10)
            {
                AnsiConsole.MarkupLine($"    [dim]... and {catGroup.Count() - 10} more[/]");
            }
            
            Console.WriteLine();
        }
        
        // Show remediation suggestions
        var primaryCategory = byCategory.First().Key;
        ShowRemediation(primaryCategory, itemName);
        
        AnsiConsole.MarkupLine($"\n[dim]Run: fleetmate troubleshoot <serial> {itemName}[/]");
    }
    
    private static void ShowDeviceDetails(List<InstallRecord> errors, string deviceQuery)
    {
        var deviceErrors = errors
            .Where(e => e.SerialNumber.Equals(deviceQuery, StringComparison.OrdinalIgnoreCase) ||
                        e.DeviceName.Equals(deviceQuery, StringComparison.OrdinalIgnoreCase))
            .ToList();
        
        if (deviceErrors.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No errors found for device:[/] {deviceQuery}");
            return;
        }
        
        var first = deviceErrors.First();
        AnsiConsole.MarkupLine($"[cyan]Device:[/] {first.DeviceName}");
        AnsiConsole.MarkupLine($"[cyan]Serial:[/] {first.SerialNumber}");
        AnsiConsole.MarkupLine($"[cyan]Location:[/] {first.Location}");
        AnsiConsole.MarkupLine($"[cyan]Catalog:[/] {first.Catalog}");
        AnsiConsole.MarkupLine($"[cyan]Last Seen:[/] {first.LastSeen?.ToString("g") ?? "Unknown"}\n");
        
        AnsiConsole.MarkupLine($"[red]Failed Items ({deviceErrors.Count}):[/]\n");
        
        foreach (var error in deviceErrors.OrderBy(e => e.ItemName))
        {
            AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(error.ItemName)}[/] ({error.Category})");
            if (!string.IsNullOrEmpty(error.LastError))
            {
                var truncated = error.LastError.Length > 70 
                    ? error.LastError[..67] + "..." 
                    : error.LastError;
                AnsiConsole.MarkupLine($"    [dim]{Markup.Escape(truncated)}[/]");
            }
        }
        
        AnsiConsole.MarkupLine($"\n[dim]Run: fleetmate troubleshoot {first.SerialNumber} <item>[/]");
    }
    
    private static void ShowRemediation(ErrorCategory category, string itemName)
    {
        AnsiConsole.MarkupLine("[cyan]Suggested remediation:[/]");
        
        switch (category)
        {
            case ErrorCategory.MissingChocolatey:
                AnsiConsole.MarkupLine("   Chocolatey not installed on device");
                AnsiConsole.MarkupLine("   Ensure Chocolatey is in managed_installs before this item");
                AnsiConsole.MarkupLine("   Or convert package to MSI/MSIX format");
                break;
            case ErrorCategory.MissingSbinInstaller:
                AnsiConsole.MarkupLine("   sbin-installer not available on device");
                AnsiConsole.MarkupLine("   Ensure SbinInstaller is in managed_installs before this item");
                break;
            case ErrorCategory.DownloadFailed:
                AnsiConsole.MarkupLine("   Check CDN health and package availability");
                AnsiConsole.MarkupLine("   Verify network connectivity on affected devices");
                AnsiConsole.MarkupLine("   Check firewall rules for CDN access");
                break;
            case ErrorCategory.MsiFailure:
                AnsiConsole.MarkupLine("   MSI exit code 1603 typically indicates:");
                AnsiConsole.MarkupLine("     • Insufficient permissions");
                AnsiConsole.MarkupLine("     • Disk space issues");
                AnsiConsole.MarkupLine("     • Previous installation not cleaned up");
                AnsiConsole.MarkupLine("   Consider adding uninstallcheck_script to clean up first");
                break;
            case ErrorCategory.NotFound:
                AnsiConsole.MarkupLine("   Package file not found on CDN (404)");
                AnsiConsole.MarkupLine("   Run: makecatalogs to regenerate catalogs");
                AnsiConsole.MarkupLine("   Verify installer file exists in deployment/pkgs/");
                break;
            case ErrorCategory.HashMismatch:
                AnsiConsole.MarkupLine("   Downloaded file hash doesn't match pkginfo");
                AnsiConsole.MarkupLine("   Regenerate pkginfo with: cimiimport --auto");
                AnsiConsole.MarkupLine("   Or manually update hash in pkginfo");
                break;
            default:
                AnsiConsole.MarkupLine($"   Review error message for specific issue");
                AnsiConsole.MarkupLine($"   Check device logs for more details");
                break;
        }
    }
}
