using System.CommandLine;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Services.Tickets;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Reporting;
using Spectre.Console;

namespace FleetMate.Commands.Shared;

public static class ValidateCommand
{
    public static Command Create(FleetMateConfig config, PkgInfoService pkgInfo)
    {
        var command = new Command("validate", "Validate a package's pkginfo and installer");
        
        var packageArg = new Argument<string>(
            name: "package",
            description: "Package name to validate");
        
        command.AddArgument(packageArg);
        
        command.SetHandler((package) =>
        {
            Execute(config, pkgInfo, package);
        }, packageArg);
        
        return command;
    }
    
    private static void Execute(FleetMateConfig config, PkgInfoService pkgInfo, string package)
    {
        AnsiConsole.Write(new Rule($"[cyan]Validating: {package}[/]").LeftJustified());
        Console.WriteLine();
        
        // Find package location
        var location = pkgInfo.FindPackage(package);
        
        if (location == null)
        {
            AnsiConsole.MarkupLine($"[red]✗ Package not found:[/] {package}");
            AnsiConsole.MarkupLine("[dim]Searched: packages/, installers/, deployment/pkgsinfo/[/]");
            return;
        }
        
        // Show where we found it
        AnsiConsole.MarkupLine($"[green]✓ Found in:[/] {location.Source} ({location.Path})");
        
        if (location.IsVersioned)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ Versioned package - available versions:[/]");
            foreach (var ver in location.AvailableVersions ?? new List<string>())
            {
                AnsiConsole.MarkupLine($"    • {ver}");
            }
            AnsiConsole.MarkupLine($"[dim]Specify version: fleetmate validate {package}\\<version>[/]");
            return;
        }
        
        Console.WriteLine();
        
        // Find and validate pkginfo
        var pkgInfoPath = location.PkgInfoPath ?? pkgInfo.FindPkgInfo(package);
        
        if (pkgInfoPath == null)
        {
            AnsiConsole.MarkupLine("[yellow]⚠ No pkginfo file found in deployment repo[/]");
            AnsiConsole.MarkupLine("[dim]Run: cimiimport to create pkginfo[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[green]✓ pkginfo:[/] {GetRelativePath(config, pkgInfoPath)}");
            
            // Validate the pkginfo
            var validation = pkgInfo.ValidatePkgInfo(pkgInfoPath);
            
            if (validation.IsValid && !validation.Issues.Any())
            {
                AnsiConsole.MarkupLine("[green]✓ pkginfo is valid[/]");
            }
            else
            {
                Console.WriteLine();
                
                foreach (var issue in validation.Issues)
                {
                    var icon = issue.Severity switch
                    {
                        ValidationSeverity.Error => "[red]✗[/]",
                        ValidationSeverity.Warning => "[yellow]⚠[/]",
                        _ => "[dim]ℹ[/]"
                    };
                    
                    AnsiConsole.MarkupLine($"  {icon} {Markup.Escape(issue.Message)}");
                }
            }
        }
        
        Console.WriteLine();
        
        // Check for build-info.yaml (packages/installers)
        if (location.Source is PackageSource.Packages or PackageSource.Installers)
        {
            var buildInfoPath = Path.Combine(location.Path, "build-info.yaml");
            if (File.Exists(buildInfoPath))
            {
                AnsiConsole.MarkupLine("[green]✓ build-info.yaml found[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]⚠ No build-info.yaml - package cannot be built with cimipkg[/]");
            }
            
            // Check for tools folder (nupkg packages)
            var toolsPath = Path.Combine(location.Path, "tools");
            if (Directory.Exists(toolsPath))
            {
                var scripts = Directory.GetFiles(toolsPath, "*.ps1");
                if (scripts.Length > 0)
                {
                    AnsiConsole.MarkupLine($"[green]✓ tools/ folder with {scripts.Length} script(s)[/]");
                }
            }
        }
        
        Console.WriteLine();
        AnsiConsole.MarkupLine($"[dim]Run 'fleetmate test {package}' for full quality testing[/]");
    }
    
    private static string GetRelativePath(FleetMateConfig config, string fullPath)
    {
        if (config.RepoRoot != null && fullPath.StartsWith(config.RepoRoot))
        {
            return fullPath[(config.RepoRoot.Length + 1)..];
        }
        return fullPath;
    }
}
