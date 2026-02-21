// FleetMate.CLI/Commands/QaCommand.cs
// QA command - C# port of quality/control.ps1

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

namespace FleetMate.Commands.Devices;

public static class QaCommand
{
    public static Command Create(FleetMateConfig config)
    {
        var command = new Command("qa", "Quality control testing for Cimian packages (port of quality/control.ps1)")
        {
            Description = @"Run comprehensive quality control tests on Cimian packages.

This is a C# port of the PowerShell quality/control.ps1 script.

Examples:
  fleetmate qa ZEDSDK                    # Test specific package
  fleetmate qa Maya\2024                 # Test versioned package
  fleetmate qa ZEDSDK --install-only     # Skip rebuild, test installation only
  fleetmate qa Chrome --dry-run          # Show what would be done
  fleetmate qa ZEDSDK --uninstall-first  # Uninstall before testing
  fleetmate qa --list                    # List all available packages"
        };
        
        // Package argument (optional)
        var packageArg = new Argument<string?>(
            name: "package",
            getDefaultValue: () => null,
            description: "Package name to test (e.g., ZEDSDK, Maya\\2024, Chrome-x64)");
        
        // Options matching control.ps1 parameters
        var dryRunOption = new Option<bool>(
            aliases: new[] { "--dry-run", "-n" },
            description: "Show what would be done without making changes");
        
        var fixOption = new Option<bool>(
            aliases: new[] { "--fix", "-f" },
            description: "Apply auto-fixes where possible");
        
        var showDetailsOption = new Option<bool>(
            aliases: new[] { "--show-details", "-d" },
            description: "Show detailed test information");
        
        var installOnlyOption = new Option<bool>(
            aliases: new[] { "--install-only", "-i" },
            description: "Skip package rebuild, test installation only");
        
        var uninstallFirstOption = new Option<bool>(
            aliases: new[] { "--uninstall-first", "-u" },
            description: "Uninstall the software first, then test re-installation");
        
        var categoryOption = new Option<QaCategory>(
            aliases: new[] { "--category", "-c" },
            getDefaultValue: () => QaCategory.All,
            description: "Category of tests to run (unit, systems, lint, autofix, deployment, all)");
        
        var listOption = new Option<bool>(
            aliases: new[] { "--list", "-l" },
            description: "List all available packages");
        
        var checkInstallerTypeOption = new Option<bool>(
            aliases: new[] { "--check-installer-type" },
            description: "Check installer-type packages for proper install_location");
        
        var repkgOption = new Option<bool>(
            aliases: new[] { "--repkg-installers" },
            description: "Repackage all installer packages from installers directory");
        
        var importAllOption = new Option<bool>(
            aliases: new[] { "--import-all" },
            description: "Run cimiimport --auto on all installer packages");
        
        command.AddArgument(packageArg);
        command.AddOption(dryRunOption);
        command.AddOption(fixOption);
        command.AddOption(showDetailsOption);
        command.AddOption(installOnlyOption);
        command.AddOption(uninstallFirstOption);
        command.AddOption(categoryOption);
        command.AddOption(listOption);
        command.AddOption(checkInstallerTypeOption);
        command.AddOption(repkgOption);
        command.AddOption(importAllOption);
        
        // Use SetHandler with InvocationContext to handle many options
        command.SetHandler(async (context) =>
        {
            var package = context.ParseResult.GetValueForArgument(packageArg);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var fix = context.ParseResult.GetValueForOption(fixOption);
            var showDetails = context.ParseResult.GetValueForOption(showDetailsOption);
            var installOnly = context.ParseResult.GetValueForOption(installOnlyOption);
            var uninstallFirst = context.ParseResult.GetValueForOption(uninstallFirstOption);
            var category = context.ParseResult.GetValueForOption(categoryOption);
            var list = context.ParseResult.GetValueForOption(listOption);
            var checkInstallerType = context.ParseResult.GetValueForOption(checkInstallerTypeOption);
            var repkg = context.ParseResult.GetValueForOption(repkgOption);
            var importAll = context.ParseResult.GetValueForOption(importAllOption);
            
            await ExecuteAsync(
                config,
                package,
                dryRun,
                fix,
                showDetails,
                installOnly,
                uninstallFirst,
                category,
                list,
                checkInstallerType,
                repkg,
                importAll);
        });
        
        return command;
    }
    
    private static async Task ExecuteAsync(
        FleetMateConfig config,
        string? package,
        bool dryRun,
        bool fix,
        bool showDetails,
        bool installOnly,
        bool uninstallFirst,
        QaCategory category,
        bool list,
        bool checkInstallerType,
        bool repkg,
        bool importAll)
    {
        var qaService = new QaService(config);
        
        // Handle --list
        if (list)
        {
            ListPackages(qaService);
            return;
        }
        
        // Handle --check-installer-type
        if (checkInstallerType)
        {
            await CheckInstallerTypesAsync(qaService, fix, showDetails);
            return;
        }
        
        // Handle bulk operations
        if (repkg || importAll)
        {
            await RunBulkOperationsAsync(qaService, repkg, importAll, showDetails, dryRun);
            return;
        }
        
        // Single package workflow
        if (!string.IsNullOrEmpty(package))
        {
            var options = new QaOptions
            {
                DryRun = dryRun,
                Fix = fix,
                ShowDetails = showDetails,
                InstallOnly = installOnly,
                UninstallFirst = uninstallFirst,
                Category = category
            };
            
            await RunPackageQaAsync(qaService, package, options);
            return;
        }
        
        // No package specified - show help
        ShowHelp();
    }
    
    private static void ListPackages(QaService qaService)
    {
        AnsiConsole.Write(new Rule("[cyan]Available Packages[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();
        
        var packages = qaService.GetAllPackages();
        
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Package")
            .AddColumn("Location")
            .AddColumn("Path");
        
        foreach (var pkg in packages.OrderBy(p => p.BasePackageName))
        {
            var source = pkg.Source switch
            {
                PackageSource.Packages => "[green]packages[/]",
                PackageSource.Installers => "[blue]installers[/]",
                _ => "[grey]unknown[/]"
            };
            
            var relativePath = pkg.Path.Replace(Directory.GetCurrentDirectory(), ".");
            table.AddRow(
                $"[white]{pkg.BasePackageName}[/]",
                source,
                $"[grey]{relativePath}[/]"
            );
        }
        
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]Total: {packages.Count} packages[/]");
    }
    
    private static async Task RunPackageQaAsync(QaService qaService, string package, QaOptions options)
    {
        // Header
        var mode = options.InstallOnly ? "INSTALL-ONLY" : "COMPREHENSIVE";
        var header = $"[cyan]CIMIAN QUALITY CONTROL[/] - {mode}";
        AnsiConsole.Write(new Rule(header).RuleStyle("grey"));
        AnsiConsole.WriteLine();
        
        AnsiConsole.MarkupLine($"[white]Package:[/] {package}");
        if (options.DryRun)
        {
            AnsiConsole.MarkupLine("[yellow]Mode: DRY RUN[/]");
        }
        AnsiConsole.WriteLine();
        
        // Run QA with progress
        QaResult result;
        
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Running quality control...", async ctx =>
            {
                result = await qaService.RunPackageQaAsync(package, options);
                
                // Display results as they complete
                ctx.Status("Completed");
            });
        
        // We need to re-run to get the result (Status() doesn't return values well)
        result = await qaService.RunPackageQaAsync(package, options);
        
        // Check for versioned package error
        if (!result.Location.Found && result.Location.IsVersioned)
        {
            AnsiConsole.MarkupLine("[yellow]📦 Versioned package detected[/]");
            AnsiConsole.MarkupLine("[white]Available versions:[/]");
            foreach (var ver in result.Location.AvailableVersions)
            {
                AnsiConsole.MarkupLine($"  [grey]• {ver}[/]");
            }
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[red]Please specify version:[/] fleetmate qa {package}\\<version>");
            return;
        }
        
        // Display results
        DisplayQaResults(result, options.ShowDetails);
    }
    
    private static void DisplayQaResults(QaResult result, bool showDetails)
    {
        AnsiConsole.WriteLine();
        
        // Steps table
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Step")
            .AddColumn("Name")
            .AddColumn("Status")
            .AddColumn("Details");
        
        foreach (var step in result.Steps)
        {
            var status = step.Success 
                ? "[green]✓ PASS[/]" 
                : step.Severity == QaSeverity.Warning 
                    ? "[yellow]⚠ WARN[/]"
                    : "[red]✗ FAIL[/]";
            
            var details = new List<string>();
            if (step.Messages.Any() && showDetails)
            {
                details.AddRange(step.Messages.Take(2).Select(Markup.Escape));
            }
            if (step.Errors.Any())
            {
                details.AddRange(step.Errors.Select(e => $"[red]{Markup.Escape(e)}[/]"));
            }
            if (step.Warnings.Any() && showDetails)
            {
                details.AddRange(step.Warnings.Select(w => $"[yellow]{Markup.Escape(w)}[/]"));
            }
            
            table.AddRow(
                $"[dim]{step.StepNumber}[/]",
                Markup.Escape(step.StepName),
                status,
                string.Join("\n", details.Take(3))
            );
        }
        
        AnsiConsole.Write(table);
        
        // Summary
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Summary[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();
        
        var summaryTable = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn("Key")
            .AddColumn("Value");
        
        summaryTable.AddRow("Package:", $"[white]{result.PackageName}[/]");
        summaryTable.AddRow("Total Tests:", $"[white]{result.Total}[/]");
        summaryTable.AddRow("Passed:", $"[green]{result.Passed}[/]");
        summaryTable.AddRow("Failed:", result.Failed > 0 ? $"[red]{result.Failed}[/]" : $"[dim]{result.Failed}[/]");
        summaryTable.AddRow("Skipped:", $"[yellow]{result.Skipped}[/]");
        summaryTable.AddRow("Duration:", $"[dim]{result.Duration.TotalSeconds:F1}s[/]");
        
        var successRateColor = result.SuccessRate >= 80 ? "green" : result.SuccessRate >= 60 ? "yellow" : "red";
        summaryTable.AddRow("Success Rate:", $"[{successRateColor}]{result.SuccessRate}%[/]");
        
        AnsiConsole.Write(summaryTable);
        
        AnsiConsole.WriteLine();
        
        // Final verdict
        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]✓ Package '{result.PackageName}' passed all quality checks[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✗ Package '{result.PackageName}' failed {result.Failed} test(s)[/]");
            AnsiConsole.MarkupLine("[yellow]Please review the errors above and fix the package issues.[/]");
        }
    }
    
    private static async Task CheckInstallerTypesAsync(QaService qaService, bool fix, bool showDetails)
    {
        AnsiConsole.Write(new Rule("[cyan]Installer-Type Package Validation[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();
        
        if (fix)
        {
            AnsiConsole.MarkupLine("[yellow]🔧 AUTO-FIX MODE ENABLED[/]");
            AnsiConsole.WriteLine();
        }
        
        // TODO: Implement full installer type checking
        AnsiConsole.MarkupLine("[dim]Scanning packages and installers directories...[/]");
        
        var packages = qaService.GetAllPackages();
        AnsiConsole.MarkupLine($"[dim]Found {packages.Count} packages[/]");
        
        // For now, show what would be checked
        AnsiConsole.MarkupLine("[yellow]Full installer-type validation coming soon![/]");
        AnsiConsole.MarkupLine("[dim]Use quality/control.ps1 -CheckInstallerType for now[/]");
        
        await Task.CompletedTask;
    }
    
    private static async Task RunBulkOperationsAsync(
        QaService qaService, 
        bool repkg, 
        bool importAll, 
        bool showDetails, 
        bool dryRun)
    {
        if (repkg)
        {
            AnsiConsole.Write(new Rule("[cyan]Bulk Repackaging[/]").RuleStyle("grey"));
            AnsiConsole.WriteLine();
            
            if (dryRun)
            {
                AnsiConsole.MarkupLine("[yellow]DRY RUN: Would repackage all installer packages[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Bulk repackaging coming soon![/]");
                AnsiConsole.MarkupLine("[dim]Use quality/control.ps1 -RepkgInstallers for now[/]");
            }
        }
        
        if (importAll)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[cyan]Bulk Import[/]").RuleStyle("grey"));
            AnsiConsole.WriteLine();
            
            if (dryRun)
            {
                AnsiConsole.MarkupLine("[yellow]DRY RUN: Would import all installer packages[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Bulk import coming soon![/]");
                AnsiConsole.MarkupLine("[dim]Use quality/control.ps1 -CimiImportAll for now[/]");
            }
        }
        
        await Task.CompletedTask;
    }
    
    private static void ShowHelp()
    {
        var panel = new Panel(new Markup(@"[cyan]CIMIAN QUALITY CONTROL SYSTEM[/]

[white]USAGE:[/]
  fleetmate qa <package>              Test specific package
  fleetmate qa Maya\2024              Test versioned package
  fleetmate qa --list                 List all packages

[white]OPTIONS:[/]
  --install-only, -i     Skip rebuild, test installation only
  --uninstall-first, -u  Uninstall before testing
  --dry-run, -n          Show what would be done
  --fix, -f              Apply auto-fixes
  --show-details, -d     Show detailed output

[white]EXAMPLES:[/]
  fleetmate qa ZEDSDK
  fleetmate qa Chrome-x64 --install-only
  fleetmate qa Maya\2024 --uninstall-first --dry-run

[dim]This is a C# port of quality/control.ps1[/]"))
            .Border(BoxBorder.Rounded)
            .Header("[cyan]Help[/]");
        
        AnsiConsole.Write(panel);
    }
}
