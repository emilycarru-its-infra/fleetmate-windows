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
    public static Command Create(FleetMateConfig config, ReportMateService reportMate, SnipeService? snipe = null)
    {
        var command = new Command("status", "Show FleetMate status and configuration");
        
        command.SetHandler(async () =>
        {
            await ExecuteAsync(config, reportMate, snipe);
        });
        
        return command;
    }
    
    private static async Task ExecuteAsync(FleetMateConfig config, ReportMateService reportMate, SnipeService? snipe)
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
        configTable.AddRow("ReportMate Auth", config.ReportMateUsesOidc
            ? "[green]Entra SSO[/] [dim](secretless)[/]"
            : "[yellow]Legacy passphrase[/]");
        
        configTable.AddRow("Snipe-IT URL", string.IsNullOrEmpty(config.SnipeUrl) ? "[dim](not configured)[/]" : Markup.Escape(config.SnipeUrl));
        configTable.AddRow("Snipe-IT Auth", config.SnipeUsesOidc
            ? "[green]Entra SSO[/] [dim](secretless)[/]"
            : "[yellow]No SnipeOidcAudience set[/]");
        
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
        
        // ReportMate status. A URL is enough to try now — auth is the operator's
        // Entra session, so there is no credential to wait for.
        if (!string.IsNullOrEmpty(config.ReportMateUrl))
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
        await WriteSnipeAsync(snipe);
        
        Console.WriteLine();
        AnsiConsole.MarkupLine("[dim]Commands: errors, troubleshoot, device, test, lint, validate[/]");
        AnsiConsole.MarkupLine("[dim]Run 'fleetmate --help' for usage information[/]");
    }

    /// <summary>
    /// The Snipe-IT section, as on the Mac: connected with its counts, or
    /// disconnected with the reason, or not configured. A failed sign-in used
    /// to leave no Snipe-IT line at all.
    /// </summary>
    private static async Task WriteSnipeAsync(SnipeService? snipe)
    {
        if (snipe == null)
        {
            AnsiConsole.MarkupLine("[yellow]⚠ Snipe-IT not configured - set SnipeUrl and SnipeOidcAudience[/]");
            return;
        }

        var summary = await AnsiConsole.Status()
            .StartAsync("Checking Snipe-IT connection...", _ => SnipeStatus.CheckAsync(snipe));

        var table = new Table();
        table.Border = TableBorder.Rounded;
        table.Title = new TableTitle("[cyan]Snipe-IT[/]");
        table.AddColumn("Metric");
        table.AddColumn("Value");
        if (summary.Error is { } error)
        {
            table.AddRow("Status", "[red]Disconnected[/]");
            table.AddRow("Error", Markup.Escape(error));
            Environment.ExitCode = 1;
        }
        else
        {
            table.AddRow("Status", "[green]Connected[/]");
            table.AddRow("Total Assets", summary.TotalAssets.ToString());
            table.AddRow("Deployed", summary.Deployed.ToString());
            table.AddRow("Ready", summary.Ready.ToString());
            table.AddRow("Archived", summary.Archived.ToString());
            table.AddRow("Locations", summary.Locations.ToString());
        }
        AnsiConsole.Write(table);
    }
}

/// <summary>What <c>fleetmate status</c> reports for Snipe-IT.</summary>
public sealed record SnipeStatus(int TotalAssets, int Deployed, int Ready, int Archived, int Locations, string? Error)
{
    public static async Task<SnipeStatus> CheckAsync(SnipeService snipe)
    {
        try
        {
            var assets = await snipe.GetAssetsAsync();
            snipe.ClearLastError();
            var locations = await snipe.GetLocationsAsync();
            if (snipe.LastError is { } failed) return Failed(failed);
            return From(assets, locations.Count);
        }
        catch (SnipeException ex)
        {
            return Failed(ex.Message);
        }
    }

    public static SnipeStatus Failed(string reason) => new(0, 0, 0, 0, 0, reason);

    /// <summary>Counts by the status label's meta, the way the Mac groups them.</summary>
    public static SnipeStatus From(IReadOnlyCollection<FleetMate.Core.Models.Inventory.SnipeAsset> assets, int locations) => new(
        assets.Count,
        assets.Count(a => a.StatusLabel?.StatusMeta == "deployed"),
        assets.Count(a => a.StatusLabel?.StatusMeta == "deployable"),
        assets.Count(a => a.StatusLabel?.StatusMeta == "archived"),
        locations,
        null);
}
