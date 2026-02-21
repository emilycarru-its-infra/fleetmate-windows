using System.CommandLine;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Services.Tickets;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Reporting;
using Spectre.Console;

namespace FleetMate.Commands.Devices;

public static class DeviceCommand
{
    public static Command Create(ReportMateService reportMate)
    {
        var command = new Command("device", "Look up device information");
        
        var queryArg = new Argument<string>(
            name: "query",
            description: "Device serial, hostname, asset tag, or owner name");
        
        var installsOption = new Option<bool>(
            aliases: new[] { "--installs", "-i" },
            description: "Show install status for this device");
        
        var errorsOption = new Option<bool>(
            aliases: new[] { "--errors", "-e" },
            description: "Show only failed installs");
        
        command.AddArgument(queryArg);
        command.AddOption(installsOption);
        command.AddOption(errorsOption);
        
        command.SetHandler(async (query, showInstalls, showErrors) =>
        {
            await ExecuteAsync(reportMate, query, showInstalls, showErrors);
        }, queryArg, installsOption, errorsOption);
        
        return command;
    }
    
    private static async Task ExecuteAsync(
        ReportMateService reportMate,
        string query,
        bool showInstalls,
        bool showErrors)
    {
        var device = await reportMate.FindDeviceAsync(query);
        
        if (device == null)
        {
            AnsiConsole.MarkupLine($"[yellow]No device found for:[/] {query}");
            return;
        }
        
        // Device info
        var table = new Table();
        table.Border = TableBorder.Rounded;
        table.AddColumn("Property");
        table.AddColumn("Value");
        
        table.AddRow("Name", device.DisplayName);
        table.AddRow("Serial", device.SerialNumber);
        if (!string.IsNullOrEmpty(device.Owner)) table.AddRow("Owner", device.Owner);
        if (!string.IsNullOrEmpty(device.AssetTag)) table.AddRow("Asset Tag", device.AssetTag);
        if (!string.IsNullOrEmpty(device.Location)) table.AddRow("Location", device.Location);
        if (!string.IsNullOrEmpty(device.Catalog)) table.AddRow("Catalog", device.Catalog);
        table.AddRow("Last Seen", device.LastSeen?.ToString("g") ?? "Unknown");
        
        if (!string.IsNullOrEmpty(device.Manufacturer)) table.AddRow("Manufacturer", device.Manufacturer);
        if (!string.IsNullOrEmpty(device.Model)) table.AddRow("Model", device.Model);
        if (!string.IsNullOrEmpty(device.Architecture)) table.AddRow("Architecture", device.Architecture);
        if (!string.IsNullOrEmpty(device.OsVersion)) table.AddRow("OS Version", device.OsVersion);
        if (!string.IsNullOrEmpty(device.IpAddress)) table.AddRow("IP Address", device.IpAddress);
        if (!string.IsNullOrEmpty(device.CimianVersion)) table.AddRow("Cimian Version", device.CimianVersion);
        
        AnsiConsole.Write(table);
        
        // Show installs if requested
        if (showInstalls || showErrors)
        {
            Console.WriteLine();
            var installs = await reportMate.GetDeviceInstallsAsync(device.SerialNumber);
            
            if (showErrors)
            {
                installs = installs.Where(i => i.IsError).ToList();
            }
            
            if (installs.Count == 0)
            {
                AnsiConsole.MarkupLine(showErrors 
                    ? "[green]No installation errors![/]" 
                    : "[dim]No install records found[/]");
                return;
            }
            
            var title = showErrors ? "Failed Installs" : "Install Status";
            AnsiConsole.Write(new Rule($"[cyan]{title}[/]").LeftJustified());
            
            var installTable = new Table();
            installTable.Border = TableBorder.Simple;
            installTable.AddColumn("Item");
            installTable.AddColumn("Status");
            installTable.AddColumn("Installed");
            installTable.AddColumn("Latest");
            
            foreach (var install in installs.OrderBy(i => i.ItemName))
            {
                var statusColor = install.CurrentStatus?.ToLowerInvariant() switch
                {
                    "installed" => "green",
                    "failed" or "error" => "red",
                    "pending" => "yellow",
                    _ => "dim"
                };
                
                installTable.AddRow(
                    install.ItemName,
                    $"[{statusColor}]{install.CurrentStatus}[/]",
                    install.InstalledVersion ?? "-",
                    install.LatestVersion);
            }
            
            AnsiConsole.Write(installTable);
        }
    }
}
