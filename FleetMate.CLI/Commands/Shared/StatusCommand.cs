#nullable disable warnings
using System.CommandLine;
using FleetMate.Core.Config;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Services.Tickets;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Reporting;
using Spectre.Console;

namespace FleetMate.Commands.Shared;

public static class StatusCommand
{
    public static Command Create(FleetMateConfig config, ReportMateService reportMate)
    {
        var command = new Command("status", "Show FleetMate status and configuration");
        
        command.SetHandler(async () =>
        {
            await ExecuteAsync(config, reportMate);
        });
        
        return command;
    }
    
    private static async Task ExecuteAsync(FleetMateConfig config, ReportMateService reportMate)
    {
        AnsiConsole.Write(new FigletText("FleetMate").Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[dim]Fleet orchestration, inventory, deployment monitoring, and troubleshooting[/]\n");
        
        // Configuration
        var configTable = new Table();
        configTable.Border = TableBorder.Rounded;
        configTable.Title = new TableTitle("[cyan]Configuration[/]");
        configTable.AddColumn("Setting");
        configTable.AddColumn("Value");
        
        configTable.AddRow("Repo Root", config.RepoRoot ?? "[dim](not found)[/]");
        configTable.AddRow("Deployment Path", config.ResolvePath(config.DeploymentPath));
        configTable.AddRow("Quality Path", config.ResolvePath(config.QualityPath));
        configTable.AddRow("Log Path", config.LogPath ?? "[dim](not set)[/]");
        configTable.AddRow("ReportMate URL", string.IsNullOrEmpty(config.ReportMateUrl) ? "[dim](not configured)[/]" : config.ReportMateUrl);
        configTable.AddRow("ReportMate Auth", string.IsNullOrEmpty(config.ReportMatePassphrase) ? "[red]Not configured[/]" : "[green]Configured[/]");
        
        AnsiConsole.Write(configTable);
        Console.WriteLine();
        
        // Check paths
        var pathsTable = new Table();
        pathsTable.Border = TableBorder.Rounded;
        pathsTable.Title = new TableTitle("[cyan]Path Status[/]");
        pathsTable.AddColumn("Path");
        pathsTable.AddColumn("Status");
        
        var paths = new[]
        {
            ("deployment/pkgsinfo", config.ResolvePath(config.PkgsinfoPath)),
            ("deployment/pkgs", config.ResolvePath(config.PkgsPath)),
            ("deployment/catalogs", config.ResolvePath(config.CatalogsPath)),
            ("deployment/manifests", config.ResolvePath(config.ManifestsPath)),
            ("packages", config.ResolvePath(config.PackagesPath)),
            ("installers", config.ResolvePath(config.InstallersPath)),
            ("quality", config.ResolvePath(config.QualityPath))
        };
        
        foreach (var (name, path) in paths)
        {
            var exists = Directory.Exists(path);
            var status = exists ? "[green]✓[/]" : "[red]✗[/]";
            pathsTable.AddRow(name, status);
        }
        
        AnsiConsole.Write(pathsTable);
        Console.WriteLine();
        
        // ReportMate status
        if (!string.IsNullOrEmpty(config.ReportMatePassphrase))
        {
            await AnsiConsole.Status()
                .StartAsync("Checking ReportMate connection...", async ctx =>
                {
                    try
                    {
                        var devices = await reportMate.GetDevicesAsync();
                        var errors = await reportMate.GetErrorsAsync();
                        
                        var fleetTable = new Table();
                        fleetTable.Border = TableBorder.Rounded;
                        fleetTable.Title = new TableTitle("[cyan]Fleet Status[/]");
                        fleetTable.AddColumn("Metric");
                        fleetTable.AddColumn("Value");
                        
                        fleetTable.AddRow("Total Devices", devices.Count.ToString());
                        fleetTable.AddRow("Installation Errors", errors.Count.ToString());
                        
                        var errorsByCategory = errors
                            .GroupBy(e => e.Category)
                            .OrderByDescending(g => g.Count())
                            .Take(3)
                            .ToList();
                        
                        if (errorsByCategory.Any())
                        {
                            fleetTable.AddRow("Top Error Categories", 
                                string.Join(", ", errorsByCategory.Select(g => $"{g.Key} ({g.Count()})")));
                        }
                        
                        AnsiConsole.Write(fleetTable);
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Failed to connect to ReportMate:[/] {ex.Message}");
                    }
                });
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]⚠ ReportMate not configured - fleet monitoring unavailable[/]");
            AnsiConsole.MarkupLine("[dim]Set REPORTMATE_PASSPHRASE environment variable or add to ~/.fleetmate/.env[/]");
        }
        
        Console.WriteLine();
        AnsiConsole.MarkupLine("[dim]Commands: errors, troubleshoot, device, test, lint, validate[/]");
        AnsiConsole.MarkupLine("[dim]Run 'fleetmate --help' for usage information[/]");
    }
}
